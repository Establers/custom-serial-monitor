$ErrorActionPreference = 'Stop'
if (-not ('SerialLabWire' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'WireCodec.cs') }
function Assert-That([bool]$Condition, [string]$Name) { if (-not $Condition) { throw "FAILED $Name" }; Write-Host "PASS $Name" }
Assert-That ([SerialLabWire]::Crc16([Text.Encoding]::ASCII.GetBytes('123456789'),0,9) -eq 0x29b1) 'CCITT-FALSE independent check vector'
$frame = [SerialLabWire]::Encode(2,0x12345678,[byte[]](0..255))
$decoded = [SerialLabWire]::Decode($frame)
Assert-That ($decoded.Count -eq 1 -and $decoded[0].Sequence -eq 0x12345678 -and $decoded[0].CrcValid) 'binary length/endian/CRC round trip'
[SerialLabWire]::AssertBytes($decoded[0].Payload,[byte[]](0..255))
$frame[$frame.Length - 1] = $frame[$frame.Length - 1] -bxor 1
Assert-That (-not [SerialLabWire]::Decode($frame)[0].CrcValid) 'corrupted CRC stays visible'
$rejected = $false
try { $null = [SerialLabWire]::Decode([byte[]](0xa5,0x5a,1)) } catch { $rejected = $true }
Assert-That $rejected 'truncated frame rejected'
$records = [Text.Encoding]::ASCII.GetBytes("OK BURST`r`nBURST seq=1 payload=`r`nBURST seq=2 payload=`r`nBURST seq=2 payload=`r`nBURST seq=4 payload=`r`nBURST seq=4 payload=`r`n")
[SerialLabWire]::AssertTextRecords($records,'BURST',4,3,2)
Assert-That $true 'known drop/duplicate sequence accepted'
$rejected = $false
try { [SerialLabWire]::AssertTextRecords($records,'BURST',4,0,0) } catch { $rejected = $true }
Assert-That $rejected 'unexpected drop/duplicate sequence rejected'
$rejected = $false
try { [SerialLabWire]::AssertTextRecords([Text.Encoding]::ASCII.GetBytes('BURST seq=1 payload='),'BURST',1,0,0) } catch { $rejected = $true }
Assert-That $rejected 'unterminated record rejected'
$rejected = $false
try { [SerialLabWire]::AssertBurstPayload([Text.Encoding]::ASCII.GetBytes("BURST seq=1 payload=ABX`r`n"),3) } catch { $rejected = $true }
Assert-That $rejected 'payload corruption rejected independently of sequence'
foreach ($length in @(0,1,255,256,257,511,512,513,1023,1024)) {
    $payload = [byte[]]@(for ($i = 0; $i -lt $length; $i++) { [byte](($i * 73 + [int][Math]::Floor($i / 256) * 19 + 0x5a) -band 255) })
    $packet = [SerialLabWire]::Encode(2,42,$payload)
    $decoded = [SerialLabWire]::Decode($packet)
    [SerialLabWire]::AssertBytes($decoded[0].Payload,$payload)
    Assert-That ($packet.Length -eq $length + 11 -and $decoded[0].CrcValid -and
        $packet[7] -eq ($length -band 255) -and $packet[8] -eq ($length -shr 8)) "binary boundary $length"
}
$rejected = $false
try { $null = [SerialLabWire]::Encode(2,1,[byte[]]::new(1025)) } catch { $rejected = $true }
Assert-That $rejected '1025-byte payload rejected'
Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' | ForEach-Object {
    $parseErrors = $null; $tokens = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($_.FullName,[ref]$tokens,[ref]$parseErrors)
    if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
}
Write-Host 'PASS all PowerShell scripts parse'
