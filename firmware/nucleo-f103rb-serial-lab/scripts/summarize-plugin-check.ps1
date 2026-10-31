[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SafeEvidence,
    [Parameter(Mandatory = $true)][string]$StateEvidence
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$labRoot = Split-Path -Parent $PSScriptRoot
$reports = @()
$directories = @($SafeEvidence,$StateEvidence) | ForEach-Object { (Resolve-Path -LiteralPath $_).Path }
foreach ($directory in $directories) {
    $report = Get-Content -LiteralPath (Join-Path $directory 'report.json') -Raw | ConvertFrom-Json
    if ($report.failure -or @($report.ownedSequencesRemaining).Count) { throw "Incomplete test phase: $directory" }
    $reports += $report
}
if ($reports[0].phase -ne 'safe' -or $reports[1].phase -ne 'state') { throw 'Require a successful safe phase and state phase.' }
$coverage = @()
foreach ($name in @($reports[0].coverage.name | Sort-Object)) {
    $entries = @($reports | ForEach-Object { $_.coverage | Where-Object name -eq $name })
    $positive = ($entries | Measure-Object positive -Sum).Sum
    $negative = ($entries | Measure-Object negative -Sum).Sum
    if ($positive -lt 1 -or $negative -lt 1) { throw "Missing positive or rejection coverage: $name" }
    $coverage += [pscustomobject]@{name=$name; positive=[int]$positive; negative=[int]$negative; result='PASS'}
}
if ($coverage.Count -ne $reports[1].coverage.Count) { throw 'Tool schemas changed between test phases.' }
$finalState = Get-Content -LiteralPath (Join-Path $directories[0] 'final-status.json') -Raw | ConvertFrom-Json
if (-not $finalState.connected -or $finalState.busy -or $finalState.port -ne 'COM3' -or $finalState.baudRate -ne 115200 -or
    $finalState.mode -ne 'terminal' -or $finalState.byteTimeoutMs -ne 40 -or $finalState.serialErrors -ne 0) { throw 'Final COM3 state was not restored.' }
$requestFiles = @($directories | ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -Filter '*-request.json' -File })
$observations = @($reports | ForEach-Object { $_.observations } | Select-Object -Unique)
$summary = [ordered]@{
    path='actual plugin mcp.json -> Start-SerialMonitorMcp.ps1 -> CLI MCP -> GUI -> COM3'
    testedTools=$coverage.Count
    toolCallAttempts=$requestFiles.Count
    passedCaseGroups=($reports | ForEach-Object { $_.cases } | Sort-Object -Unique).Count
    coverage=$coverage
    observations=$observations
    finalState=$finalState
    evidence=$directories
    limitation='Official SDK client against actual repository plugin launcher; native Codex chat tool exposure is separate.'
}
$summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $labRoot 'results\all-plugin-summary.json') -Encoding UTF8
$coverage | Format-Table -AutoSize
Write-Host "PASS all $($coverage.Count) discovered tools have positive and rejection evidence. Calls: $($requestFiles.Count)"
Write-Host ('Summary: ' + (Join-Path $labRoot 'results\all-plugin-summary.json'))
