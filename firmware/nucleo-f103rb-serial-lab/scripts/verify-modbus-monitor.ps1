[CmdletBinding()]
param(
    [ValidateSet('Offline','Live')][string]$Mode = 'Offline',
    [ValidatePattern('^build(?:-[A-Za-z0-9_-]+)?$')][string]$BuildDirectory = 'build-modbus-monitor',
    [string]$Port,
    [string]$Manifest,
    [string]$EvidenceDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$labRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $labRoot 'tests/ModbusMonitorCheck/ModbusMonitorCheck.csproj'
$sequence = Join-Path $labRoot 'sequences/modbus/03-monitor-extended.gui.json'
if (-not $Manifest) { $Manifest = Join-Path (Join-Path $labRoot $BuildDirectory) 'manifest.json' }
if ($Mode -eq 'Offline') {
    $arguments = @('offline',$Manifest,$sequence)
} else {
    if (-not $Port -or $Port -notmatch '^COM[1-9][0-9]*$') { throw 'Live requires an explicit -Port COMn. Disconnect that port in Serial Monitor first.' }
    if (-not $EvidenceDirectory) { throw 'Live requires an explicit new -EvidenceDirectory. Existing evidence is never overwritten.' }
    if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'EvidenceDirectory already exists; choose a new directory.' }
    $arguments = @('live',$Port,$Manifest,$EvidenceDirectory,$sequence)
}
# Offline never opens COM ports. Live directly owns only the explicit port;
# it does not control GUI, flash/reset hardware, retry a failed TX, or run a
# GUI v3 file through CLI/MCP. All RX evidence is written outside callbacks.
dotnet run --project $project -c Release -- @arguments
if ($LASTEXITCODE -ne 0) { throw "Modbus monitor validation failed ($LASTEXITCODE). Inspect report.json/wire.jsonl before further TX." }
