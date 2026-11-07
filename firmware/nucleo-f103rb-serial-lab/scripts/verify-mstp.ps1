[CmdletBinding()]
param(
    [ValidateSet('Offline','Live')][string]$Mode='Offline',
    [ValidatePattern('^build(?:-[A-Za-z0-9_-]+)?$')][string]$BuildDirectory='build-bacnet-mstp',
    [string]$Port='COM3',
    [string]$EvidenceDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$labRoot=Split-Path -Parent $PSScriptRoot
$project=Join-Path $labRoot 'tests/ModbusUartCheck/ModbusUartCheck.csproj'
$build=Join-Path $labRoot $BuildDirectory
if($Mode -eq 'Offline') { $arguments=@('mstp-offline',$build) }
else {
    if(-not $EvidenceDirectory) { $EvidenceDirectory=Join-Path $labRoot ('results/mstp-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')) }
    $arguments=@('mstp-live',$Port,(Join-Path $build 'manifest.json'),$EvidenceDirectory)
}
# Live owns the port: close that GUI connection first. No automatic retry/flash.
dotnet run --project $project -c Release -- @arguments
if($LASTEXITCODE -ne 0) { throw "MS/TP check failed ($LASTEXITCODE). Inspect evidence before further TX." }
