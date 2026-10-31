[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if (-not ('SerialLabWire' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'WireCodec.cs') }
$sequenceDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'sequences'
$null = New-Item -ItemType Directory -Path $sequenceDir -Force

function Pair([string]$Name, [string]$Command, [string]$Pattern, [int]$Window = 700,
              [string]$TxMode = 'Terminal', [string]$RxMode = 'Terminal') {
    [ordered]@{Kind='Tx'; Name=$Name; CommandText=$Command; TxMode=$TxMode; LineEndingMode=$(if ($TxMode -eq 'Hex') {'None'} else {'Crlf'}); DelayAfterMs=0}
    [ordered]@{Kind='RxCheck'; Name=($Name + ' / RX'); CommandText=''; RxPattern=$Pattern; TxMode=$RxMode; LineEndingMode='None'; DelayAfterMs=$Window}
}
function Save-Gui([string]$File, [string]$Name, [object[]]$Steps) {
    $definition = [ordered]@{FormatVersion=3; Name=('F103RB LAB - ' + $Name); RepeatCount=1; Steps=@($Steps)}
    [IO.File]::WriteAllText((Join-Path $sequenceDir $File),($definition | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
}

$steps = @()
$steps += Pair 'Stop prior generators' 'STOP' 'OK STOP'
$steps += Pair 'Clear injected faults' 'FAULT CLEAR' 'OK FAULT CLEAR'
$steps += Pair 'Identify board' 'ID?' 'ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1'
$steps += Pair 'PING' 'PING onboard' 'PONG onboard'
$steps += Pair 'Preserve spaces' 'ECHO   spaced  ' 'ECHO   spaced  '
$steps += Pair 'UTF-8 fixture' 'UTF8' 'UTF8 '
$steps += Pair 'Log levels / events' 'LEVELS' '[ERROR] test error'
$steps += Pair 'LD2 on' 'LED ON' 'OK LED ON'
$steps += Pair 'LD2 off' 'LED OFF' 'OK LED OFF'
$steps += Pair 'User button' 'BUTTON?' 'BUTTON '
$steps += Pair 'Status' 'STATUS?' 'rx_drops=0 uart_errors=0'
Save-Gui '01-basic.gui.json' 'Basic' $steps

$steps = @()
$steps += Pair 'Stop' 'STOP' 'OK STOP'
$steps += Pair '800ms delay' 'DELAY 800 DELAYED-OK' 'DELAYED-OK' 1400
$steps += Pair '20ms per byte' 'SPLIT 20 SPLIT-OK' 'SPLIT-OK' 1000
$steps += Pair '2048-byte line' 'LONG 2048' 'ABCDEFGHIJKLMNOPQRSTUVWXYZ' 1200
$steps += Pair 'Ten periodic records' 'STREAM START 30 10' 'DATA seq=10 ' 1500
$steps += Pair 'Stop' 'STOP' 'OK STOP'
Save-Gui '02-timing.gui.json' 'Timing and line boundaries' $steps

$steps = @()
$steps += Pair 'Clear faults' 'FAULT CLEAR' 'OK FAULT CLEAR'
$steps += Pair '1000 text records' 'BURST 1000 32' 'BURST seq=1000 ' 8000
$steps += Pair 'Device counters' 'STATUS?' 'rx_drops=0 uart_errors=0 tx_rejected=0' 700
Save-Gui '03-burst.gui.json' 'Text burst 1000' $steps

$steps = @()
$steps += Pair 'Clear faults' 'FAULT CLEAR' 'OK FAULT CLEAR'
$request = [SerialLabWire]::Hex([SerialLabWire]::Encode(2,42,[byte[]](0,1,2,0x0a,0x0d,0x7f,0x80,0xa5,0x5a,0xff)))
$response = [SerialLabWire]::Hex([SerialLabWire]::Encode(0x82,42,[byte[]](0,1,2,0x0a,0x0d,0x7f,0x80,0xa5,0x5a,0xff)))
$steps += Pair 'Exact binary echo with CRC' $request $response 700 'Hex' 'Hex'
$steps += Pair 'Ten generated binary frames' 'HEX 10' 'A5 5A 90 0A 00 00 00 20 00' 1000 'Terminal' 'Hex'
$steps += Pair 'Status' 'STATUS?' 'rx_drops=0 uart_errors=0' 700
Save-Gui '04-binary.gui.json' 'Binary echo and frames' $steps

$steps = @()
$steps += Pair 'Intentional missing reply' 'SILENT' 'THIS-MUST-NOT-ARRIVE' 500
$steps += Pair 'Continue after expected miss' 'PING after-timeout' 'PONG after-timeout' 700
Save-Gui '05-expected-timeout.gui.json' 'Expected timeout then recovery' $steps

# CLI/MCP uses a different schema. This one is TX-only plus fixed RX captures.
$cliSteps = @(
    @{name='Identify'; command='ID?'; captureMs=700; expect=@{kind='text';value='ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1'}},
    @{name='Stop'; command='STOP'; captureMs=500; expect=@{kind='text';value='OK STOP'}},
    @{name='Clear faults'; command='FAULT CLEAR'; captureMs=500; expect=@{kind='text';value='OK FAULT CLEAR'}},
    @{name='PING'; command='PING automation'; captureMs=500; expect=@{kind='text';value='PONG automation'}},
    @{name='Split response'; command='SPLIT 20 CLI-SPLIT-OK'; captureMs=1000; expect=@{kind='text';value='CLI-SPLIT-OK'}},
    @{name='Burst'; command='BURST 100 16'; captureMs=1500; expect=@{kind='text';value='BURST seq=100 '}},
    @{name='Status'; command='STATUS?'; captureMs=500; expect=@{kind='text';value='rx_drops=0 uart_errors=0'}})
foreach ($step in $cliSteps) { $step.txMode='text'; $step.lineEnding='crlf'; $step.delayAfterMs=0 }
$cli = [ordered]@{name='F103RB LAB - CLI smoke'; repeatCount=1; steps=$cliSteps}
[IO.File]::WriteAllText((Join-Path $sequenceDir 'smoke.cli.json'),($cli | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$binary = [ordered]@{name='F103RB LAB - Binary CRC echo'; repeatCount=1; steps=@(
    [ordered]@{name='Binary echo including control/high bytes'; command=$request; txMode='hex'; lineEnding='none'; captureMs=700; delayAfterMs=0; expect=@{kind='hex';value=$response}})}
[IO.File]::WriteAllText((Join-Path $sequenceDir 'binary.cli.json'),($binary | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Write-Host "Generated five GUI v3 sequences and two CLI sequences: $sequenceDir"
