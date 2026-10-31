[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CliPath,
    [Parameter(Mandatory = $true)][string]$InstanceId,
    [ValidateSet('COM3')][string]$Port = 'COM3',
    [ValidateRange(1, 10)][int]$Rounds = 4
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not ('SerialLabWire' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'WireCodec.cs') }
$directory = Join-Path (Split-Path -Parent $PSScriptRoot) ('results\sessions-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$null = New-Item -ItemType Directory -Path $directory -Force
$checks = [Collections.Generic.List[string]]::new()
$captures = [Collections.Generic.List[object]]::new()
$script:requestNumber = 0
$stamp = Get-Date -Format 'HHmmssfff'
$mainName = "F103RB LAB - Session audit $stamp main"
$stopName = "F103RB LAB - Session audit $stamp stop"
$ownedRuns = [Collections.Generic.List[string]]::new()
$ownedNames = [Collections.Generic.List[string]]::new()
$initialMode = $null
$failure = $null

function Start-Control([string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($CliPath)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8; $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($argument in @($Arguments) + @('--instance', $InstanceId)) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($start)
    ++$script:requestNumber
    return [pscustomobject]@{ Child = $child; Output = $child.StandardOutput.ReadToEndAsync(); Error = $child.StandardError.ReadToEndAsync(); Number = $script:requestNumber; Arguments = $Arguments }
}
function Finish-Control($Call) {
    try {
        if (-not $Call.Child.WaitForExit(15000)) { throw 'CLI did not exit within 15 seconds; TX is not retried.' }
        $json = $Call.Output.GetAwaiter().GetResult()
        $errorText = $Call.Error.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $directory ('cli-{0:D4}.json' -f $Call.Number)), $json, [Text.UTF8Encoding]::new($false))
        [ordered]@{arguments = $Call.Arguments; exitCode = $Call.Child.ExitCode; stderr = $errorText} | ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath (Join-Path $directory ('process-{0:D4}.json' -f $Call.Number)) -Encoding utf8
        if ($errorText) { throw "CLI stderr; inspect retained evidence before any retry: $errorText" }
        if ([string]::IsNullOrWhiteSpace($json)) { throw "CLI returned empty stdout (exit $($Call.Child.ExitCode)); inspect current run/captures before any retry." }
        return $json | ConvertFrom-Json
    }
    finally { if (-not $Call.Child.HasExited) { $Call.Child.Kill($true) }; $Call.Child.Dispose() }
}
function Control([string[]]$Arguments) {
    $waiting = [Diagnostics.Stopwatch]::StartNew()
    do {
        $response = Finish-Control (Start-Control $Arguments)
        if ($response.success) { return $response.data }
        # Only an explicit pre-operation busy rejection is retryable. An empty
        # response, TX evidence or any other error is never replayed here.
        if ($response.error.code -ne 'busy' -or $null -ne $response.data -or $waiting.Elapsed.TotalSeconds -ge 30) {
            throw "Operation failed, not retried: $($response | ConvertTo-Json -Depth 5 -Compress)"
        }
        Start-Sleep -Milliseconds 100
    } while ($true)
}
function Read-Capture($Capture, [string]$Label) {
    $bytes = [Collections.Generic.List[byte]]::new()
    $offset = 0L
    do {
        $page = (Control @('read', '--capture', $Capture.captureId, '--format', 'hex', '--offset', [string]$offset, '--limit', '4096')).page
        $bytes.AddRange([Convert]::FromHexString($page.text))
        $offset = $page.nextOffset
    } while ($page.hasMore)
    if ($bytes.Count -ne $Capture.storedBytes -or $Capture.rxGapDetected -or $Capture.droppedBytes -ne 0 -or $Capture.serialErrors -ne 0) {
        throw "Capture gap or inconsistent raw byte count: $($Capture.captureId)"
    }
    $path = Join-Path $directory "$Label.bin"
    [IO.File]::WriteAllBytes($path, $bytes.ToArray())
    $captures.Add([pscustomobject]@{captureId = $Capture.captureId; path = $path; bytes = $bytes.Count; status = $Capture.captureStatus; sendStatus = $Capture.sendStatus})
    return ,$bytes.ToArray()
}
function Save-Definition($Definition, [string]$Path) {
    [IO.File]::WriteAllText($Path, ($Definition | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    if (-not $ownedNames.Contains($Definition.name)) { $ownedNames.Add($Definition.name) }
    $null = Control @('sequence', 'save', '--file', $Path, '--overwrite', 'true')
}
function Start-Sequence([string]$Name) {
    $run = Control @('sequence', 'run', '--name', $Name, '--port', $Port)
    $ownedRuns.Add($run.runId)
    return $run.runId
}
function Wait-Run([string]$Id, [switch]$FirstSent) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        $run = Control @('sequence', 'status', '--run', $Id)
        if ($FirstSent -and @($run.steps | Where-Object sendStatus -EQ 'sent').Count -gt 0) { return $run }
        if ($run.state -ne 'running') { return $run }
        Start-Sleep -Milliseconds 20
    } while ($deadline.Elapsed.TotalSeconds -lt 30)
    throw "Run did not reach its expected state: $Id"
}
function Pass([string]$Name) { $checks.Add($Name); Write-Host "PASS $Name" }

try {
    $state = Control @('status')
    if (-not $state.connected -or $state.port -ne $Port -or $state.baudRate -ne 115200 -or $state.dataBits -ne 8 -or
        $state.parity -ne 'None' -or $state.stopBits -ne 'One' -or $state.handshake -ne 'None' -or $state.busy) { throw 'Require idle COM3 at 115200 / 8N1 / no handshake.' }
    $initialMode = $state.mode
    $identified = Control @('request', '--port', $Port, '--text', 'ID?', '--ending', 'crlf', '--duration-ms', '700', '--format', 'hex')
    [SerialLabWire]::AssertBytes((Read-Capture $identified.capture 'identity'), [Text.Encoding]::UTF8.GetBytes("ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1`r`n"))
    for ($round = 1; $round -le $Rounds; $round++) {
        $mode = if ($round % 2) { 'hex' } else { 'terminal' }
        $null = Control @('mode', '--set', $mode)
        $steps = [Collections.Generic.List[object]]::new()
        $expectedCycle = [Collections.Generic.List[byte]]::new()
        for ($index = 1; $index -le 2; $index++) {
            $sequence = [uint32]($round * 100 + $index)
            [byte[]]$payload = 0..255
            $packet = [SerialLabWire]::Encode(2, $sequence, $payload)
            $reply = [SerialLabWire]::Encode(0x82, $sequence, $payload)
            $steps.Add([ordered]@{name = "CRC echo $sequence"; command = [SerialLabWire]::Hex($packet); txMode = 'hex'; lineEnding = 'none'; captureMs = 400; delayAfterMs = 0})
            $expectedCycle.AddRange($reply)
            $text = "audit-$stamp-$round-$index"
            $steps.Add([ordered]@{name = "PING $text"; command = "PING $text"; txMode = 'text'; lineEnding = 'crlf'; captureMs = 400; delayAfterMs = 0})
            $expectedCycle.AddRange([Text.Encoding]::UTF8.GetBytes("PONG $text`r`n"))
        }
        Save-Definition ([ordered]@{name = $mainName; repeatCount = 3; steps = $steps.ToArray()}) (Join-Path $directory 'main.json')
        $stopToken = "cancel-$stamp-$round"
        Save-Definition ([ordered]@{name = $stopName; repeatCount = 1; steps = @(
            [ordered]@{name = 'Cancel after this TX'; command = "PING $stopToken"; txMode = 'text'; lineEnding = 'crlf'; captureMs = 60000; delayAfterMs = 0},
            [ordered]@{name = 'Must never be sent'; command = "PING unexpected-$stamp-$round"; txMode = 'text'; lineEnding = 'crlf'; captureMs = 400; delayAfterMs = 0}
        )}) (Join-Path $directory 'stop.json')
        $old = Start-Sequence $stopName
        $sent = Wait-Run $old -FirstSent
        if ($sent.state -ne 'running') { throw 'The stop test completed before cancellation was exercised.' }
        Start-Sleep -Milliseconds 100
        # Keep the owner inside a long observation window while all contenders
        # actually finish, so slow process startup cannot move a request past it.
        $calls = @(
            (Start-Control @('mode', '--set', $(if ($mode -eq 'hex') { 'terminal' } else { 'hex' }))),
            (Start-Control @('send', '--port', $Port, '--text', "PING forbidden-$stamp-$round")),
            (Start-Control @('disconnect')),
            (Start-Control @('sequence', 'run', '--name', $mainName, '--port', $Port)),
            (Start-Control @('cancel')),
            (Start-Control @('capture', '--port', $Port, '--duration-ms', '100'))
        )
        foreach ($call in $calls) {
            $reply = Finish-Control $call
            if ($reply.success -or $reply.error.code -ne 'busy') { throw 'A concurrent mutating operation was not rejected as busy.' }
        }
        $null = Control @('sequence', 'cancel', '--run', $old)
        $restart = Finish-Control (Start-Control @('sequence', 'run', '--name', $mainName, '--port', $Port))
        $old = Wait-Run $old
        if ($old.state -ne 'canceled' -or @($old.steps).Count -ne 1 -or $old.steps[0].sendStatus -ne 'sent') { throw 'Cancel allowed an extra step or lost the known TX outcome.' }
        $oldRx = Read-Capture $old.capture "cancel-$round"
        [SerialLabWire]::AssertBytes($oldRx, [Text.Encoding]::UTF8.GetBytes("PONG $stopToken`r`n"))
        if ($restart.success) { $id = $restart.data.runId; $ownedRuns.Add($id) }
        elseif ($restart.error.code -eq 'busy' -and $null -eq $restart.data) { $id = Start-Sequence $mainName }
        else { throw "Restart failed with uncertain outcome: $($restart | ConvertTo-Json -Depth 6 -Compress)" }
        $run = Wait-Run $id
        if ($run.state -ne 'completed' -or $run.completedSteps -ne 12 -or @($run.steps | Where-Object sendStatus -NE 'sent').Count -ne 0) { throw 'Mixed repeated sequence failed or had an uncertain TX.' }
        $expected = [Collections.Generic.List[byte]]::new()
        1..3 | ForEach-Object { $expected.AddRange($expectedCycle.ToArray()) }
        [SerialLabWire]::AssertBytes((Read-Capture $run.capture "mixed-$round"), $expected.ToArray())
        Pass "round ${round}: $mode GUI, cancel/restart, six concurrent contenders, 12 exact mixed TX/RX steps"
        $state = Control @('status')
        if ($state.serialErrors -ne 0) { throw 'GUI reported a serial error.' }
        $generation = $state.sessionGeneration
        $null = Control @('disconnect')
        $null = Control @('connect', '--port', $Port, '--baud', '115200')
        $state = Control @('status')
        if (-not $state.connected -or $state.sessionGeneration -le $generation) { throw 'Reconnect reused the old session or failed.' }
        Pass "round ${round}: COM3 close/reopen generation $generation -> $($state.sessionGeneration)"
    }
    $health = Control @('request', '--port', $Port, '--text', 'STATUS?', '--ending', 'crlf', '--duration-ms', '700', '--format', 'hex')
    $healthText = [Text.Encoding]::UTF8.GetString((Read-Capture $health.capture 'device-status'))
    if ($healthText -notmatch 'rx_drops=0 uart_errors=0 tx_rejected=0') { throw "Device reported UART/queue errors: $healthText" }
    Pass 'device rx_drops=0 uart_errors=0 tx_rejected=0'
}
catch { $failure = $_.Exception.Message; Write-Warning $failure }
finally {
    foreach ($id in $ownedRuns) {
        try { $run = Control @('sequence', 'status', '--run', $id); if ($run.state -eq 'running') { $null = Control @('sequence', 'cancel', '--run', $id); $null = Wait-Run $id } } catch { }
    }
    foreach ($name in $ownedNames) { try { $null = Control @('sequence', 'delete', '--name', $name) } catch { Write-Warning "Test sequence retained: $name" } }
    if ($initialMode) { try { $null = Control @('mode', '--set', $initialMode) } catch { } }
    [ordered]@{port = $Port; instanceId = $InstanceId; rounds = $Rounds; passed = $checks.ToArray(); failure = $failure; captures = $captures.ToArray()} |
        ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $directory 'report.json') -Encoding utf8
    Write-Host "Evidence: $directory"
}
if ($failure) { throw $failure }
