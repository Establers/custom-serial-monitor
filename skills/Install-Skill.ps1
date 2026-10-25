[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param(
    [ValidateSet('All', 'Codex', 'Claude', 'Copilot')][string[]]$Client = @('All'),
    [ValidateSet('Project', 'User')][string]$Scope = 'Project',
    [string]$RootDirectory,
    [string]$CliPath,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$skillName = 'serial-monitor-control'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $skillName))
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'SKILL.md') -PathType Leaf)) { throw 'Skill source is missing.' }
if (-not $RootDirectory) {
    if ($Scope -eq 'User') { $RootDirectory = [Environment]::GetFolderPath('UserProfile') }
    else { $RootDirectory = (Get-Location).ProviderPath }
}
$targetRoot = [IO.Path]::GetFullPath($RootDirectory)
$rootPrefix = $targetRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar

function Assert-Child([string]$Path, [string]$Parent) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Path escapes install root: $full" }
    return $full
}

function Assert-NoLinks([string]$Path) {
    if ((Test-Path -LiteralPath $Path) -and ((Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to overwrite a symbolic link: $Path"
    }
    $cursor = [IO.DirectoryInfo]::new($Path)
    while ($cursor -and ($cursor.FullName -eq $targetRoot -or $cursor.FullName.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase))) {
        if ($cursor.Exists -and ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to install through a junction or symbolic link: $($cursor.FullName)"
        }
        $cursor = $cursor.Parent
    }
}

$sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File)
if (@(Get-ChildItem -LiteralPath $sourceRoot -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
    throw 'Skill source must not contain symbolic links.'
}
$payload = @{}
foreach ($file in $sourceFiles) {
    $relative = $file.FullName.Substring($sourceRoot.TrimEnd('\', '/').Length + 1)
    $payload[$relative] = [IO.File]::ReadAllBytes($file.FullName)
}
try {
    $resolvedCli = & (Join-Path $sourceRoot 'scripts\Find-SerialCtl.ps1') -CliPath $CliPath
    $settings = @{ cliPath = $resolvedCli } | ConvertTo-Json
    $payload['serial-monitor.local.json'] = [Text.UTF8Encoding]::new($false).GetBytes($settings + "`n")
}
catch {
    if ($CliPath) { throw }
    Write-Warning 'Installing without a pinned CLI path. Set SERIAL_MONITOR_CLI later or use configured local MCP.'
}

$clients = if ($Client -contains 'All') { @('Codex', 'Claude', 'Copilot') } else { @($Client | Select-Object -Unique) }
$destinations = foreach ($item in $clients) {
    $folder = switch ($item) {
        'Codex' { '.agents\skills' }
        'Claude' { '.claude\skills' }
        'Copilot' { if ($Scope -eq 'User') { '.copilot\skills' } else { '.github\skills' } }
    }
    $destination = Assert-Child (Join-Path (Join-Path $targetRoot $folder) $skillName) $targetRoot
    if ($destination -eq $sourceRoot) { throw 'Install destination cannot be the source skill.' }
    Assert-NoLinks $destination
    [PSCustomObject]@{ Client = $item; Path = $destination }
}

# Validate every target before copying any files. Never silently replace a
# separately maintained skill or local modifications to a managed installation.
foreach ($destination in $destinations) {
    if (-not (Test-Path -LiteralPath $destination.Path)) { continue }
    $marker = Join-Path $destination.Path '.serial-monitor-skill.json'
    $managed = $null
    if (Test-Path -LiteralPath $marker -PathType Leaf) {
        try { $managed = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json }
        catch { if (-not $Force) { throw } }
    }
    if (-not $Force) {
        if (-not $managed -or $managed.skill -ne $skillName) { throw "Unmanaged skill already exists: $($destination.Path). Use -Force only to replace it deliberately." }
        foreach ($entry in $managed.files.PSObject.Properties) {
            $installed = Assert-Child (Join-Path $destination.Path $entry.Name) $destination.Path
            Assert-NoLinks $installed
            if (-not (Test-Path -LiteralPath $installed -PathType Leaf) -or (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -ne $entry.Value) {
                throw "Installed skill was edited: $installed. Preserve your edits or explicitly use -Force."
            }
        }
        foreach ($relative in $payload.Keys) {
            $installed = Assert-Child (Join-Path $destination.Path $relative) $destination.Path
            if ((Test-Path -LiteralPath $installed) -and -not $managed.files.PSObject.Properties[$relative]) {
                throw "Incoming file would replace an untracked file: $installed. Use -Force only deliberately."
            }
        }
    }
}

foreach ($destination in $destinations) {
    if (-not $PSCmdlet.ShouldProcess($destination.Path, "Install $skillName for $($destination.Client)")) { continue }
    New-Item -ItemType Directory -Force -Path $destination.Path | Out-Null
    $hashes = @{}
    foreach ($relative in $payload.Keys) {
        $installed = Assert-Child (Join-Path $destination.Path $relative) $destination.Path
        Assert-NoLinks $installed
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $installed) | Out-Null
        [IO.File]::WriteAllBytes($installed, $payload[$relative])
        $hashes[$relative] = (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash
    }
    $manifest = @{ skill = $skillName; files = $hashes } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Join-Path $destination.Path '.serial-monitor-skill.json'), $manifest, [Text.UTF8Encoding]::new($false))
    Write-Output ([PSCustomObject]@{ Client = $destination.Client; Scope = $Scope; SkillPath = $destination.Path; CliPath = $resolvedCli })
}
