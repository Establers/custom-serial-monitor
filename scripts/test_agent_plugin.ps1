[CmdletBinding()]
param([string]$CliPath)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$pluginRoot = Join-Path $repoRoot 'plugins\serial-monitor'
$packager = Join-Path $PSScriptRoot 'package_agent_plugin.ps1'
$scratch = Join-Path $repoRoot ('artifacts\plugin-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$passed = 0

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Read-Json([string]$Path) { return [IO.File]::ReadAllText($Path) | ConvertFrom-Json }
function Run-Launcher([string]$Root, [string]$Executable, [string]$Mode, [string]$InputText = '', [hashtable]$Environment = @{}) {
    $launcher = Join-Path $Root 'scripts\Start-SerialMonitorMcp.ps1'
    foreach ($value in @($launcher, $Executable)) {
        if ($value.Contains('"')) { throw 'Test paths must not contain double quotes.' }
    }
    $arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $launcher +
        '" -CliPath "' + $Executable + '" -Mode ' + $Mode
    $start = [Diagnostics.ProcessStartInfo]::new($powershell, $arguments)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($name in $Environment.Keys) { $start.EnvironmentVariables[$name] = $Environment[$name] }
    # .NET Framework takes redirected stdin's encoding from Console.InputEncoding
    # during Start; ProcessStartInfo.StandardInputEncoding is unavailable on PS5.
    # A UTF-8 BOM would turn the first initialize request into a JSON parse error.
    $previousInputEncoding = [Console]::InputEncoding
    try {
        [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
        $process = [Diagnostics.Process]::Start($start)
    }
    finally { [Console]::InputEncoding = $previousInputEncoding }
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorText = $process.StandardError.ReadToEndAsync()
        if ($InputText) { $process.StandardInput.Write($InputText) }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(20000)) {
            $process.Kill()
            $process.WaitForExit(1000) | Out-Null
            throw "Plugin launcher exceeded the test deadline (mode=$Mode, CLI=$Executable)."
        }
        Assert-True ([Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($output, $errorText), 2000)) 'Launcher streams remained open.'
        return [PSCustomObject]@{ ExitCode = $process.ExitCode; Output = $output.Result; Error = $errorText.Result }
    }
    finally {
        if (-not $process.HasExited) { $process.Kill() }
        $process.Dispose()
    }
}

# Git-installed copies must already be complete; do not silently fix source drift
# in CI before checking it.
$null = & $packager -Check
$manifest = Read-Json (Join-Path $pluginRoot 'plugin.json')
$compatibility = Read-Json (Join-Path $pluginRoot 'compatibility.json')
$marketplace = Read-Json (Join-Path $repoRoot '.claude-plugin\marketplace.json')
Assert-True ($manifest.'$schema' -eq 'https://agent-plugins.org/schemas/1.0.0/plugin.schema.json') 'Portable plugin schema is missing.'
Assert-True ($marketplace.plugins[0].source -eq './plugins/serial-monitor') 'Marketplace does not reference the packaged plugin.'
foreach ($relative in @('mcp.json', 'mcp.codex.json', '.mcp.json')) {
    $config = Read-Json (Join-Path $pluginRoot $relative)
    $server = $config.mcpServers.'serial-monitor'
    Assert-True ($server.type -eq 'stdio' -and $server.command -eq 'powershell.exe') 'MCP launch transport is not local stdio.'
    $expectedVariable = if ($relative -eq '.mcp.json') { '${CLAUDE_PLUGIN_ROOT}' } else { '${PLUGIN_ROOT}' }
    Assert-True ($server.args[-1] -eq ($expectedVariable + '/scripts/Start-SerialMonitorMcp.ps1')) 'Wrong client root expansion in MCP launch arguments.'
    Assert-True ($server.args -contains '-NoProfile' -and $server.args -contains '-NonInteractive') 'MCP launcher can load interactive user profiles.'
}
$passed++

$package = & $packager -OutputDirectory (Join-Path $scratch 'package')
Assert-True (Test-Path -LiteralPath $package.ChecksumPath -PathType Leaf) 'Plugin checksum is missing.'
$archive = [IO.Compression.ZipFile]::OpenRead($package.ZipPath)
try {
    $entries = @($archive.Entries | ForEach-Object FullName)
    foreach ($required in @('.claude-plugin/marketplace.json', 'README.md', 'LICENSE', 'docs/agent_plugins.md', 'docs/ai_control.md',
        'plugins/serial-monitor/plugin.json', 'plugins/serial-monitor/mcp.json', 'plugins/serial-monitor/.mcp.json',
        'plugins/serial-monitor/.claude-plugin/plugin.json', 'plugins/serial-monitor/.codex-plugin/plugin.json',
        'plugins/serial-monitor/scripts/Start-SerialMonitorMcp.ps1',
        'plugins/serial-monitor/skills/serial-monitor-control/SKILL.md')) {
        Assert-True ($entries -contains ('SerialMonitorPlugin/' + $required)) "ZIP is missing a required/hidden file: $required"
    }
    Assert-True (@($entries | Where-Object { $_ -match '\.(exe|dll|pdb|bin)$|serial-monitor\.local\.json|\.serial-monitor-skill\.json' }).Count -eq 0) 'Plugin ZIP contains app binaries or machine-specific settings.'
    Assert-True (@($entries | Where-Object { $_ -match '(^|/)\.\./|^[A-Za-z]:|^/' }).Count -eq 0) 'ZIP contains an escaping path.'
}
finally { $archive.Dispose() }
foreach ($markdownFile in @(Get-ChildItem -LiteralPath $package.StageDirectory -Recurse -Force -Filter '*.md' -File)) {
    $markdown = [IO.File]::ReadAllText($markdownFile.FullName, [Text.Encoding]::UTF8)
    foreach ($match in [regex]::Matches($markdown, '\[[^\]]*\]\(([^\)]+)\)')) {
        $target = $match.Groups[1].Value
        if ($target -match '^(?:[a-zA-Z][a-zA-Z0-9+.-]*:|#)') { continue }
        $target = [uri]::UnescapeDataString(($target -split '[#?]')[0])
        $localTarget = [IO.Path]::GetFullPath((Join-Path $markdownFile.DirectoryName $target))
        Assert-True (Test-Path -LiteralPath $localTarget) "Plugin Markdown link is broken: $($markdownFile.FullName) -> $target"
    }
}
$expectedHash = (Get-FileHash -LiteralPath $package.ZipPath -Algorithm SHA256).Hash
Assert-True (([IO.File]::ReadAllText($package.ChecksumPath)).StartsWith($expectedHash)) 'Plugin checksum does not match the archive.'
$passed++

# Compile a controlled CLI fixture into a path containing shell metacharacters.
# The launcher must pass paths as arguments, never interpret them as shell code.
$unicode = ([string][char]0xD55C) + [char]0xAE00
$specialDirectory = Join-Path $scratch ($unicode + ' '' $literal ` [custom] ; path')
New-Item -ItemType Directory -Force -Path $specialDirectory | Out-Null
$mockCli = Join-Path $specialDirectory 'serialctl.exe'
$mockSource = @'
using System;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Threading;
public static class MockSerialMonitorCli {
    public static int Main(string[] args) {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length > 0 && args[0] == "describe") {
            var marker = Environment.GetEnvironmentVariable("SERIAL_MONITOR_TEST_PID");
            if (!String.IsNullOrEmpty(marker)) File.WriteAllText(marker, Process.GetCurrentProcess().Id.ToString());
            if (Environment.GetEnvironmentVariable("SERIAL_MONITOR_TEST_HANG") == "true") Thread.Sleep(10000);
            Console.WriteLine(Environment.GetEnvironmentVariable("SERIAL_MONITOR_TEST_METADATA"));
            return 0;
        }
        if (args.Length > 0 && args[0] == "mcp") Console.WriteLine("MCP_SHOULD_NOT_START_FOR_INVALID_METADATA");
        return 0;
    }
}
'@
$compiledMock = Join-Path $scratch 'fixture-serialctl.exe'
Add-Type -TypeDefinition $mockSource -OutputAssembly $compiledMock -OutputType ConsoleApplication
Copy-Item -LiteralPath $compiledMock -Destination $mockCli
$metadata = @{ success = $true; data = @{ schemaVersion = 1; controlApiVersion = '1.0'; cliVersion = '1.6.0'; minimumPluginVersion = '1.0.0' } }
$goodJson = $metadata | ConvertTo-Json -Depth 5 -Compress
$result = Run-Launcher $pluginRoot $mockCli 'describe' '' @{ SERIAL_MONITOR_TEST_METADATA = $goodJson }
Assert-True ($result.ExitCode -eq 0 -and $result.Error -eq '') ('Valid CLI probe failed: ' + $result.Error)
Assert-True (($result.Output | ConvertFrom-Json).data.cliVersion -eq '1.6.0') 'Probe stdout contains startup text or lost JSON.'
$passed++

foreach ($invalid in @('old-cli', 'future-api', 'future-plugin', 'bad-json')) {
    $metadata.data.cliVersion = '1.6.0'
    $metadata.data.controlApiVersion = '1.0'
    $metadata.data.minimumPluginVersion = '1.0.0'
    switch ($invalid) {
        'old-cli' { $metadata.data.cliVersion = '1.5.2' }
        'future-api' { $metadata.data.controlApiVersion = '2.0' }
        'future-plugin' { $metadata.data.minimumPluginVersion = '2.0.0' }
    }
    $json = if ($invalid -eq 'bad-json') { 'old application help output' } else { $metadata | ConvertTo-Json -Depth 5 -Compress }
    $result = Run-Launcher $pluginRoot $mockCli 'mcp' '' @{ SERIAL_MONITOR_TEST_METADATA = $json }
    Assert-True ($result.ExitCode -ne 0 -and $result.Output -eq '' -and $result.Error.Contains('Serial Monitor plugin:')) "Invalid metadata started MCP or contaminated stdout: $invalid"
    $passed++
}

$result = Run-Launcher $pluginRoot (Join-Path $scratch 'missing.exe') 'mcp'
Assert-True ($result.ExitCode -ne 0 -and $result.Output -eq '' -and $result.Error.Length -gt 0) 'Missing executable did not fail clearly on stderr.'
$passed++

# A hung compatibility probe has a bounded lifetime and leaves no child behind.
$timeoutPlugin = Join-Path $scratch 'timeout-plugin'
Copy-Item -LiteralPath $pluginRoot -Destination $timeoutPlugin -Recurse -Force
$shortCompatibility = Read-Json (Join-Path $timeoutPlugin 'compatibility.json')
$shortCompatibility.probeTimeoutMs = 200
[IO.File]::WriteAllText((Join-Path $timeoutPlugin 'compatibility.json'), ($shortCompatibility | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
$pidMarker = Join-Path $scratch 'probe-pid.txt'
$result = Run-Launcher $timeoutPlugin $mockCli 'mcp' '' @{ SERIAL_MONITOR_TEST_HANG = 'true'; SERIAL_MONITOR_TEST_PID = $pidMarker }
Assert-True ($result.ExitCode -ne 0 -and $result.Output -eq '' -and $result.Error.Contains('timed out')) 'Hung probe was not bounded.'
Assert-True (Test-Path -LiteralPath $pidMarker -PathType Leaf) 'Timed probe fixture did not start.'
$probeProcessId = [int][IO.File]::ReadAllText($pidMarker)
Assert-True (-not (Get-Process -Id $probeProcessId -ErrorAction SilentlyContinue)) 'Timed out probe leaked its CLI process.'
$passed++

# Local sources must resolve their own app without changing environment, registry
# or clients. The fake file is resolved but never executed in this check.
$localApp = Join-Path $scratch 'portable application'
$localPlugin = Join-Path $localApp 'agent-plugin\plugins\serial-monitor'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $localPlugin) | Out-Null
Copy-Item -LiteralPath $pluginRoot -Destination $localPlugin -Recurse -Force
New-Item -ItemType Directory -Force -Path (Join-Path $localApp 'tools') | Out-Null
$nearbyCli = Join-Path $localApp 'tools\serialctl.exe'
Copy-Item -LiteralPath $mockCli -Destination $nearbyCli
$previousOverride = $env:SERIAL_MONITOR_CLI
try {
    $env:SERIAL_MONITOR_CLI = $null
    $resolved = & (Join-Path $localPlugin 'scripts\Resolve-SerialMonitorCli.ps1')
    Assert-True ($resolved -eq $nearbyCli) 'Local marketplace did not prefer its nearby application.'
    $env:SERIAL_MONITOR_CLI = Join-Path $scratch 'missing-override.exe'
    $failed = $false
    try { & (Join-Path $localPlugin 'scripts\Resolve-SerialMonitorCli.ps1') | Out-Null } catch { $failed = $true }
    Assert-True $failed 'An invalid explicit environment override silently selected another installation.'
}
finally { $env:SERIAL_MONITOR_CLI = $previousOverride }
$passed++

function Resolve-WithRunningApps([string[]]$ApplicationPaths, [string]$ExplicitPath = '') {
    # The mock only lives in this function scope. The actual resolver is still
    # invoked as a script, and no registry/process/client settings are mutated.
    function Get-Process {
        param([string]$Name, $ErrorAction)
        foreach ($application in $ApplicationPaths) { [PSCustomObject]@{ Path = $application } }
    }
    & (Join-Path $pluginRoot 'scripts\Resolve-SerialMonitorCli.ps1') -CliPath $ExplicitPath
}
$runningApps = @()
foreach ($name in @('running A', 'running B')) {
    $applicationRoot = Join-Path $scratch $name
    New-Item -ItemType Directory -Force -Path (Join-Path $applicationRoot 'tools') | Out-Null
    $application = Join-Path $applicationRoot 'SerialMonitor.WinUI.exe'
    [IO.File]::WriteAllBytes($application, [byte[]]@(77, 90))
    Copy-Item -LiteralPath $mockCli -Destination (Join-Path $applicationRoot 'tools\serialctl.exe')
    $runningApps += $application
}
$previousOverride = $env:SERIAL_MONITOR_CLI
try {
    $env:SERIAL_MONITOR_CLI = $null
    $expected = Join-Path (Split-Path -Parent $runningApps[0]) 'tools\serialctl.exe'
    Assert-True ((Resolve-WithRunningApps @($runningApps[0])) -eq $expected) 'Cached plugin could not locate the unique running portable GUI.'
    Assert-True ((Resolve-WithRunningApps @($runningApps[0], $runningApps[0])) -eq $expected) 'Two GUI instances from one app directory were treated as ambiguous binaries.'
    $failed = $false
    try { Resolve-WithRunningApps $runningApps | Out-Null } catch { $failed = $true }
    Assert-True $failed 'Different running application folders were selected arbitrarily.'
    Assert-True ((Resolve-WithRunningApps $runningApps $mockCli) -eq $mockCli) 'Explicit binary selection did not override multiple running apps.'
}
finally { $env:SERIAL_MONITOR_CLI = $previousOverride }
$passed++

if (-not $CliPath) {
    foreach ($configuration in @('Debug', 'Release')) {
        $candidate = Join-Path $repoRoot "SerialMonitor.Cli\bin\$configuration\net8.0\serialctl.exe"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $CliPath = $candidate; break }
    }
}
if (-not $CliPath -or -not (Test-Path -LiteralPath $CliPath -PathType Leaf)) {
    throw 'Build SerialMonitor.Cli first or pass -CliPath for the end-to-end MCP launcher test.'
}
$CliPath = (Resolve-Path -LiteralPath $CliPath).ProviderPath
$result = Run-Launcher $pluginRoot $CliPath 'describe'
Assert-True ($result.ExitCode -eq 0 -and $result.Error -eq '') ('Real CLI compatibility probe failed: ' + $result.Error)
Assert-True (($result.Output | ConvertFrom-Json).data.controlApiVersion -eq '1.0') 'Real CLI metadata was not forwarded unchanged.'
$passed++

$requests = @(
    @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'plugin-test'; version = '1.0.0' } } },
    @{ jsonrpc = '2.0'; method = 'notifications/initialized' },
    @{ jsonrpc = '2.0'; id = 2; method = 'ping' },
    @{ jsonrpc = '2.0'; id = 3; method = 'tools/list' },
    @{ jsonrpc = '2.0'; id = 4; method = 'tools/call'; params = @{ name = 'serial_get_capabilities'; arguments = @{} } }
)
$inputText = (@($requests | ForEach-Object { $_ | ConvertTo-Json -Depth 8 -Compress }) -join "`n") + "`n"
# Reproduce GitHub Actions' Windows PowerShell console default on every machine.
$previousInputEncoding = [Console]::InputEncoding
try {
    [Console]::InputEncoding = [Text.UTF8Encoding]::new($true)
    $result = Run-Launcher $pluginRoot $CliPath 'mcp' $inputText
}
finally { [Console]::InputEncoding = $previousInputEncoding }
Assert-True ($result.ExitCode -eq 0 -and $result.Error -eq '') ('End-to-end MCP launcher failed: ' + $result.Error)
$responses = @($result.Output -split '\r?\n' | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
Assert-True ($responses.Count -eq 4) 'MCP launcher added stdout text, lost responses or failed to preserve EOF.'
$rpcErrors = @($responses | Where-Object { $_.error })
Assert-True ($rpcErrors.Count -eq 0) ('MCP launcher returned JSON-RPC errors: ' + ($rpcErrors | ConvertTo-Json -Depth 6 -Compress))
$tools = @($responses | Where-Object id -eq 3)[0].result.tools
Assert-True (@($tools | Where-Object name -eq 'serial_get_capabilities').Count -eq 1) 'Live discovery tool is missing from MCP.'
$capabilityResponse = @($responses | Where-Object id -eq 4)[0].result
Assert-True (-not $capabilityResponse.isError) 'Read-only capability discovery failed through MCP.'
$passed++

Write-Output "Plugin packaging, compatibility and stdio tests passed: $passed"
