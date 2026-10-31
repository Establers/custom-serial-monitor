[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CliPath,
    [Parameter(Mandatory = $true)][string]$InstanceId,
    [string]$Port = 'COM3',
    [switch]$AllowDisruptiveChecks
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $AllowDisruptiveChecks) { throw 'This suite changes baud/GUI mode and resets the MCU. Pass -AllowDisruptiveChecks explicitly; use FixtureContractCheck for screen-preserving checks.' }
if (-not ('SerialLabWire' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'WireCodec.cs') }
$root = Split-Path -Parent $PSScriptRoot
$directory = Join-Path $root ('results\advanced-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$null = New-Item -ItemType Directory -Path $directory -Force
$script:step = 0
$script:checks = [Collections.Generic.List[string]]::new()
$script:evidence = [Collections.Generic.List[object]]::new()

function Control([string[]]$Arguments) {
    ++$script:step
    $raw = & $CliPath @Arguments '--instance' $InstanceId
    $json = $raw -join "`n"
    [IO.File]::WriteAllText((Join-Path $directory ('cli-{0:D3}.json' -f $script:step)),$json,[Text.UTF8Encoding]::new($false))
    $response = $json | ConvertFrom-Json
    if (-not $response.success) { throw "CLI failure; not retried: $json" }
    return $response.data
}
function Receive([string]$Text, [int]$Window = 700, [switch]$Observe) {
    if ($Observe) { $argsList = @('capture') }
    else { $argsList = @('request','--text',$Text,'--ending','crlf') }
    $data = Control ($argsList + @('--port',$Port,'--duration-ms',[string]$Window,'--preview-bytes','128','--format','hex'))
    $capture = $data.capture
    $path = Join-Path $directory ('rx-{0:D3}.bin' -f $script:step)
    Copy-Item -LiteralPath $capture.rawFile -Destination $path
    $script:evidence.Add([pscustomobject]@{captureId=$capture.captureId; path=$path; bytes=$capture.storedBytes})
    if ($capture.captureStatus -ne 'completed' -or $capture.rxGapDetected -or $capture.droppedBytes -ne 0 -or $capture.serialErrors -ne 0) {
        throw "Incomplete capture: $($capture.captureId)"
    }
    return ,([IO.File]::ReadAllBytes($path))
}
function Contains([byte[]]$Bytes,[string]$Text) {
    if (-not [Text.Encoding]::UTF8.GetString($Bytes).Contains($Text)) { throw "Missing expected response: $Text" }
}
function Pass([string]$Name) { $script:checks.Add($Name); Write-Host "PASS $Name" }

$failure = $null
try {
    $state = Control @('status')
    if (-not $state.connected -or $state.port -ne $Port -or $state.baudRate -ne 115200 -or $state.dataBits -ne 8 -or
        $state.parity -ne 'None' -or $state.stopBits -ne 'One' -or $state.handshake -ne 'None') { throw 'Require selected COM3 connection at 115200 / 8N1 / no handshake.' }
    Contains (Receive 'ID?') 'ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1'
    Contains (Receive 'STOP') 'OK STOP'
    Contains (Receive 'FAULT CLEAR') 'OK FAULT CLEAR'
    # Verify support before asking the device to switch away from its current baud.
    $null = Control @('configure','--port',$Port,'--baud','115200','--byte-timeout-ms','40')
    foreach ($rate in @(9600,19200,38400,57600,230400,460800,921600,115200)) {
        Contains (Receive "BAUD $rate" 200) "OK BAUD $rate SWITCH_IN_MS=500"
        $null = Control @('configure','--port',$Port,'--baud',[string]$rate)
        # No TX while the device's delayed baud transition is pending.
        $null = Receive '' 1200 -Observe
        Contains (Receive "PING baud-$rate" 700) "PONG baud-$rate"
        $state = Control @('status')
        if ($state.baudRate -ne $rate) { throw "Host baud readback differs: $rate" }
        Pass "live baud $rate"
    }
    $null = Control @('mode','--set','hex')
    foreach ($timeout in @(1,40,120)) {
        $null = Control @('configure','--port',$Port,'--byte-timeout-ms',[string]$timeout)
        $rx = Receive 'HEX 20' 700
        [SerialLabWire]::AssertGeneratedFrames($rx,20,0)
        Pass "HEX mode raw integrity with byte timeout ${timeout}ms"
    }
    $null = Control @('configure','--port',$Port,'--byte-timeout-ms','40')
    $null = Control @('mode','--set','terminal')
    Contains (Receive 'LED ON') 'OK LED ON'
    Contains (Receive 'LED OFF') 'OK LED OFF'
    Contains (Receive 'BUTTON?') 'BUTTON '
    Pass 'LED commands and USER-button read response'
    $rx = Receive 'STATUS?'
    Contains $rx 'rx_drops=0 uart_errors=0 tx_rejected=0'
    Pass 'zero unintended device errors after baud/mode changes'
    $rx = Receive 'RESET CONFIRM' 2000
    Contains $rx 'OK RESET'
    Contains $rx 'BOOT NUCLEO-F103RB SERIAL-LAB/1 baud=115200 8N1'
    Contains (Receive 'PING after-reset') 'PONG after-reset'
    Pass 'software reset, boot log, and command recovery'
    $rx = Receive 'STATUS?'
    Contains $rx 'parse_errors=0 rx_drops=0 uart_errors=0 tx_rejected=0'
    Pass 'clean counters after reboot'
}
catch { $failure = $_.Exception.Message; Write-Warning $failure }
finally {
    # An uncertain baud transition is not automatically replayed. Restore with the
    # physical reset button and host 115200 if a failure interrupted this script.
    [ordered]@{port=$Port; instanceId=$InstanceId; passed=$script:checks.ToArray(); failure=$failure; captures=$script:evidence.ToArray()} |
        ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $directory 'report.json') -Encoding UTF8
    Write-Host "Evidence: $directory"
}
if ($failure) { throw $failure }
Write-Host "PASS $($script:checks.Count) advanced hardware checks; restored Terminal/115200/40ms."
