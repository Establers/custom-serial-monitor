[CmdletBinding()]
param(
    [ValidateSet('Offline','Live')][string]$Mode = 'Offline',
    [ValidatePattern('^build(?:-[A-Za-z0-9_-]+)?$')][string]$BuildDirectory = 'build-rtu-server',
    [string]$Port = 'COM3',
    [string]$EvidenceDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$labRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $labRoot 'tests/ModbusUartCheck/ModbusUartCheck.csproj'
$build = Join-Path $labRoot $BuildDirectory
if ($Mode -eq 'Offline') { $arguments = @('server-offline',$build) }
else {
    if (-not $EvidenceDirectory) {
        $EvidenceDirectory = Join-Path $labRoot ('results/rtu-server-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
    }
    $arguments = @('server-live',$Port,(Join-Path $build 'manifest.json'),$EvidenceDirectory)
}
# Live opens the port directly: close only that GUI connection first. No flash,
# mode switch, automatic resend, or connection replacement is performed.
dotnet run --project $project -c Release -- @arguments
if ($LASTEXITCODE -ne 0) { throw "RTU server check failed ($LASTEXITCODE). Inspect evidence before further TX." }
