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
    # Reuse the existing independent protocol/fixture/import checks. This path
    # does not execute commands, access UART, reset or flash hardware.
    $arguments = @('offline',$Manifest,$sequence)
} else {
    if (-not $Port -or $Port -notmatch '^COM[1-9][0-9]*$') { throw 'Live requires an explicit -Port COMn. Disconnect that port in Serial Monitor first.' }
    if (-not $EvidenceDirectory) { throw 'Live requires an explicit new -EvidenceDirectory.' }
    if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'EvidenceDirectory already exists; choose a new directory.' }
    # The fifth argument keeps the existing five-argument validator interface;
    # commands-live ignores it and never runs the generic GUI sequence.
    $arguments = @('commands-live',$Port,$Manifest,$EvidenceDirectory,$sequence)
}
dotnet run --project $project -c Release -- @arguments
if ($LASTEXITCODE -ne 0) { throw "Modbus command validation failed ($LASTEXITCODE). Inspect report.json/wire.jsonl before any further TX." }
