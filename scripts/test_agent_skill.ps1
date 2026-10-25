[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$installer = Join-Path $repo 'skills\Install-Skill.ps1'
$source = Join-Path $repo 'skills\serial-monitor-control'
$resolver = Join-Path $source 'scripts\Find-SerialCtl.ps1'
$cli = & $resolver
$scratch = Join-Path $repo ('artifacts\skill-tests-' + [Guid]::NewGuid().ToString('N'))
$project = Join-Path $scratch 'project with spaces'
$passed = 0
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Expect-Failure([scriptblock]$Action) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Assert-True $failed 'Expected operation to fail.'
}

& $installer -Client All -Scope Project -RootDirectory $project -CliPath $cli -WhatIf | Out-Null
Assert-True (-not (Test-Path -LiteralPath $project)) '-WhatIf created files.'
$passed++

$installed = @(& $installer -Client All -Scope Project -RootDirectory $project -CliPath $cli)
Assert-True ($installed.Count -eq 3) 'Not all hosts were installed.'
$sourceHash = (Get-FileHash -LiteralPath (Join-Path $source 'SKILL.md')).Hash
foreach ($item in $installed) {
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $item.SkillPath 'SKILL.md')).Hash -eq $sourceHash) 'Installed skill differs.'
    $found = & (Join-Path $item.SkillPath 'scripts\Find-SerialCtl.ps1')
    Assert-True ($found -eq $cli) 'Installed resolver lost CLI path.'
}
$passed++

& $installer -Client All -Scope Project -RootDirectory $project -CliPath $cli | Out-Null
$passed++

$claude = ($installed | Where-Object Client -eq 'Claude').SkillPath
$codex = ($installed | Where-Object Client -eq 'Codex').SkillPath
$codexSkill = Join-Path $codex 'SKILL.md'
$before = (Get-Item -LiteralPath $codexSkill).LastWriteTimeUtc
Add-Content -LiteralPath (Join-Path $claude 'SKILL.md') -Value 'local user modification'
Expect-Failure { & $installer -Client All -Scope Project -RootDirectory $project -CliPath $cli }
Assert-True ((Get-Item -LiteralPath $codexSkill).LastWriteTimeUtc -eq $before) 'Preflight failure partially overwrote another host.'
$passed++

& $installer -Client All -Scope Project -RootDirectory $project -CliPath $cli -Force | Out-Null
Assert-True ((Get-FileHash -LiteralPath (Join-Path $claude 'SKILL.md')).Hash -eq $sourceHash) '-Force did not restore managed files.'
$passed++

$unmanagedRoot = Join-Path $scratch 'unmanaged'
$unmanaged = Join-Path $unmanagedRoot '.claude\skills\serial-monitor-control'
New-Item -ItemType Directory -Force -Path $unmanaged | Out-Null
Set-Content -LiteralPath (Join-Path $unmanaged 'SKILL.md') -Value 'user-owned skill'
Expect-Failure { & $installer -Client Claude -Scope Project -RootDirectory $unmanagedRoot -CliPath $cli }
Assert-True ((Get-Content -LiteralPath (Join-Path $unmanaged 'SKILL.md') -Raw).Trim() -eq 'user-owned skill') 'Unmanaged skill was overwritten.'
$passed++

$personal = @(& $installer -Client All -Scope User -RootDirectory (Join-Path $scratch 'personal') -CliPath $cli)
Assert-True (($personal | Where-Object Client -eq 'Copilot').SkillPath.EndsWith('.copilot\skills\serial-monitor-control')) 'Personal Copilot destination is wrong.'
$passed++

Expect-Failure { & $resolver -CliPath (Join-Path $scratch 'missing.exe') }
$passed++

$previousOverride = $env:SERIAL_MONITOR_CLI
try {
    $env:SERIAL_MONITOR_CLI = Join-Path $scratch 'missing.exe'
    Assert-True ((& $resolver -CliPath $cli) -eq $cli) 'Explicit CLI did not override the environment.'
    Expect-Failure { & $resolver }
}
finally { $env:SERIAL_MONITOR_CLI = $previousOverride }
$passed++

$package = & (Join-Path $repo 'scripts\package_agent_skill.ps1') -OutputDirectory (Join-Path $scratch 'package')
Assert-True (Test-Path -LiteralPath $package.ChecksumPath -PathType Leaf) 'Package checksum is missing.'
$archive = [IO.Compression.ZipFile]::OpenRead($package.ZipPath)
try {
    $entries = @($archive.Entries | Where-Object Length -gt 0 | ForEach-Object FullName)
    foreach ($required in @('README.md', 'LICENSE', 'skills/Install-Skill.ps1', 'docs/agent_skills.md',
        'docs/ai_control.md', 'skills/serial-monitor-control/SKILL.md',
        'skills/serial-monitor-control/references/mcp.md', 'skills/serial-monitor-control/scripts/Find-SerialCtl.ps1')) {
        Assert-True ($entries -contains ('SerialMonitorSkills/' + $required)) "Missing ZIP entry: $required"
    }
    Assert-True (@($entries | Where-Object { $_ -match '\.(exe|dll|bin|pdb)$|serial-monitor\.local\.json|\.serial-monitor-skill\.json' }).Count -eq 0) 'Package contains binaries or machine-specific settings.'
    $zipPaths = @{}
    foreach ($entryName in $entries) { $zipPaths[[IO.Path]::GetFullPath((Join-Path $scratch $entryName))] = $true }
    foreach ($entry in $archive.Entries | Where-Object { $_.FullName.EndsWith('.md') }) {
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $markdown = $reader.ReadToEnd() } finally { $reader.Dispose() }
        foreach ($link in [regex]::Matches($markdown, '(?<!!)\[[^\]]+\]\(([^)]+)\)')) {
            $url = $link.Groups[1].Value
            if ($url -match '^https?://' -or $url -match '^#') { continue }
            $basePath = Split-Path -Parent (Join-Path $scratch $entry.FullName)
            $target = [IO.Path]::GetFullPath((Join-Path $basePath $url.Split('#', 2)[0]))
            Assert-True ($zipPaths.ContainsKey($target)) "Broken ZIP guide link: $($entry.FullName) -> $url"
        }
    }
}
finally { $archive.Dispose() }
$checksum = (Get-Content -LiteralPath $package.ChecksumPath -Raw).Trim()
Assert-True ($checksum -eq ((Get-FileHash -LiteralPath $package.ZipPath).Hash + ' *SerialMonitorSkills.zip')) 'Package checksum does not match.'
$passed++

$download = Join-Path $scratch 'download with spaces'
Expand-Archive -LiteralPath $package.ZipPath -DestinationPath $download
$downloadInstaller = Join-Path $download 'SerialMonitorSkills\skills\Install-Skill.ps1'
$receiver = @(& $downloadInstaller -Client All -Scope User -RootDirectory (Join-Path $scratch 'receiver') -CliPath $cli)
Assert-True ($receiver.Count -eq 3) 'Downloaded ZIP could not install all hosts.'
foreach ($item in $receiver) {
    Assert-True ((& (Join-Path $item.SkillPath 'scripts\Find-SerialCtl.ps1')) -eq $cli) 'Downloaded installer did not pin the separately installed CLI.'
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $item.SkillPath 'SKILL.md')).Hash -eq $sourceHash) 'Downloaded skill differs from source.'
}
$passed++
Write-Output "Agent skill installation/package tests passed: $passed"
