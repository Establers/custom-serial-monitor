[CmdletBinding()]
param(
    [ValidateSet('Offline','Probe','Live')][string]$Mode = 'Offline',
    [ValidatePattern('^build(?:-[A-Za-z0-9_-]+)?$')][string]$BuildDirectory = 'build-modbus-rtu',
    [string]$Port = 'COM3',
    [string]$EvidenceDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$labRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $labRoot 'tests/ModbusUartCheck/ModbusUartCheck.csproj'
$build = Join-Path $labRoot $BuildDirectory
if ($Mode -eq 'Offline') { $arguments = @('offline',$build) }
elseif ($Mode -eq 'Probe') { $arguments = @('probe',$Port) }
else {
    if (-not $EvidenceDirectory) {
        $EvidenceDirectory = Join-Path $labRoot ('results/modbus-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
    }
    $arguments = @('live',$Port,(Join-Path $build 'manifest.json'),$EvidenceDirectory)
}
# Live/Probe own the specified port. Close only the operator's connection to that
# port first; never replace another GUI connection, auto-flash, or retry TX.
dotnet run --project $project -c Release -- @arguments
if ($LASTEXITCODE -ne 0) { throw "Modbus check failed ($LASTEXITCODE). Inspect evidence before any further TX." }
