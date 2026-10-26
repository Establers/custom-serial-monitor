[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param(
    [ValidateSet('All', 'Codex', 'Claude', 'Copilot')][string[]]$Client = @('All'),
    [ValidateSet('Project', 'User')][string]$Scope = 'Project',
    [string]$RootDirectory,
    [string]$CliPath,
    [string]$CodexSkillDirectory,
    [string]$ClaudeSkillDirectory,
    [string]$CopilotSkillDirectory,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SkillInstall.Common.ps1')
$sourceRoot = Get-SkillAbsolutePath (Join-Path $PSScriptRoot 'serial-monitor-control')
$explicitRoot = -not [string]::IsNullOrWhiteSpace($RootDirectory)
if (-not $RootDirectory) {
    if ($Scope -eq 'User') { $RootDirectory = [Environment]::GetFolderPath('UserProfile') }
    else { $RootDirectory = (Get-Location).ProviderPath }
}
$targetRoot = [IO.Path]::GetFullPath($RootDirectory)
$resolvedCli = $null
if ($CliPath) {
    $resolvedCli = [IO.Path]::GetFullPath($CliPath)
    if (-not [IO.File]::Exists($resolvedCli) -or [IO.Path]::GetExtension($resolvedCli) -ne '.exe') { throw 'Use an existing serialctl.exe path.' }
}
else {
    try { $resolvedCli = & (Join-Path $sourceRoot 'scripts\Find-SerialCtl.ps1') }
    catch { Write-Warning 'Installing without a pinned CLI path. Set SERIAL_MONITOR_CLI later or use configured local MCP.' }
}

$clients = if ($Client -contains 'All') { @('Codex', 'Claude', 'Copilot') } else { @($Client | Select-Object -Unique) }
$destinations = @()
foreach ($item in $clients) {
    $override = switch ($item) { 'Codex' { $CodexSkillDirectory } 'Claude' { $ClaudeSkillDirectory } 'Copilot' { $CopilotSkillDirectory } }
    if ($override) { $path = Get-SkillAbsolutePath $override }
    else {
        $folder = switch ($item) {
            'Codex' { '.agents\skills' }
            'Claude' { '.claude\skills' }
            'Copilot' { if ($Scope -eq 'User') { '.copilot\skills' } else { '.github\skills' } }
        }
        $path = [IO.Path]::Combine($targetRoot, $folder, 'serial-monitor-control')
        if ($Scope -eq 'User' -and -not $explicitRoot) {
            $configRoot = switch ($item) { 'Codex' { $env:CODEX_HOME } 'Claude' { $env:CLAUDE_CONFIG_DIR } 'Copilot' { $env:COPILOT_HOME } }
            if (-not [string]::IsNullOrWhiteSpace($configRoot)) { $path = [IO.Path]::Combine([IO.Path]::GetFullPath($configRoot), 'skills', 'serial-monitor-control') }
        }
    }
    $path = Assert-SkillDestination $path $sourceRoot
    if ($PSCmdlet.ShouldProcess($path, "Install serial-monitor-control for $item")) {
        $destinations += [PSCustomObject]@{ client = $item; skillDirectory = $path }
    }
}
if (-not $destinations.Count) { return }
$result = Invoke-SkillInstallTransaction -SourceRoot $sourceRoot -CliPath $resolvedCli -Install $destinations -Force:$Force
foreach ($installed in $result.installed) {
    Write-Output ([PSCustomObject]@{ Client = $installed.client; Scope = $Scope; SkillPath = $installed.skillDirectory; CliPath = $resolvedCli })
}
