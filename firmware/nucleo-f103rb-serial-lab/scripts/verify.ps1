[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CliPath,
    [Parameter(Mandatory = $true)][string]$InstanceId,
    [string]$Port = 'COM3',
    [ValidateRange(0,86400)][int]$SoakSeconds = 0
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not ('SerialLabWire' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'WireCodec.cs') }
$labRoot = Split-Path -Parent $PSScriptRoot
$resultDir = Join-Path $labRoot ('results\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$null = New-Item -ItemType Directory -Path $resultDir -Force
$script:captures = [Collections.Generic.List[object]]::new()
$script:passed = [Collections.Generic.List[string]]::new()
$script:counter = 0
$script:identified = $false

function Invoke-Control([string[]]$CliArguments) {
    $lines = & $CliPath @CliArguments '--instance' $InstanceId
    $exitCode = $LASTEXITCODE
    $json = $lines -join "`n"
    [IO.File]::WriteAllText((Join-Path $resultDir ('cli-{0:D3}.json' -f $script:counter)), $json, [Text.UTF8Encoding]::new($false))
    try { $response = $json | ConvertFrom-Json } catch { throw "Invalid CLI JSON (exit $exitCode); command was NOT retried. $json" }
    if (-not $response.success) { throw "CLI failure (not retried): $json" }
    return $response.data
}

function Get-Rx {
    param([string]$Text, [byte[]]$Bytes, [int]$WindowMs = 500, [string]$Ending = 'crlf', [switch]$Observe)
    $script:counter++
    $argsList = @()
    if ($Observe) { $argsList += 'capture' }
    elseif ($PSBoundParameters.ContainsKey('Bytes')) { $argsList += @('request','--hex',[SerialLabWire]::Hex($Bytes)) }
    else {
        # UTF-8 file preserves spaces and non-ASCII across Windows PowerShell/native argv.
        $commandFile = Join-Path $resultDir ('tx-{0:D3}.txt' -f $script:counter)
        [IO.File]::WriteAllText($commandFile,$Text,[Text.UTF8Encoding]::new($false))
        $argsList += @('request','--text-file',$commandFile,'--ending',$Ending)
    }
    $argsList += @('--port',$Port,'--duration-ms',[string]$WindowMs,'--preview-bytes','64','--format','hex')
    $data = Invoke-Control $argsList
    $cap = $data.capture
    if ($cap.captureStatus -ne 'completed' -or $cap.rxGapDetected -or $cap.droppedBytes -ne 0 -or $cap.serialErrors -ne 0) {
        throw "Incomplete RX evidence; inspect capture $($cap.captureId). No TX retry performed."
    }
    $evidence = Join-Path $resultDir ('rx-{0:D3}.bin' -f $script:counter)
    Copy-Item -LiteralPath $cap.rawFile -Destination $evidence
    $cap | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath ($evidence + '.json') -Encoding UTF8
    $script:captures.Add([pscustomobject]@{captureId=$cap.captureId; path=$evidence; bytes=$cap.storedBytes})
    if ((Get-Item -LiteralPath $evidence).Length -gt 16MB) { throw 'Capture exceeds verifier memory budget (16 MiB).' }
    return ,([IO.File]::ReadAllBytes($evidence))
}

function Assert-Contains([byte[]]$Actual, [string]$Expected) {
    if (-not [Text.Encoding]::UTF8.GetString($Actual).Contains($Expected)) { throw "Missing literal response: $Expected" }
}
function Assert-Text([byte[]]$Actual, [string]$Expected) {
    [SerialLabWire]::AssertBytes($Actual,[Text.Encoding]::UTF8.GetBytes($Expected))
}
function Passed([string]$Name) { $script:passed.Add($Name); Write-Host "PASS $Name" }

$failure = $null
try {
    $status = Invoke-Control @('status')
    # Firmware tests require a deliberately selected GUI already connected to COM3.
    if (-not $status.connected -or $status.port -ne $Port -or $status.baudRate -ne 115200 -or
        $status.dataBits -ne 8 -or $status.parity -ne 'None' -or $status.stopBits -ne 'One' -or $status.handshake -ne 'None') {
        throw "Connect the selected GUI to $Port at 115200 / 8N1 / no handshake first. Status: $($status | ConvertTo-Json -Depth 8 -Compress)"
    }
    $rx = Get-Rx -Text 'ID?' -WindowMs 700
    Assert-Contains $rx 'ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1'
    $script:identified = $true
    Passed 'firmware identity'
    Assert-Contains (Get-Rx -Text 'STOP') 'OK STOP'
    Assert-Contains (Get-Rx -Text 'FAULT CLEAR') 'OK FAULT CLEAR'
    foreach ($ending in @('cr','lf','crlf')) {
        Assert-Text (Get-Rx -Text "PING $ending" -Ending $ending) "PONG $ending`r`n"
        Passed "line ending $ending"
    }
    Assert-Text (Get-Rx -Text 'ECHO   spaces  ') "ECHO   spaces  `r`n"; Passed 'exact whitespace echo'
    $unicode = [Text.Encoding]::UTF8.GetString([byte[]](0xec,0x95,0x88,0xeb,0x85,0x95,0xed,0x95,0x98,0xec,0x84,0xb8,0xec,0x9a,0x94,0x20,0xe6,0x97,0xa5,0xe6,0x9c,0xac,0xe8,0xaa,0x9e,0x20,0xf0,0x9f,0x9a,0x80))
    Assert-Text (Get-Rx -Text 'UTF8') "UTF8 $unicode`r`n"; Passed 'UTF-8 Korean/Japanese/emoji'
    Assert-Text (Get-Rx -Text 'ANSI') ("ANSI " + [char]27 + "[32mGREEN" + [char]27 + "[0m " + [char]27 + "[31mRED" + [char]27 + "[0m`r`n"); Passed 'ANSI bytes'
    Assert-Text (Get-Rx -Text 'NEWLINES') "CRLF`r`nLF`nCR`rEND`r`n"; Passed 'mixed newline RX'
    Assert-Contains (Get-Rx -Text 'LEVELS') '[ERROR] test error'; Passed 'event/log level fixture'
    $expectedLong = -join (0..2047 | ForEach-Object { [char](65 + $_ % 26) })
    Assert-Text (Get-Rx -Text 'LONG 2048' -WindowMs 1000) ($expectedLong + "`r`n"); Passed '2048 byte line'
    Assert-Text (Get-Rx -Text "PING first`r`nPING second`nPING third`r" -Ending none) "PONG first`r`nPONG second`r`nPONG third`r`n"; Passed 'coalesced commands'
    Assert-Text (Get-Rx -Text 'SILENT' -WindowMs 300) ''; Passed 'intentional no response'
    Assert-Text (Get-Rx -Text 'DELAY 1500 LATE' -WindowMs 300) ''; Passed 'no premature delayed response'
    Assert-Text (Get-Rx -Observe -WindowMs 2200) "LATE`r`n"; Passed 'late response without retransmission'
    $splitText = $unicode.Substring(0,2)
    Assert-Text (Get-Rx -Text "SPLIT 30 $splitText" -WindowMs 1200) "$splitText`r`n"; Passed 'byte-split multibyte UTF-8'
    Assert-Text (Get-Rx -Text 'SPLIT 1000 cancel-me' -WindowMs 10) ''; $null = Get-Rx -Text 'STOP'
    Assert-Text (Get-Rx -Observe -WindowMs 300) ''; Passed 'STOP cancels delayed output'
    Assert-Contains (Get-Rx -Text (('X' * 2000) + "`nPING recovered") -WindowMs 1000) 'PONG recovered'; Passed 'oversized command recovery'
    Assert-Contains (Get-Rx -Text 'BAUD 12345') 'ERR BAUD'; Passed 'invalid arguments rejected'
    $rx = Get-Rx -Text 'BURST 1000 32' -WindowMs 7500
    [SerialLabWire]::AssertTextRecords($rx,'BURST',1000,0,0)
    [SerialLabWire]::AssertBurstPayload($rx,32); Passed '1000 numbered text records + exact payload'
    [SerialLabWire]::AssertTextRecords((Get-Rx -Text 'STREAM START 5 40' -WindowMs 1000),'DATA',40,0,0); Passed 'finite periodic stream'
    $rx = Get-Rx -Text "STREAM START 5 100`r`nPING mixed" -WindowMs 1500
    Assert-Contains $rx 'PONG mixed'; [SerialLabWire]::AssertTextRecords($rx,'DATA',100,0,0); Passed 'commands during periodic stream'
    $rx = Get-Rx -Text "FAULT DROP 5`r`nFAULT DUP 3`r`nBURST 30" -WindowMs 1000
    [SerialLabWire]::AssertTextRecords($rx,'BURST',30,5,3); Passed 'intentional loss/duplicate classification'
    [SerialLabWire]::AssertBurstPayload($rx,0)
    $null = Get-Rx -Text 'FAULT CLEAR'
    $payload = [byte[]](0..255)
    $rx = Get-Rx -Bytes ([SerialLabWire]::Encode(2,305419896,$payload))
    [SerialLabWire]::AssertBytes($rx,[SerialLabWire]::Encode(0x82,305419896,$payload)); Passed 'all 256 byte values + CRC echo'
    $ping = [SerialLabWire]::Encode(1,7,[byte[]]@())
    $rx = Get-Rx -Bytes ([byte[]]($ping + $ping))
    $pong = [SerialLabWire]::Encode(0x81,7,[byte[]]@())
    [SerialLabWire]::AssertBytes($rx,[byte[]]($pong + $pong)); Passed 'coalesced binary frames'
    $badCrc = [SerialLabWire]::Encode(1,8,[byte[]]@()); $badCrc[$badCrc.Length - 1] = $badCrc[$badCrc.Length - 1] -bxor 1
    $rx = Get-Rx -Bytes $badCrc
    [SerialLabWire]::AssertBytes($rx,[SerialLabWire]::Encode(0xe0,8,[Text.Encoding]::ASCII.GetBytes('CRC'))); Passed 'invalid request CRC rejected'
    $rx = Get-Rx -Bytes ([byte[]](0xa5,0x5a,1,0,0,0,0,1,4))
    [SerialLabWire]::AssertBytes($rx,[SerialLabWire]::Encode(0xe0,0,[Text.Encoding]::ASCII.GetBytes('LEN'))); Passed 'oversized binary length rejected'
    $rx = Get-Rx -Text 'HEX 1000' -WindowMs 6000
    [SerialLabWire]::AssertGeneratedFrames($rx,1000,0); Passed '1000 binary frames: order/payload/CRC'
    $rx = Get-Rx -Text "FAULT CRC 7`r`nHEX 21" -WindowMs 700
    [SerialLabWire]::AssertGeneratedFrames($rx,21,7); Passed 'exactly three intentional corrupt CRCs'
    $null = Get-Rx -Text 'FAULT CLEAR'
    $remaining = $SoakSeconds
    while ($remaining -gt 0) {
        $seconds = [Math]::Min(10,$remaining)
        $count = $seconds * 100
        $rx = Get-Rx -Text "STREAM START 10 $count" -WindowMs ($seconds * 1000 + 1500)
        [SerialLabWire]::AssertTextRecords($rx,'DATA',$count,0,0)
        $remaining -= $seconds
        Write-Host "SOAK verified $($SoakSeconds - $remaining)/$SoakSeconds seconds"
    }
    if ($SoakSeconds) { Passed "soak $SoakSeconds seconds (finite windows, no full-run RX accumulation)" }
    $rx = Get-Rx -Text 'STATUS?'
    Assert-Contains $rx 'rx_drops=0 uart_errors=0 tx_rejected=0'; Passed 'no device-side unintended RX/TX loss'
}
catch { $failure = $_.Exception.Message; Write-Warning $failure }
finally {
    # STOP is a documented cancellation, never a replay of the failed command.
    if ($script:identified) {
        try { $null = Get-Rx -Text "STOP`r`nFAULT CLEAR" -WindowMs 300 }
        catch {
            $cleanupFailure = "Cleanup uncertain: $($_.Exception.Message)"
            Write-Warning $cleanupFailure
            if (-not $failure) { $failure = $cleanupFailure }
        }
    }
    $report = [ordered]@{ port=$Port; instanceId=$InstanceId; baud=115200; passed=$script:passed.ToArray(); failure=$failure; captures=$script:captures.ToArray() }
    $report | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $resultDir 'report.json') -Encoding UTF8
    Write-Host "Evidence: $resultDir"
}
if ($failure) { throw $failure }
Write-Host "PASS $($script:passed.Count) hardware checks. UI rendering/file log review remains separate."
