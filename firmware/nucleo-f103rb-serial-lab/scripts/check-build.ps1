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
$manifest = Get-Content -LiteralPath (Join-Path $output 'manifest.json') -Raw | ConvertFrom-Json
$bootGuideBytes = [IO.File]::ReadAllBytes((Join-Path $output 'boot-guide.bin'))
$bootGuideText = [Text.UTF8Encoding]::new($false,$true).GetString($bootGuideBytes)
if ($bootGuideBytes.Length -gt 1536 -or $bootGuideBytes.Length -eq 0 -or
    -not $bootGuideText.StartsWith("`r`nBOOT NUCLEO-F103RB SERIAL-LAB/1 baud=115200 8N1`r`nFW=$($manifest.firmwareVersion) BUILD=$($manifest.buildId)`r`n") -or
    [regex]::Matches($bootGuideText,'BOOT NUCLEO-F103RB').Count -ne 1 -or
    $bootGuideText -match '(?<!\r)\n' -or $bootGuideText -notmatch '[\uAC00-\uD7A3]' -or
    -not $bootGuideText.EndsWith("`r`n")) { throw 'Invalid one-shot UTF-8 boot guide contract.' }
foreach ($requiredGuideText in @('03-monitor-extended.gui.json','RX Check 26/26','Auto reconnect OFF',
    'FC04, 0x1000','UInt32','FC03, 0x1000','FC01, 0x1000','MONITOR ON','BURST 1000 32','RTU RESET')) {
    if (-not $bootGuideText.Contains($requiredGuideText)) { throw "Boot guide is missing: $requiredGuideText" }
}
"PASS one-shot UTF-8 boot guide: $($bootGuideBytes.Length) bytes, identity/line-endings/test instructions valid." |
    Tee-Object -FilePath (Join-Path $output 'boot-guide-check.txt')
dotnet run --project (Join-Path $root 'tests\FixtureContractCheck') -- offline $output (Join-Path $output 'manifest.json') |
    Tee-Object -FilePath (Join-Path $output 'contract-check.txt')
if ($LASTEXITCODE -ne 0) { throw 'Independent fixture contract verification failed.' }
& (Join-Path $PSScriptRoot 'test-codec.ps1')
dotnet run --project (Join-Path $root 'tests\AssetsCheck') -- (Join-Path $root 'sequences') |
    Tee-Object -FilePath (Join-Path $output 'asset-check.txt')
if ($LASTEXITCODE -ne 0) { throw 'GUI/CLI sequence definitions rejected by app parsers.' }
& (Join-Path $PSScriptRoot 'verify-mstp.ps1') -Mode Offline -BuildDirectory $BuildDirectory |
    Tee-Object -FilePath (Join-Path $output 'mstp-check.txt')
& (Join-Path $PSScriptRoot 'verify-modbus.ps1') -Mode Offline -BuildDirectory $BuildDirectory |
    Tee-Object -FilePath (Join-Path $output 'modbus-fixture-check.txt')
& (Join-Path $PSScriptRoot 'verify-rtu-server.ps1') -Mode Offline -BuildDirectory $BuildDirectory |
    Tee-Object -FilePath (Join-Path $output 'rtu-server-check.txt')
& (Join-Path $PSScriptRoot 'verify-modbus-monitor.ps1') -Mode Offline -BuildDirectory $BuildDirectory |
    Tee-Object -FilePath (Join-Path $output 'modbus-monitor-check.txt')
$contract = Get-Content -LiteralPath (Join-Path $output 'contract-report.json') -Raw | ConvertFrom-Json
$validatorFiles = @(Get-ChildItem -LiteralPath (Join-Path $root 'tests\FixtureContractCheck') -File -Filter '*.cs') +
    @(Get-ChildItem -LiteralPath (Join-Path $root 'tests\ModbusMonitorCheck'),(Join-Path $root 'tests\ModbusUartCheck') -File -Filter '*.cs') +
    @(Get-Item -LiteralPath (Join-Path $PSScriptRoot 'WireCodec.cs'),(Join-Path $root 'tests\test_lab.c'),(Join-Path $root 'tests\test_modbus_extended.c'),
        (Join-Path $root '..\..\SerialMonitor.WinUI\Services\SequenceRunObserver.cs'))
$validation = [ordered]@{
    schemaVersion=1; checkedAt=[DateTimeOffset]::Now.ToString('o'); passed=$true
    firmwareVersion=$manifest.firmwareVersion; buildId=$manifest.buildId; binarySha256=$manifest.sha256
    nativeProtocolChecks=$nativeChecks; independentContractChecks=$contract.assertions
    bootGuidePassed=$true; bootGuideBytes=$bootGuideBytes.Length; bootGuideEncoding='strict UTF-8 / CRLF'
    binaryPayloadLengths=$contract.binaryLengths; binaryContractChecks=$contract.binaryAssertions
    maxBinaryPayloadBytes=$manifest.maxBinaryPayloadBytes; maxBinaryFrameBytes=$manifest.maxBinaryFrameBytes
    guiDefinitions=5; cliDefinitions=2; mstpGuiDefinitions=1; mstpCliDefinitions=1; mstpNativeCases=6; fixtureCases=$contract.cases
    modbusLegacyGuiDefinitions=2; modbusMonitorGuiDefinitions=1; modbusMonitorOfflinePassed=$true
    hardwareVerified=$false; hardwareStatus='Not run for this build; previous hardware reports identify an earlier BIN hash.'
    deviceOperations='none'; guiModeChanges=0
    testPlanSha256=(Get-FileHash -LiteralPath (Join-Path $root 'test-plan.json') -Algorithm SHA256).Hash
    validators=@($validatorFiles | Sort-Object FullName | ForEach-Object {
        $relativePath = ([Uri]($root + [IO.Path]::DirectorySeparatorChar)).MakeRelativeUri([Uri]$_.FullName).ToString()
        [ordered]@{path=[Uri]::UnescapeDataString($relativePath); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
}
$validation | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'validation.json') -Encoding UTF8
Write-Host "PASS build $($manifest.buildId): $nativeChecks C checks, $($contract.assertions) independent contract checks."
Write-Host "Evidence: $output"
Write-Host 'Hardware was not accessed. No GUI mode switch or visible log clear.'
