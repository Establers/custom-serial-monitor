[CmdletBinding()]
param([string]$CliPath)

$ErrorActionPreference = 'Stop'

function Resolve-Executable([string]$Candidate) {
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
