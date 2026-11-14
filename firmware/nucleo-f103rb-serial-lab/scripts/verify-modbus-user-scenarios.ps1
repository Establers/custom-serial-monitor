[CmdletBinding()]
param(
    [ValidateSet('Offline','Live')][string]$Mode = 'Offline',
    [string]$Port,
    [string]$Manifest,
    [string]$EvidenceDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$labRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $labRoot 'tests/ModbusMonitorCheck/ModbusMonitorCheck.csproj'
if (-not $Manifest) { $Manifest = Join-Path $labRoot 'build-boot-guide/manifest.json' }
if ($Mode -eq 'Offline') {
    $arguments = @('offline', $Manifest, (Join-Path $labRoot 'sequences/modbus/03-monitor-extended.gui.json'))
} else {
    if (-not $Port -or $Port -notmatch '^COM[1-9][0-9]*$') { throw 'Live requires an explicit -Port COMn; close only that GUI connection first.' }
    if (-not $EvidenceDirectory -or (Test-Path -LiteralPath $EvidenceDirectory)) { throw 'Live requires a new explicit -EvidenceDirectory.' }
    $arguments = @('scenarios-live', $Port, $Manifest, $EvidenceDirectory, 'unused')
}
# Production command/ViewModel paths and actual UART; no UI automation or shared profile edits.
dotnet run --project $project -c Release -- @arguments
if ($LASTEXITCODE -ne 0) { throw 'User scenario validation failed. Inspect report.json/wire.jsonl before any further TX; no automatic retry or restoration.' }
