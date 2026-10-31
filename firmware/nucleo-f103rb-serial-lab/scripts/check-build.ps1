[CmdletBinding()]
param([ValidatePattern('^build(?:-[A-Za-z0-9_-]+)?$')][string]$BuildDirectory = 'build-candidate')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root $BuildDirectory
& (Join-Path $PSScriptRoot 'build.ps1') -BuildDirectory $BuildDirectory
$native = @(& (Join-Path $PSScriptRoot 'test.ps1') -BuildDirectory $BuildDirectory | Tee-Object -FilePath (Join-Path $output 'native-check.txt'))
if (($native -join "`n") -notmatch 'PASS: (\d+) protocol checks') { throw 'Native C tests did not report success.' }
$nativeChecks = [int]$Matches[1]
dotnet run --project (Join-Path $root 'tests\FixtureContractCheck') -- offline $output (Join-Path $output 'manifest.json') |
    Tee-Object -FilePath (Join-Path $output 'contract-check.txt')
if ($LASTEXITCODE -ne 0) { throw 'Independent fixture contract verification failed.' }
& (Join-Path $PSScriptRoot 'test-codec.ps1')
dotnet run --project (Join-Path $root 'tests\AssetsCheck') -- (Join-Path $root 'sequences') |
    Tee-Object -FilePath (Join-Path $output 'asset-check.txt')
if ($LASTEXITCODE -ne 0) { throw 'GUI/CLI sequence definitions rejected by app parsers.' }
$manifest = Get-Content -LiteralPath (Join-Path $output 'manifest.json') -Raw | ConvertFrom-Json
$contract = Get-Content -LiteralPath (Join-Path $output 'contract-report.json') -Raw | ConvertFrom-Json
$validatorFiles = @(Get-ChildItem -LiteralPath (Join-Path $root 'tests\FixtureContractCheck') -File -Filter '*.cs') +
    @(Get-Item -LiteralPath (Join-Path $PSScriptRoot 'WireCodec.cs'),(Join-Path $root 'tests\test_lab.c'))
$validation = [ordered]@{
    schemaVersion=1; checkedAt=[DateTimeOffset]::Now.ToString('o'); passed=$true
    firmwareVersion=$manifest.firmwareVersion; buildId=$manifest.buildId; binarySha256=$manifest.sha256
    nativeProtocolChecks=$nativeChecks; independentContractChecks=$contract.assertions
    binaryPayloadLengths=$contract.binaryLengths; binaryContractChecks=$contract.binaryAssertions
    maxBinaryPayloadBytes=$manifest.maxBinaryPayloadBytes; maxBinaryFrameBytes=$manifest.maxBinaryFrameBytes
    guiDefinitions=5; cliDefinitions=2; fixtureCases=$contract.cases
    hardwareVerified=$false; hardwareStatus='Not run for this build; previous hardware reports identify an earlier BIN hash.'
    deviceOperations='none'; guiModeChanges=0
    testPlanSha256=(Get-FileHash -LiteralPath (Join-Path $root 'test-plan.json') -Algorithm SHA256).Hash
    validators=@($validatorFiles | Sort-Object FullName | ForEach-Object {
        [ordered]@{path=$_.FullName.Substring($root.Length + 1).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
}
$validation | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'validation.json') -Encoding UTF8
Write-Host "PASS build $($manifest.buildId): $nativeChecks C checks, $($contract.assertions) independent contract checks."
Write-Host "Evidence: $output"
Write-Host 'Hardware was not accessed. No GUI mode switch or visible log clear.'
