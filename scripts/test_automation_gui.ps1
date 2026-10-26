#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CliPath,
    [Parameter(Mandatory)][string]$InstanceId
)

# Uses an already running Debug GUI. Never starts an app or opens a real COM port.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not [IO.Path]::IsPathFullyQualified($CliPath) -or -not (Test-Path -LiteralPath $CliPath -PathType Leaf)) {
    throw '-CliPath must name an existing absolute serialctl.exe path.'
}
if ([string]::IsNullOrWhiteSpace($InstanceId)) { throw '-InstanceId is required; select it from serialctl instances.' }
$repo = Split-Path -Parent $PSScriptRoot
$reportDirectory = Join-Path $repo ('artifacts\automation-gui-smoke\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($reportDirectory) | Out-Null
$reportPath = Join-Path $reportDirectory 'report.json'
$sequencePath = Join-Path $reportDirectory 'sequence.json'
$sequenceName = 'CLI MCP smoke ' + [Guid]::NewGuid().ToString('N')
$checks = [Collections.Generic.List[object]]::new()
$captureReports = [Collections.Generic.List[object]]::new()
$cleanupErrors = [Collections.Generic.List[string]]::new()
$initialState = $null
$finalState = $null
$sessionGeneration = $null
$connectedByScript = $false
$sequenceSaveAttempted = $false
$runId = $null
$mcpProcess = $null
$mcpErrorRead = $null
$mcpRequestId = 0
$failure = $null

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Get-BoundedMessage([string]$Message) {
    if ($Message.Length -gt 1024) { return $Message.Substring(0, 1024) }
    return $Message
}

function New-CliProcess([string[]]$Arguments, [switch]$Stdio) {
    $start = [Diagnostics.ProcessStartInfo]::new($CliPath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.RedirectStandardInput = $Stdio.IsPresent
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    return [Diagnostics.Process]::Start($start)
}

function Assert-Response($Response, [string]$Operation) {
    if (-not $Response.success) {
        $errorCode = $Response.error.code
        $errorText = Get-BoundedMessage $Response.error.message
        throw "$Operation failed ($errorCode): $errorText. No TX was retried."
    }
}

function Invoke-Cli([string[]]$Arguments, [switch]$AllowFailure) {
    $process = New-CliProcess -Arguments @($Arguments + @('--instance', $InstanceId))
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(20000)) {
            throw "CLI $($Arguments[0]) timed out; its TX outcome can be uncertain. Do not resend."
        }
        $json = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        Assert-True ([string]::IsNullOrWhiteSpace($errorText)) ('Unexpected CLI stderr: ' + (Get-BoundedMessage $errorText))
        $response = $json | ConvertFrom-Json -Depth 64
        if (-not $AllowFailure) {
            Assert-Response $response ('CLI ' + $Arguments[0])
            Assert-True ($process.ExitCode -eq 0) "CLI returned exit code $($process.ExitCode)."
        }
        return $response
    }
    finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(5000) | Out-Null }
        $process.Dispose()
    }
}

function Send-McpMessage($Message) {
    $line = $Message | ConvertTo-Json -Compress -Depth 64
    $mcpProcess.StandardInput.WriteLine($line)
    $mcpProcess.StandardInput.Flush()
}

function Read-McpResponse([int]$ExpectedId) {
    $read = $mcpProcess.StandardOutput.ReadLineAsync()
    if (-not $read.Wait(20000)) { throw "MCP request $ExpectedId timed out; no TX will be retried." }
    $line = $read.GetAwaiter().GetResult()
    Assert-True (-not [string]::IsNullOrEmpty($line)) 'MCP closed stdout before its response.'
    $response = $line | ConvertFrom-Json -Depth 64
    Assert-True ($response.id -eq $ExpectedId) "Unexpected MCP response id; expected $ExpectedId."
    if ($response.PSObject.Properties.Name -contains 'error') {
        throw ('MCP JSON-RPC error: ' + (Get-BoundedMessage $response.error.message))
    }
    return $response.result
}

function Invoke-Mcp([string]$Name, [hashtable]$Arguments) {
    $Arguments['instanceId'] = $InstanceId
    $script:mcpRequestId++
    Send-McpMessage @{ jsonrpc = '2.0'; id = $mcpRequestId; method = 'tools/call'; params = @{ name = $Name; arguments = $Arguments } }
    $result = Read-McpResponse $mcpRequestId
    if ($result.PSObject.Properties.Name -contains 'structuredContent') {
        $response = $result.structuredContent
    }
    else {
        $textContent = @($result.content | Where-Object type -eq 'text')
        Assert-True ($textContent.Count -eq 1) 'MCP response has no unique JSON text result.'
        $response = $textContent[0].text | ConvertFrom-Json -Depth 64
    }
    Assert-Response $response $Name
    Assert-True (-not $result.isError) "$Name returned isError=true."
    return $response
}

function Assert-MockState($State, [switch]$SameSession) {
    Assert-True ($State.connected -and $State.port -ieq 'MOCK') 'Safety check: the selected GUI must be connected to MOCK.'
    if ($SameSession) {
        Assert-True ($State.sessionGeneration -eq $sessionGeneration) 'The receive session changed unexpectedly; refusing further mutation.'
    }
}

function Get-StateSummary($State) {
    return [ordered]@{ connected = $State.connected; port = $State.port; baudRate = $State.baudRate;
        byteTimeoutMs = $State.byteTimeoutMs; mode = $State.mode; sessionGeneration = $State.sessionGeneration; busy = $State.busy }
}

function Find-BytePattern([byte[]]$Bytes, [byte[]]$Pattern) {
    for ($offset = 0; $offset -le $Bytes.Length - $Pattern.Length; $offset++) {
        $matched = $true
        for ($index = 0; $index -lt $Pattern.Length; $index++) {
            if ($Bytes[$offset + $index] -ne $Pattern[$index]) { $matched = $false; break }
        }
        if ($matched) { return $offset }
    }
    return -1
}

function Assert-Capture($Data, [string]$Source, [string]$ExpectedEcho, [switch]$McpPaging) {
    $capture = $Data.capture
    Assert-True ($capture.captureStatus -eq 'completed') "$Source capture did not complete."
    Assert-True (-not $capture.rxGapDetected) "$Source capture has an RX gap."
    if ($Source -ne 'sequence') { Assert-True ($capture.sendStatus -eq 'sent') "$Source did not report sent." }
    $raw = [IO.File]::ReadAllBytes($capture.rawFile)
    $expectedBytes = [Text.Encoding]::UTF8.GetBytes($ExpectedEcho)
    $echoOffset = Find-BytePattern $raw $expectedBytes
    Assert-True ($echoOffset -ge 0) "$Source raw capture does not contain the exact MOCK echo bytes."
    $page = $Data.page
    $hexPages = [Text.StringBuilder]::new()
    $pageCount = 0
    do {
        Assert-True ($page.format -eq 'hex') "$Source preview/page did not honor HEX format."
        $hexPages.Append($page.text) | Out-Null
        $pageCount++
        Assert-True ($pageCount -le 128) "$Source exceeded the smoke test's bounded page budget."
        if (-not $page.hasMore) { break }
        $offset = [long]$page.nextOffset
        if ($McpPaging) {
            $read = Invoke-Mcp 'serial_read_capture' @{ captureId = $capture.captureId; offset = $offset; limit = 4096; format = 'hex' }
        }
        else {
            $read = Invoke-Cli @('read', '--capture', $capture.captureId, '--offset', [string]$offset, '--limit', '4096', '--format', 'hex')
        }
        $page = $read.data.page
    } while ($true)
    Assert-True ($hexPages.ToString() -ceq [Convert]::ToHexString($raw)) "$Source paged HEX differs from the saved raw RX bytes."
    $captureReports.Add([ordered]@{ source = $Source; captureId = $capture.captureId; captureStatus = $capture.captureStatus;
        sendStatus = $capture.sendStatus; receivedBytes = $capture.receivedBytes; rawBytes = $raw.Length;
        echoOffset = $echoOffset; exactMockEchoBytes = $expectedBytes.Length; pagesRead = $pageCount; rxGapDetected = $capture.rxGapDetected })
}

try {
    $initialState = (Invoke-Cli @('status')).data
    Assert-True (-not $initialState.busy) 'The selected GUI is busy; the smoke test will not interrupt other work.'
    if ($initialState.connected) {
        Assert-MockState $initialState
    }
    else {
        $connection = Invoke-Cli @('connect', '--port', 'MOCK', '--baud', [string]$initialState.baudRate)
        Assert-MockState $connection.data
        $connectedByScript = $true
    }
    $state = (Invoke-Cli @('status')).data
    Assert-MockState $state
    $sessionGeneration = $state.sessionGeneration
    $checks.Add('Selected GUI targets MOCK; no physical COM connection is used.')

    $configuration = Invoke-Cli @('configure', '--port', 'MOCK', '--baud', '250000', '--byte-timeout-ms', '80')
    Assert-MockState $configuration.data -SameSession
    Assert-True ($configuration.data.baudRate -eq 250000 -and $configuration.data.byteTimeoutMs -eq 80) 'CLI configure settings were not applied.'
    $readback = (Invoke-Cli @('status')).data
    Assert-MockState $readback -SameSession
    Assert-True ($readback.baudRate -eq 250000 -and $readback.byteTimeoutMs -eq 80) 'CLI configure status readback differs.'
    $checks.Add('CLI custom baud 250000 and timeout 80 applied live without changing session generation.')

    $mode = Invoke-Cli @('mode', '--set', 'hex')
    Assert-True ($mode.data.mode -eq 'hex') 'CLI did not switch GUI HEX mode.'
    $request = Invoke-Cli @('request', '--port', 'MOCK', '--hex', 'AA 00 01 FF', '--ending', 'crlf', '--duration-ms', '0', '--preview-bytes', '4')
    Assert-Capture $request.data 'cli-hex' ('AA 00 01 FF' + [Environment]::NewLine)
    $checks.Add('CLI HEX TX defaults to HEX preview; all pages equal raw MOCK echo bytes, including with CRLF requested.')

    $mcpProcess = New-CliProcess @('mcp', '--instance', $InstanceId) -Stdio
    $mcpErrorRead = $mcpProcess.StandardError.ReadToEndAsync()
    $mcpRequestId++
    Send-McpMessage @{ jsonrpc = '2.0'; id = $mcpRequestId; method = 'initialize'; params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'gui-smoke'; version = '1' } } }
    $initialized = Read-McpResponse $mcpRequestId
    Assert-True ($initialized.serverInfo.name -eq 'serial-monitor') 'Unexpected MCP server identity.'
    Send-McpMessage @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
    $mcpRequestId++
    Send-McpMessage @{ jsonrpc = '2.0'; id = $mcpRequestId; method = 'tools/list' }
    $toolList = Read-McpResponse $mcpRequestId
    foreach ($requiredTool in @('serial_configure', 'serial_send_and_capture', 'serial_sequence_get', 'serial_sequence_start')) {
        Assert-True ($requiredTool -in $toolList.tools.name) "MCP is missing $requiredTool."
    }
    $configured = Invoke-Mcp 'serial_configure' @{ port = 'MOCK'; baudRate = 57600; byteTimeoutMs = 120 }
    Assert-MockState $configured.data -SameSession
    Assert-True ($configured.data.baudRate -eq 57600 -and $configured.data.byteTimeoutMs -eq 120) 'MCP configure settings were not applied.'
    $checks.Add('MCP initializes, lists current tools, and applies baud 57600 and timeout 120 without changing session generation.')

    $request = Invoke-Mcp 'serial_send_and_capture' @{ port = 'MOCK'; command = '55 00 FF AA'; txMode = 'hex';
        lineEnding = 'crlf'; durationMs = 0; previewBytes = 4; format = 'hex' }
    Assert-Capture $request.data 'mcp-hex' ('55 00 FF AA' + [Environment]::NewLine) -McpPaging
    $checks.Add('MCP HEX TX with explicit format=hex preserves the complete raw MOCK echo across preview and read pages.')

    $definition = [ordered]@{
        name = $sequenceName; repeatCount = 1; steps = @(
            [ordered]@{ name = 'HEX packet echo'; command = 'A5 00 FF 5A'; txMode = 'hex'; lineEnding = 'none'; captureMs = 0; delayAfterMs = 0;
                expect = @{ kind = 'text'; value = 'A5 00 FF 5A' } },
            [ordered]@{ name = 'Terminal echo'; command = 'smoke-terminal'; txMode = 'text'; lineEnding = 'none'; captureMs = 0; delayAfterMs = 0;
                expect = @{ kind = 'text'; value = 'smoke-terminal' } }
        )
    }
    [IO.File]::WriteAllText($sequencePath, ($definition | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false))
    $sequenceSaveAttempted = $true
    $saved = Invoke-Cli @('sequence', 'save', '--file', $sequencePath)
    Assert-True $saved.data.persisted 'CLI did not persist the temporary sequence.'
    $loaded = Invoke-Mcp 'serial_sequence_get' @{ name = $sequenceName }
    Assert-True ($loaded.data.steps.Count -eq 2 -and $loaded.data.steps[0].txMode -eq 'hex' -and $loaded.data.steps[1].txMode -eq 'text') 'MCP did not read back the mixed-mode sequence.'
    $started = Invoke-Mcp 'serial_sequence_start' @{ name = $sequenceName; port = 'MOCK' }
    $runId = $started.data.runId
    Assert-True (-not [string]::IsNullOrWhiteSpace($runId)) 'MCP did not return the background run ID.'
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $run = (Invoke-Cli @('sequence', 'status', '--run', $runId)).data
        if ($run.state -ne 'running') { break }
        Assert-True ([DateTime]::UtcNow -lt $deadline) 'The smoke sequence did not complete within its bounded observation window.'
        Start-Sleep -Milliseconds 100
    } while ($true)
    Assert-True ($run.state -eq 'completed' -and $run.completedSteps -eq 2) 'The mixed-mode sequence did not complete both steps.'
    foreach ($step in $run.steps) {
        Assert-True ($step.sendStatus -eq 'sent' -and $step.matchStatus -eq 'matched') 'A sequence step did not send or match its documented MOCK echo.'
    }
    $read = Invoke-Cli @('read', '--capture', $run.capture.captureId, '--limit', '4', '--format', 'hex')
    Assert-Capture $read.data 'sequence' ('A5 00 FF 5A' + [Environment]::NewLine)
    $raw = [IO.File]::ReadAllBytes($run.capture.rawFile)
    Assert-True ((Find-BytePattern $raw ([Text.Encoding]::UTF8.GetBytes('smoke-terminal' + [Environment]::NewLine))) -ge 0) 'Sequence raw RX does not contain the Terminal echo.'
    $checks.Add('CLI persisted a uniquely named mixed HEX/Terminal sequence; MCP inspected/started it; CLI observed both steps matched and read byte-exact raw pages.')
}
catch { $failure = Get-BoundedMessage $_.Exception.Message }
finally {
    # Only cancel the run created above. Cancellation never undoes a prior TX.
    if ($runId) {
        try {
            $run = (Invoke-Cli @('sequence', 'status', '--run', $runId)).data
            if ($run.state -eq 'running') {
                Invoke-Cli @('sequence', 'cancel', '--run', $runId) | Out-Null
                $deadline = [DateTime]::UtcNow.AddSeconds(10)
                do {
                    $run = (Invoke-Cli @('sequence', 'status', '--run', $runId)).data
                    if ($run.state -ne 'running') { break }
                    if ([DateTime]::UtcNow -ge $deadline) { throw 'Own sequence is still running; later cleanup may remain busy.' }
                    Start-Sleep -Milliseconds 100
                } while ($true)
            }
        }
        catch { $cleanupErrors.Add((Get-BoundedMessage $_.Exception.Message)) }
    }
    if ($null -ne $initialState -and $null -ne $sessionGeneration) {
        try {
            $state = (Invoke-Cli @('status')).data
            Assert-MockState $state -SameSession
            if ($state.baudRate -ne $initialState.baudRate -or $state.byteTimeoutMs -ne $initialState.byteTimeoutMs) {
                Invoke-Cli @('configure', '--port', 'MOCK', '--baud', [string]$initialState.baudRate, '--byte-timeout-ms', [string]$initialState.byteTimeoutMs) | Out-Null
            }
            if ($state.mode -ne $initialState.mode) { Invoke-Cli @('mode', '--set', $initialState.mode) | Out-Null }
            if ($connectedByScript) {
                $state = (Invoke-Cli @('status')).data
                Assert-MockState $state -SameSession
                Invoke-Cli @('disconnect') | Out-Null
            }
            $finalState = (Invoke-Cli @('status')).data
            Assert-True ($finalState.connected -eq $initialState.connected -and $finalState.mode -eq $initialState.mode -and
                $finalState.baudRate -eq $initialState.baudRate -and $finalState.byteTimeoutMs -eq $initialState.byteTimeoutMs) 'Cleanup did not restore the original mode/baud/timeout/connection state.'
            $checks.Add('Original connection state, mode, baud and byte timeout were restored.')
        }
        catch { $cleanupErrors.Add((Get-BoundedMessage $_.Exception.Message)) }
    }
    # Sequence deletion persists the current profile. Restore runtime settings
    # first so that this save cannot retain the smoke test's baud/timeout/mode.
    if ($sequenceSaveAttempted) {
        try {
            $existing = Invoke-Cli @('sequence', 'get', '--name', $sequenceName) -AllowFailure
            if ($existing.success) {
                Invoke-Cli @('sequence', 'delete', '--name', $sequenceName) | Out-Null
                $checks.Add('Temporary sequence was deleted and profile persistence completed.')
            }
            elseif ($existing.error.code -ne 'sequence_not_found') { Assert-Response $existing 'Cleanup sequence lookup' }
        }
        catch { $cleanupErrors.Add((Get-BoundedMessage $_.Exception.Message)) }
    }
    if ($null -ne $mcpProcess) {
        try {
            $mcpProcess.StandardInput.Close()
            if (-not $mcpProcess.WaitForExit(5000)) { $mcpProcess.Kill($true); $mcpProcess.WaitForExit(5000) | Out-Null }
            if ($null -ne $mcpErrorRead -and $mcpErrorRead.IsCompleted) {
                $errorText = $mcpErrorRead.GetAwaiter().GetResult()
                if (-not [string]::IsNullOrWhiteSpace($errorText)) { $cleanupErrors.Add(('MCP stderr: ' + (Get-BoundedMessage $errorText))) }
            }
        }
        catch { $cleanupErrors.Add((Get-BoundedMessage $_.Exception.Message)) }
        finally { $mcpProcess.Dispose() }
    }
    $report = [ordered]@{ success = ($null -eq $failure -and $cleanupErrors.Count -eq 0); completedUtc = [DateTime]::UtcNow.ToString('o');
        instanceId = $InstanceId; cliPath = $CliPath; target = 'MOCK'; physicalDeviceTested = $false;
        mockBehavior = 'MOCK emits UTF-8 formatted TX text plus the Windows newline; it does not echo the original binary UART packet. Background samples may also appear.';
        initialState = $(if ($null -ne $initialState) { Get-StateSummary $initialState } else { $null });
        finalState = $(if ($null -ne $finalState) { Get-StateSummary $finalState } else { $null });
        checks = @($checks); captures = @($captureReports); sequenceName = $sequenceName; runId = $runId;
        error = $failure; cleanupErrors = @($cleanupErrors) }
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false))
}
Write-Output ([pscustomobject]@{ Success = $report.success; Checks = $checks.Count; Captures = $captureReports.Count; ReportPath = $reportPath })
if (-not $report.success) { throw "GUI automation smoke failed. Inspect $reportPath" }
