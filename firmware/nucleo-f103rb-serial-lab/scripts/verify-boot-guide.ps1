[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^COM[1-9][0-9]*$')][string]$Port,
    [ValidatePattern('^build(?:-[A-Za-z0-9_-]+)?$')][string]$BuildDirectory='build-boot-guide',
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$labRoot=Split-Path -Parent $PSScriptRoot
$build=Join-Path $labRoot $BuildDirectory
$manifest=Get-Content -LiteralPath (Join-Path $build 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.board -ne 'NUCLEO-F103RB' -or $manifest.baudRate -ne 115200 -or
    (Get-FileHash -LiteralPath (Join-Path $build 'serial-lab.bin') -Algorithm SHA256).Hash -ne $manifest.sha256) {
    throw 'Unexpected board/baud or manifest image hash.'
}
$guide=[IO.File]::ReadAllBytes((Join-Path $build 'boot-guide.bin'))
$guideText=[Text.UTF8Encoding]::new($false,$true).GetString($guide)
if ($guide.Length -gt 1536 -or -not $guideText.Contains("FW=$($manifest.firmwareVersion) BUILD=$($manifest.buildId)`r`n")) {
    throw 'Invalid expected boot guide.'
}
$evidence=[IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidence) { throw 'Use a new evidence directory; previous evidence is preserved.' }
$null=New-Item -ItemType Directory -Path $evidence
$script:bootWire=[Collections.Generic.List[object]]::new()
$script:bootPassed=[Collections.Generic.List[string]]::new()
$bootFailure=$null
$bootSerial=[IO.Ports.SerialPort]::new($Port,115200,[IO.Ports.Parity]::None,8,[IO.Ports.StopBits]::One)
$bootSerial.Handshake=[IO.Ports.Handshake]::None
$bootSerial.DtrEnable=$false; $bootSerial.RtsEnable=$false
$bootSerial.ReadTimeout=100; $bootSerial.WriteTimeout=1000

function Read-Window([string]$Name,[byte[]]$Tx,[int]$Milliseconds) {
    $bytes=[Collections.Generic.List[byte]]::new()
    $started=[DateTimeOffset]::UtcNow
    try {
        if ($Tx.Length) { $bootSerial.Write($Tx,0,$Tx.Length) }
        $clock=[Diagnostics.Stopwatch]::StartNew()
        while ($clock.ElapsedMilliseconds -lt $Milliseconds) {
            if ($bootSerial.BytesToRead -gt 0) {
                $block=[byte[]]::new([Math]::Min(4096,$bootSerial.BytesToRead))
                $count=$bootSerial.Read($block,0,$block.Length)
                if ($bytes.Count + $count -gt 65536) { throw 'RX evidence budget exceeded; no command is retried.' }
                for ($i=0;$i -lt $count;$i++) { $bytes.Add($block[$i]) }
            } else { Start-Sleep -Milliseconds 5 }
        }
        return ,($bytes.ToArray())
    } finally {
        [IO.File]::WriteAllBytes((Join-Path $evidence ($Name + '.tx.bin')),$Tx)
        [IO.File]::WriteAllBytes((Join-Path $evidence ($Name + '.rx.bin')),$bytes.ToArray())
        $script:bootWire.Add([pscustomobject]@{name=$Name;startedAt=$started;endedAt=[DateTimeOffset]::UtcNow;
            txHex=[BitConverter]::ToString($Tx).Replace('-','');rxHex=[BitConverter]::ToString($bytes.ToArray()).Replace('-','')})
    }
}
function Assert-Exchange([string]$Name,[byte[]]$Tx,[byte[]]$Expected,[int]$Milliseconds=250) {
    $actual=Read-Window $Name $Tx $Milliseconds
    if ([BitConverter]::ToString($actual) -cne [BitConverter]::ToString($Expected)) { throw "Unexpected complete RX: $Name. Inspect raw evidence; no TX retry." }
    $script:bootPassed.Add($Name)
    Write-Host "PASS $Name ($($actual.Length) RX bytes)"
}
function Assert-Text([string]$Name,[string]$Command,[string]$Expected) {
    Assert-Exchange $Name ([Text.Encoding]::ASCII.GetBytes($Command+"`r`n")) ([Text.Encoding]::ASCII.GetBytes($Expected+"`r`n"))
}
try {
    # Direct exclusive port access only; close the GUI connection first. No GUI
    # control, flash, automatic reconnect, or uncertain-send retry is performed.
    $bootSerial.Open()
    $null=Read-Window 'initial-observation' ([byte[]]@()) 600
    Assert-Text 'build-identity' 'BUILD?' "BUILD version=$($manifest.firmwareVersion) protocol=1 id=$($manifest.buildId)"
    Assert-Text 'board-identity' 'ID?' 'ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1'
    Assert-Text 'initial-rtu' 'RTU' 'OK RTU'
    $bootSerial.Close(); $bootSerial.Open()
    Assert-Exchange 'reconnect-no-guide' ([byte[]]@()) ([byte[]]@()) 800
    foreach ($round in 1,2) {
        Assert-Text "round-$round-lab" 'LAB' 'OK LAB'
        # This is a deliberate real MCU reset, not the RAM-only RTU RESET.
        $resetReply=[byte[]]([Text.Encoding]::ASCII.GetBytes("OK RESET`r`n") + $guide)
        Assert-Exchange "round-$round-reset-guide" ([Text.Encoding]::ASCII.GetBytes("RESET CONFIRM`r`n")) $resetReply 2000
        Assert-Exchange "round-$round-no-periodic-guide" ([byte[]]@()) ([byte[]]@()) 1100
        Assert-Exchange "round-$round-rtu-read" ([byte[]](1,3,0,0,0,2,196,11)) ([byte[]](1,3,4,0,250,0,100,219,233))
        Assert-Text "round-$round-soft-lab" 'LAB' 'OK LAB'
        Assert-Text "round-$round-soft-reset" 'RTU RESET' 'OK RTU RESET'
        Assert-Text "round-$round-soft-status" 'RTU STATUS?' 'RTU reads=0 writes=0 led=0 fault=NONE delay_ms=0 pending=0'
        Assert-Text "round-$round-soft-rtu" 'RTU' 'OK RTU'
    }
    Assert-Exchange 'final-quiet' ([byte[]]@()) ([byte[]]@()) 800
} catch { $bootFailure=$_.Exception; throw }
finally {
    $bootSerial.Dispose()
    $passed=$null -eq $bootFailure -and $script:bootPassed.Count -eq 21
    [IO.File]::WriteAllText((Join-Path $evidence 'report.json'),([ordered]@{
        passed=$passed;hardwareVerified=$passed;port=$Port;baud=115200;format='8N1 / no handshake';
        firmwareVersion=$manifest.firmwareVersion;buildId=$manifest.buildId;binarySha256=$manifest.sha256;
        guideBytes=$guide.Length;realResetCountExpected=2;passedExchanges=$script:bootPassed.ToArray();
        error=$(if ($bootFailure) {$bootFailure.ToString()} else {$null});
        scope='Direct UART, exact whole UTF-8 guide and RTU bytes; no GUI rendering or physical button actuation; no TX retry';
        finalState=$(if ($passed) {'RAM/counters reset; LED off; quiet RTU; port closed'} else {'Inspect evidence; no automatic recovery'});
        wire=$script:bootWire.ToArray()
    } | ConvertTo-Json -Depth 7),[Text.UTF8Encoding]::new($false))
}
