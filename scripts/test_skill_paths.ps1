[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $repo ('artifacts\skill-path-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'skills') -Destination (Join-Path $scratch 'bundle') -Recurse
$bundle = Join-Path $scratch 'bundle'
$installer = Join-Path $bundle 'Install-Skill.ps1'
$source = Join-Path $bundle 'serial-monitor-control'
. (Join-Path $bundle 'SkillInstall.Common.ps1')
$unicode = ([string][char]0xD55C) + [char]0xAE00
$special = $unicode + ' '' $literal ` [custom] ; path'
$program = Join-Path $scratch ('program ' + $special)
[IO.Directory]::CreateDirectory($program) | Out-Null
$cli = Join-Path $program 'serialctl.exe'
[IO.File]::WriteAllBytes($cli, [byte[]]@(77, 90, 0))
$newProgram = Join-Path $scratch ('different program ' + $special)
[IO.Directory]::CreateDirectory($newProgram) | Out-Null
$newCli = Join-Path $newProgram 'serialctl.exe'
[IO.File]::WriteAllBytes($newCli, [byte[]]@(77, 90, 1))
$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$passed = 0
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Hash([string]$Path) { return Get-SkillHash ([IO.File]::ReadAllBytes($Path)) }
function Expect-Failure([scriptblock]$Action) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Assert-True $failed 'Expected operation to fail.'
}

$hostParent = Join-Path $scratch ('hosts ' + $special)
$targets = @('Codex', 'Claude', 'Copilot') | ForEach-Object {
    [PSCustomObject]@{ client = $_; skillDirectory = (Join-Path $hostParent ($_ + ' exact ' + $special)) }
}
[IO.Directory]::CreateDirectory($targets[0].skillDirectory) | Out-Null
$nativeOutput = & $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $installer -Client All -CliPath $cli `
    -CodexSkillDirectory $targets[0].skillDirectory -ClaudeSkillDirectory $targets[1].skillDirectory -CopilotSkillDirectory $targets[2].skillDirectory 2>&1
Assert-True ($LASTEXITCODE -eq 0) ('Native PowerShell 5.1 custom install failed: ' + ($nativeOutput -join "`n"))
$previousSerial = $env:SERIAL_MONITOR_CLI
try {
    $env:SERIAL_MONITOR_CLI = $null
    foreach ($target in $targets) {
        Assert-True ((Read-SkillJson (Join-Path $target.skillDirectory 'serial-monitor.local.json')).cliPath -eq $cli) 'The exact CLI path was changed.'
        $resolved = & $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $target.skillDirectory 'scripts\Find-SerialCtl.ps1')
        Assert-True ($LASTEXITCODE -eq 0 -and $resolved -eq $cli) 'PowerShell 5.1 could not read a pinned Unicode CLI path.'
    }
}
finally { $env:SERIAL_MONITOR_CLI = $previousSerial }
$passed++

[IO.File]::AppendAllText((Join-Path $source 'SKILL.md'), "`nnew version`n")
$null = Invoke-SkillInstallTransaction -SourceRoot $source -CliPath $newCli -Install $targets
foreach ($target in $targets) {
    Assert-True ((Read-SkillJson (Join-Path $target.skillDirectory 'serial-monitor.local.json')).cliPath -eq $newCli) 'Explicit reinstall did not update the pinned program path.'
    Assert-True ((Hash (Join-Path $target.skillDirectory 'SKILL.md')) -eq (Hash (Join-Path $source 'SKILL.md'))) 'Explicit reinstall did not update the skill.'
}
$passed++

$edited = Join-Path $targets[0].skillDirectory 'SKILL.md'
[IO.File]::AppendAllText($edited, "`nuser edit`n")
$unchanged = Hash (Join-Path $targets[1].skillDirectory 'SKILL.md')
Expect-Failure { Invoke-SkillInstallTransaction -SourceRoot $source -CliPath $cli -Install $targets }
Assert-True ((Hash (Join-Path $targets[1].skillDirectory 'SKILL.md')) -eq $unchanged) 'One edited host partially changed another host.'
$null = Invoke-SkillInstallTransaction -SourceRoot $source -CliPath $newCli -Install $targets -Force
$passed++

$extra = Join-Path $scratch 'extra-files-host'
$extraTarget = @([PSCustomObject]@{ client = 'Codex'; skillDirectory = $extra })
[IO.File]::WriteAllText((Join-Path $source 'obsolete-owned.txt'), 'obsolete')
$null = Invoke-SkillInstallTransaction -SourceRoot $source -CliPath $cli -Install $extraTarget
[IO.File]::WriteAllText((Join-Path $extra 'my-notes.txt'), 'keep me')
[IO.File]::Delete((Join-Path $source 'obsolete-owned.txt'))
$null = Invoke-SkillInstallTransaction -SourceRoot $source -CliPath $newCli -Install $extraTarget
Assert-True ([IO.File]::ReadAllText((Join-Path $extra 'my-notes.txt')) -eq 'keep me') 'An untracked user file was lost.'
Assert-True (-not [IO.File]::Exists((Join-Path $extra 'obsolete-owned.txt'))) 'An obsolete owned file was retained.'
$passed++

$script:ownershipOriginal = ${function:Get-SkillOwnership}
$script:ownershipCalls = 0
$script:raceNote = Join-Path $extra 'my-notes.txt'
$raceBefore = Hash (Join-Path $extra 'SKILL.md')
[IO.File]::AppendAllText((Join-Path $source 'SKILL.md'), "`nrace version`n")
try {
    Set-Item -Path Function:Get-SkillOwnership -Value {
        param([string]$Destination, [hashtable]$Payload, [switch]$Force)
        $owned = & $script:ownershipOriginal $Destination $Payload -Force:$Force
        $script:ownershipCalls++
        if ($script:ownershipCalls -eq 2) { [IO.File]::WriteAllText($script:raceNote, 'edited during staging') }
        return $owned
    }
    Expect-Failure { Invoke-SkillInstallTransaction -SourceRoot $source -CliPath $cli -Install $extraTarget }
}
finally { Set-Item -Path Function:Get-SkillOwnership -Value $script:ownershipOriginal }
Assert-True ([IO.File]::ReadAllText($script:raceNote) -eq 'edited during staging') 'A concurrent user edit was lost.'
Assert-True ((Hash (Join-Path $extra 'SKILL.md')) -eq $raceBefore) 'A concurrent edit partially committed.'
$passed++

# Hold a file in the second host so its directory rename fails after the first
# host was swapped. The transaction must restore every earlier selected host.
$rollbackBefore = @{}
foreach ($target in $targets) { $rollbackBefore[$target.skillDirectory] = Get-SkillTreeSnapshot $target.skillDirectory }
$locked = [IO.File]::Open((Join-Path $targets[1].skillDirectory 'SKILL.md'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try { Expect-Failure { Invoke-SkillInstallTransaction -SourceRoot $source -CliPath $cli -Install $targets } }
finally { $locked.Dispose() }
foreach ($target in $targets) { Assert-SkillSnapshot $target.skillDirectory $rollbackBefore[$target.skillDirectory] }
$passed++

$nested = Join-Path $scratch 'nested'
Expect-Failure { Invoke-SkillInstallTransaction -SourceRoot $source -CliPath $cli -Install @(
    @{ client = 'Codex'; skillDirectory = $nested }, @{ client = 'Claude'; skillDirectory = (Join-Path $nested 'child') }) }
Assert-True (-not [IO.Directory]::Exists($nested)) 'Nested destinations were partially created.'
Expect-Failure { Invoke-SkillInstallTransaction -SourceRoot $source -CliPath $cli -Install @(@{ client = 'Codex'; skillDirectory = $source }) }
$passed++

$outside = Join-Path $scratch 'outside-junction'
$junction = Join-Path $scratch 'junction'
[IO.Directory]::CreateDirectory($outside) | Out-Null
New-Item -ItemType Junction -Path $junction -Target $outside | Out-Null
try {
    Expect-Failure { Invoke-SkillInstallTransaction -SourceRoot $source -CliPath $cli -Install @(@{ client = 'Codex'; skillDirectory = (Join-Path $junction 'custom') }) }
    Assert-True (-not [IO.Directory]::Exists((Join-Path $outside 'custom'))) 'Installation followed a junction ancestor.'
}
finally { Remove-Item -LiteralPath $junction -Force }
$passed++

$oldCodex = $env:CODEX_HOME
$oldClaude = $env:CLAUDE_CONFIG_DIR
$oldCopilot = $env:COPILOT_HOME
try {
    $env:CODEX_HOME = Join-Path $scratch 'codex config'
    $env:CLAUDE_CONFIG_DIR = Join-Path $scratch 'claude config'
    $env:COPILOT_HOME = Join-Path $scratch 'copilot config'
    $installed = @(& $installer -Client All -Scope User -CliPath $cli)
    foreach ($item in $installed) {
        $expected = switch ($item.Client) { 'Codex' { $env:CODEX_HOME } 'Claude' { $env:CLAUDE_CONFIG_DIR } 'Copilot' { $env:COPILOT_HOME } }
        Assert-True ($item.SkillPath -eq (Join-Path $expected 'skills\serial-monitor-control')) 'A custom environment root was ignored.'
    }
    $explicit = Join-Path $scratch 'explicit root'
    $installed = @(& $installer -Client All -Scope User -RootDirectory $explicit -CliPath $cli)
    Assert-True (@($installed | Where-Object { -not (Test-SkillChild $_.SkillPath $explicit) }).Count -eq 0) 'Explicit root lost precedence.'
    $exact = Join-Path $scratch ('exact override ' + $special)
    $installed = & $installer -Client Codex -Scope User -RootDirectory $explicit -CodexSkillDirectory $exact -CliPath $cli
    Assert-True ($installed.SkillPath -eq $exact) 'An exact directory override lost precedence.'
}
finally { $env:CODEX_HOME = $oldCodex; $env:CLAUDE_CONFIG_DIR = $oldClaude; $env:COPILOT_HOME = $oldCopilot }
$passed++

Write-Output "Manual skill custom-path tests passed: $passed"
