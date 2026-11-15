[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('COM3')][string]$Port,
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
    [Parameter(Mandatory=$true)][string]$AppDirectory,
    [string]$Manifest=(Join-Path $PSScriptRoot '../build-simulator-master/manifest.json')
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'EvidenceDirectory must be new. Close the GUI COM3 connection first.' }
$appRoot=(Resolve-Path -LiteralPath $AppDirectory).Path
$project=Join-Path $PSScriptRoot '../tests/SimulatorCheck/SimulatorCheck.csproj'
$runner=Join-Path $PSScriptRoot '../tests/SimulatorCheck/bin/pinned-live'
$expected=@{}
foreach ($name in @('SerialMonitor.WinUI.dll','SerialMonitor.Core.dll')) {
    $expected[$name]=(Get-FileHash -LiteralPath (Join-Path $appRoot $name) -Algorithm SHA256).Hash
}
# Reference the selected existing DLLs; never rebuild an in-use app or another chat's sources.
dotnet build $project -c Release -o $runner "-p:SerialMonitorAppDirectory=$appRoot"
if ($LASTEXITCODE -ne 0) { throw 'Checker build failed; no device access.' }
foreach ($name in $expected.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $runner $name) -Algorithm SHA256).Hash -ne $expected[$name]) {
        throw "Pinned application assembly mismatch: $name; no device access."
    }
}
dotnet (Join-Path $runner 'SimulatorCheck.dll') live $Port $EvidenceDirectory $Manifest
if ($LASTEXITCODE -ne 0) { throw 'Live verification failed. Read report.json before any further TX; no automatic retry.' }
$report=Get-Content -LiteralPath (Join-Path $EvidenceDirectory 'report.json') -Raw | ConvertFrom-Json
if (!$report.passed -or $report.productionSha256 -ne $expected['SerialMonitor.WinUI.dll'] -or $report.coreSha256 -ne $expected['SerialMonitor.Core.dll']) {
    throw 'Completed report does not match the selected application assemblies.'
}
