$ErrorActionPreference = 'Stop'
$script:SkillInstallName = 'serial-monitor-control'
$script:SkillInstallUtf8 = [Text.UTF8Encoding]::new($false, $true)

function Get-SkillAbsolutePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+)') {
        throw "Use an absolute Windows path: $Path"
    }
    $full = [IO.Path]::GetFullPath($Path)
    $trimmed = $full.TrimEnd('\', '/')
    if ($trimmed -eq [IO.Path]::GetPathRoot($full).TrimEnd('\', '/')) { return [IO.Path]::GetPathRoot($full) }
    return $trimmed
}

function Assert-SkillNoLinks([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if ([IO.File]::Exists($cursor) -or [IO.Directory]::Exists($cursor)) {
            if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing a junction or symbolic link: $cursor"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}

function Test-SkillChild([string]$Path, [string]$Parent) {
    $prefix = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    return [IO.Path]::GetFullPath($Path).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Get-SkillChild([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':')) {
        throw "Invalid relative skill path: $Relative"
    }
    $path = [IO.Path]::GetFullPath([IO.Path]::Combine($Root, $Relative))
    if (-not (Test-SkillChild $path $Root)) { throw "Skill path escapes destination: $Relative" }
    return $path
}

function Get-SkillTreeFiles([string]$Root) {
    Assert-SkillNoLinks $Root
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($Root)
    $count = 0
    [long]$bytes = 0
    while ($pending.Count) {
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($pending.Dequeue())) {
            $count++
            if ($count -gt 1024) { throw 'Skill directory contains too many entries.' }
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Skill contains a symbolic link: $entry" }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { $pending.Enqueue($entry); continue }
            $file = [IO.FileInfo]::new($entry)
            $bytes += $file.Length
            if ($bytes -gt 8MB) { throw 'Skill directory exceeds the 8 MiB installation limit.' }
            Write-Output $file
        }
    }
}

function Read-SkillJson([string]$Path, [int]$MaxBytes = 131072) {
    Assert-SkillNoLinks $Path
    if ([IO.FileInfo]::new($Path).Length -gt $MaxBytes) { throw "JSON file exceeds its size limit: $Path" }
    return [IO.File]::ReadAllText($Path, $script:SkillInstallUtf8) | ConvertFrom-Json
}

function Get-SkillHash([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Get-SkillTreeSnapshot([string]$Root) {
    $snapshot = @{}
    if (-not [IO.Directory]::Exists($Root)) { return $snapshot }
    foreach ($file in @(Get-SkillTreeFiles $Root)) {
        $relative = $file.FullName.Substring($Root.Length + 1)
        $snapshot[$relative] = Get-SkillHash ([IO.File]::ReadAllBytes($file.FullName))
    }
    return $snapshot
}

function Assert-SkillSnapshot([string]$Root, [hashtable]$Expected) {
    $current = Get-SkillTreeSnapshot $Root
    if ($current.Count -ne $Expected.Count) { throw 'Skill files changed during staging; the installation was rolled back.' }
    foreach ($relative in $Expected.Keys) {
        if (-not $current.ContainsKey($relative) -or $current[$relative] -ne $Expected[$relative]) {
            throw 'Skill contents changed during staging, including a local file; the installation was rolled back.'
        }
    }
}

function Get-SkillPayload([string]$SourceRoot, [string]$CliPath) {
    $payload = @{}
    foreach ($file in @(Get-SkillTreeFiles $SourceRoot)) {
        $relative = $file.FullName.Substring($SourceRoot.TrimEnd('\', '/').Length + 1)
        if ($relative -in @('serial-monitor.local.json', '.serial-monitor-skill.json')) { throw 'The source skill contains machine-specific installation files.' }
        $payload[$relative] = [IO.File]::ReadAllBytes($file.FullName)
    }
    if (-not $payload.ContainsKey('SKILL.md')) { throw 'Skill source is missing SKILL.md.' }
    if ($CliPath) {
        $cli = Get-SkillAbsolutePath $CliPath
        if (-not [IO.File]::Exists($cli) -or [IO.Path]::GetExtension($cli) -ne '.exe') { throw "serialctl.exe does not exist: $cli" }
        $payload['serial-monitor.local.json'] = $script:SkillInstallUtf8.GetBytes((@{ cliPath = $cli } | ConvertTo-Json) + "`n")
    }
    if ($payload.Count -gt 512) { throw 'The skill payload contains more than 512 files.' }
    return $payload
}

function Assert-SkillDestination([string]$Destination, [string]$SourceRoot) {
    $destinationPath = Get-SkillAbsolutePath $Destination
    if ($destinationPath.TrimEnd('\', '/') -eq [IO.Path]::GetPathRoot($destinationPath).TrimEnd('\', '/')) { throw 'A drive or share root cannot be a skill destination.' }
    if ($destinationPath -eq $SourceRoot -or (Test-SkillChild $destinationPath $SourceRoot) -or (Test-SkillChild $SourceRoot $destinationPath)) {
        throw 'The source and destination skill directories must not overlap.'
    }
    Assert-SkillNoLinks $destinationPath
    if ([IO.File]::Exists($destinationPath)) { throw "Skill destination is a file: $destinationPath" }
    return $destinationPath
}

function Get-SkillOwnership([string]$Destination, [hashtable]$Payload, [switch]$Force) {
    if (-not [IO.Directory]::Exists($Destination)) { return $null }
    $files = @(Get-SkillTreeFiles $Destination)
    if (-not $files.Count) { return $null }
    $marker = [IO.Path]::Combine($Destination, '.serial-monitor-skill.json')
    $managed = $null
    if ([IO.File]::Exists($marker)) {
        try { $managed = Read-SkillJson $marker }
        catch { if (-not $Force) { throw } }
    }
    if ($Force) { return $managed }
    if (-not $managed -or $managed.skill -ne $script:SkillInstallName -or -not $managed.files) {
        throw "Unmanaged skill already exists: $Destination. Choose an empty folder or a managed installation; existing files were preserved."
    }
    $entries = @($managed.files.PSObject.Properties)
    if (-not $entries.Count -or $entries.Count -gt 512) { throw "Invalid installation manifest: $marker" }
    foreach ($entry in $entries) {
        if ($entry.Value -isnot [string] -or $entry.Value -notmatch '^[A-Fa-f0-9]{64}$') { throw "Invalid installation hash: $marker" }
        $path = Get-SkillChild $Destination $entry.Name
        Assert-SkillNoLinks $path
        if (-not [IO.File]::Exists($path) -or (Get-SkillHash ([IO.File]::ReadAllBytes($path))) -ne $entry.Value) {
            throw "Installed skill was edited: $path. Preserve your edits or reinstall deliberately with -Force."
        }
    }
    foreach ($relative in $Payload.Keys) {
        $path = Get-SkillChild $Destination $relative
        if (([IO.File]::Exists($path) -or [IO.Directory]::Exists($path)) -and -not $managed.files.PSObject.Properties[$relative]) {
            throw "An incoming file would replace an untracked local file: $path. It was preserved."
        }
    }
    return $managed
}

function Remove-SkillOwnedStage([string]$Path, [string]$Parent, [string]$OperationId) {
    if (-not $Path -or -not [IO.Directory]::Exists($Path)) { return }
    if (-not (Test-SkillChild $Path $Parent) -or [IO.Path]::GetFileName($Path) -notlike ('*' + $OperationId)) { throw 'Unsafe staging cleanup was refused.' }
    $null = @(Get-SkillTreeFiles $Path)
    [IO.Directory]::Delete($Path, $true)
}

function Invoke-SkillInstallTransaction {
    param([string]$SourceRoot, [string]$CliPath, [object[]]$Install, [switch]$Force)
    $source = Get-SkillAbsolutePath $SourceRoot
    $payload = Get-SkillPayload $source $CliPath
    $targets = @()
    $seen = @{}
    foreach ($item in @($Install)) {
        if ($item.client -notin @('Codex', 'Claude', 'Copilot')) { throw 'Invalid selected skill client.' }
        $path = Assert-SkillDestination $item.skillDirectory $source
        if ($seen.ContainsKey($path)) { throw 'Two selected clients cannot share one destination folder.' }
        $seen[$path] = $true
        $targets += [PSCustomObject]@{ client = $item.client; skillDirectory = $path }
    }
    if ($targets.Count -lt 1 -or $targets.Count -gt 3) { throw 'Select between one and three skill clients.' }
    for ($first = 0; $first -lt $targets.Count; $first++) {
        for ($second = $first + 1; $second -lt $targets.Count; $second++) {
            if ((Test-SkillChild $targets[$first].skillDirectory $targets[$second].skillDirectory) -or
                (Test-SkillChild $targets[$second].skillDirectory $targets[$first].skillDirectory)) {
                throw 'Skill destinations must not contain another selected destination.'
            }
        }
    }

    $staged = @()
    try {
        # Validate every selected host before creating a staging directory.
        foreach ($target in $targets) {
            $path = $target.skillDirectory
            $managed = Get-SkillOwnership $path $payload -Force:$Force
            $staged += [PSCustomObject]@{
                target = $target; managed = $managed; path = $path
                originalExists = [IO.Directory]::Exists($path)
                originalFiles = (Get-SkillTreeSnapshot $path)
                stagedFiles = $null; stage = $null; backup = $null
                stageId = [Guid]::NewGuid().ToString('N')
                moved = $false; committed = $false; recoveryFailed = $false
            }
        }
        foreach ($entry in $staged) {
            $parent = [IO.Path]::GetDirectoryName($entry.path)
            Assert-SkillNoLinks $parent
            [IO.Directory]::CreateDirectory($parent) | Out-Null
            $entry.stage = [IO.Path]::Combine($parent, '.serial-monitor-skill-stage-' + $entry.stageId)
            $entry.backup = [IO.Path]::Combine($parent, '.serial-monitor-skill-backup-' + $entry.stageId)
            if ([IO.Directory]::Exists($entry.stage) -or [IO.Directory]::Exists($entry.backup)) { throw 'An installation staging path already exists.' }
            [IO.Directory]::CreateDirectory($entry.stage) | Out-Null
            if ([IO.Directory]::Exists($entry.path)) {
                foreach ($file in @(Get-SkillTreeFiles $entry.path)) {
                    $relative = $file.FullName.Substring($entry.path.Length + 1)
                    $copy = Get-SkillChild $entry.stage $relative
                    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copy)) | Out-Null
                    [IO.File]::WriteAllBytes($copy, [IO.File]::ReadAllBytes($file.FullName))
                }
            }
            if ($entry.managed -and $entry.managed.files) {
                foreach ($old in $entry.managed.files.PSObject.Properties) {
                    if ($payload.ContainsKey($old.Name)) { continue }
                    $obsolete = Get-SkillChild $entry.stage $old.Name
                    if ([IO.File]::Exists($obsolete)) { [IO.File]::Delete($obsolete) }
                }
            }
            $hashes = @{}
            foreach ($relative in $payload.Keys) {
                $copy = Get-SkillChild $entry.stage $relative
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copy)) | Out-Null
                [IO.File]::WriteAllBytes($copy, $payload[$relative])
                $hashes[$relative] = Get-SkillHash $payload[$relative]
            }
            [IO.File]::WriteAllText([IO.Path]::Combine($entry.stage, '.serial-monitor-skill.json'),
                (@{ skill = $script:SkillInstallName; files = $hashes } | ConvertTo-Json -Depth 5), $script:SkillInstallUtf8)
            $entry.stagedFiles = Get-SkillTreeSnapshot $entry.stage
        }
        foreach ($entry in $staged) { $null = Get-SkillOwnership $entry.path $payload -Force:$Force }
        foreach ($entry in $staged) {
            if ([IO.Directory]::Exists($entry.path) -ne $entry.originalExists) { throw 'A skill destination appeared or disappeared during staging.' }
            Assert-SkillSnapshot $entry.path $entry.originalFiles
        }
        foreach ($entry in $staged) {
            Assert-SkillNoLinks $entry.path
            if ([IO.Directory]::Exists($entry.path)) {
                [IO.Directory]::Move($entry.path, $entry.backup)
                $entry.moved = $true
                # Recheck after rename to preserve edits made during staging.
                # Windows disallows directory moves while child handles are open.
                Assert-SkillSnapshot $entry.backup $entry.originalFiles
            }
            [IO.Directory]::Move($entry.stage, $entry.path)
            $entry.committed = $true
        }
        $installed = @()
        foreach ($entry in $staged) {
            $installed += [PSCustomObject]@{ client = $entry.target.client; skillDirectory = $entry.path }
            try { Remove-SkillOwnedStage $entry.backup ([IO.Path]::GetDirectoryName($entry.path)) $entry.stageId }
            catch { Write-Warning ("Installation committed; its recoverable backup could not be removed: " + $entry.backup) }
        }
        return [PSCustomObject]@{ installed = $installed }
    }
    catch {
        $originalError = $_.Exception.Message
        $rollbackErrors = @()
        for ($index = $staged.Count - 1; $index -ge 0; $index--) {
            $entry = $staged[$index]
            try {
                if ($entry.committed -and [IO.Directory]::Exists($entry.path)) {
                    Assert-SkillNoLinks $entry.path
                    # A newly edited live file is retained for recovery instead
                    # of being deleted by rollback cleanup.
                    Assert-SkillSnapshot $entry.path $entry.stagedFiles
                    [IO.Directory]::Move($entry.path, $entry.stage)
                    $entry.committed = $false
                }
                if ($entry.moved -and [IO.Directory]::Exists($entry.backup)) {
                    [IO.Directory]::Move($entry.backup, $entry.path)
                    $entry.moved = $false
                }
            }
            catch {
                $entry.recoveryFailed = $true
                $rollbackErrors += $_.Exception.Message + '; recovery files: ' + $entry.path + ', ' + $entry.stage + ', ' + $entry.backup
            }
        }
        if ($rollbackErrors.Count) { throw ($originalError + ' Rollback requires recovery: ' + ($rollbackErrors -join ' | ')) }
        throw $originalError
    }
    finally {
        foreach ($entry in $staged) {
            if (-not $entry.recoveryFailed) { Remove-SkillOwnedStage $entry.stage ([IO.Path]::GetDirectoryName($entry.path)) $entry.stageId }
        }
    }
}
