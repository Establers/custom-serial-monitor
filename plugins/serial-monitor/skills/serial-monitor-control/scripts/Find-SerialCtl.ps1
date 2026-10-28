[CmdletBinding()]
param([string]$CliPath)

$ErrorActionPreference = 'Stop'

function Resolve-Executable([string]$Candidate) {
    if (-not [IO.Path]::IsPathRooted($Candidate)) {
        throw 'Use an absolute path to serialctl.exe.'
    }
    $resolved = (Resolve-Path -LiteralPath $Candidate -ErrorAction Stop).ProviderPath
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf) -or [IO.Path]::GetExtension($resolved) -ne '.exe') {
        throw 'Use the path to serialctl.exe.'
    }
    return $resolved
}

if ($CliPath) { return Resolve-Executable $CliPath }
if ($env:SERIAL_MONITOR_CLI) { return Resolve-Executable $env:SERIAL_MONITOR_CLI }
$localSettings = Join-Path (Split-Path -Parent $PSScriptRoot) 'serial-monitor.local.json'
if (Test-Path -LiteralPath $localSettings -PathType Leaf) {
    $settings = [IO.File]::ReadAllText($localSettings, [Text.UTF8Encoding]::new($false, $true)) | ConvertFrom-Json
    if ($settings.cliPath) { return Resolve-Executable $settings.cliPath }
}
# A portable or installed marketplace points at its own nearby application first.
$ancestor = [IO.DirectoryInfo]::new($PSScriptRoot)
for ($depth = 0; $depth -lt 10 -and $ancestor; $depth++) {
    $candidate = Join-Path $ancestor.FullName 'tools\serialctl.exe'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { return Resolve-Executable $candidate }
    $ancestor = $ancestor.Parent
}

# AI hosts can copy a local plugin to a cache outside the portable application.
# Recover that application's CLI from a running GUI, without selecting its COM
# connection or sending any operation. Several GUI instances from one app folder
# are fine; different application folders require an explicit binary selection.
$runningCandidates = @()
foreach ($process in @(Get-Process -Name 'SerialMonitor.WinUI' -ErrorAction SilentlyContinue)) {
    try { $applicationPath = $process.Path } catch { continue }
    if (-not $applicationPath) { continue }
    $candidate = Join-Path (Split-Path -Parent $applicationPath) 'tools\serialctl.exe'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        $runningCandidates += Resolve-Executable $candidate
    }
}
$runningCandidates = @($runningCandidates | Sort-Object -Unique)
if ($runningCandidates.Count -eq 1) { return $runningCandidates[0] }
if ($runningCandidates.Count -gt 1) {
    throw 'Several running Serial Monitor application folders were found. Set SERIAL_MONITOR_CLI to the intended tools\serialctl.exe path and restart the AI host.'
}

# Inno Setup records custom install directories under this fixed app identity.
# Discovery is read-only and does not alter registry or AI host configuration.
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{BA846EA7-2A2B-46B6-A501-3D79E6C4908B}_is1'
foreach ($registryRoot in @('HKCU:', 'HKLM:', 'HKLM:\Software\WOW6432Node')) {
    $key = if ($registryRoot -like '*WOW6432Node') {
        Join-Path $registryRoot ($uninstallKey.Substring('Software\'.Length))
    } else { Join-Path $registryRoot $uninstallKey }
    $registration = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
    if ($registration -and $registration.InstallLocation) {
        $candidate = Join-Path $registration.InstallLocation 'tools\serialctl.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return Resolve-Executable $candidate }
    }
}
if ($env:LOCALAPPDATA) {
    $candidate = Join-Path $env:LOCALAPPDATA 'Programs\SerialMonitor\tools\serialctl.exe'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { return Resolve-Executable $candidate }
}
$onPath = Get-Command serialctl.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($onPath) { return Resolve-Executable $onPath.Source }

foreach ($start in @($PSScriptRoot, (Get-Location).ProviderPath)) {
    $ancestor = [IO.DirectoryInfo]::new($start)
    for ($depth = 0; $depth -lt 10 -and $ancestor; $depth++) {
        foreach ($relative in @('tools\serialctl.exe', 'artifacts\serialctl\serialctl.exe',
            'SerialMonitor.Cli\bin\Debug\net8.0\serialctl.exe', 'SerialMonitor.Cli\bin\Release\net8.0\serialctl.exe')) {
            $candidate = Join-Path $ancestor.FullName $relative
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return Resolve-Executable $candidate }
        }
        $ancestor = $ancestor.Parent
    }
}
throw 'serialctl.exe not found. Supply -CliPath, set SERIAL_MONITOR_CLI, install with -CliPath, or use an already configured local MCP server.'
