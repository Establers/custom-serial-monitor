using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SerialMonitor.Cli;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed class AutomationCliMcpTests
{
    [Fact]
    public async Task CapabilitiesExposeCurrentToolSchemasWithoutSelectingAGui()
    {
        var (code, described) = await RunCliAsync("describe", "--json");
        Assert.Equal(0, code);
        Assert.True(described.GetProperty("success").GetBoolean());
        var cli = described.GetProperty("data");
        Assert.Equal(1, cli.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("1.0", cli.GetProperty("controlApiVersion").GetString());
        Assert.Equal(typeof(CliCommandInput).Assembly.GetName().Version!.ToString(3), cli.GetProperty("cliVersion").GetString());
        Assert.Equal("1.0.0", cli.GetProperty("minimumPluginVersion").GetString());
        Assert.Contains("serialctl describe", cli.GetProperty("cliHelp").GetString());
        Assert.Contains("serialctl configure", cli.GetProperty("cliHelp").GetString());

        // This default does not identify a GUI. Metadata must still be available
        // so an incompatible installation can be diagnosed before device access.
        await using var mcp = await McpSession.StartAsync("not-a-running-gui");
        var response = await mcp.CallAsync("serial_get_capabilities", new { });
        Assert.False(response.GetProperty("isError").GetBoolean());
        var metadata = response.GetProperty("structuredContent").GetProperty("data");
        Assert.Equal(cli.GetRawText(), metadata.GetRawText());
        var tools = metadata.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Contains(tools, tool => tool.GetProperty("name").GetString() == "serial_sequence_start");
        var discovery = tools.Single(tool => tool.GetProperty("name").GetString() == "serial_get_capabilities");
        Assert.Empty(discovery.GetProperty("inputSchema").GetProperty("properties").EnumerateObject());
    }

    [Fact]
    public async Task CapabilitiesRejectTargetArgumentsInsteadOfImplyingGuiCompatibility()
    {
        var (code, described) = await RunCliAsync("describe", "--instance", "not-a-running-gui");
        Assert.Equal(1, code);
        Assert.False(described.GetProperty("success").GetBoolean());
        await using var mcp = await McpSession.StartAsync("not-a-running-gui");
        var response = await mcp.CallAsync("serial_get_capabilities", new { instanceId = "not-a-running-gui" });
        Assert.True(response.GetProperty("isError").GetBoolean());
        Assert.Equal("invalid_argument", response.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExecutableConfiguresBothSettingsAndOneSettingWithoutSending()
    {
        await using var gui = await SimulatedGui.StartAsync();
        var (code, result) = await RunCliAsync("configure", "--baud", "250000", "--byte-timeout-ms", "80",
            "--port", "COM7", "--instance", gui.InstanceId);
        Assert.Equal(0, code);
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(250000, result.GetProperty("data").GetProperty("baudRate").GetInt32());
        Assert.Equal(80, result.GetProperty("data").GetProperty("byteTimeoutMs").GetInt32());

        var (timeoutCode, timeout) = await RunCliAsync("configure", "--byte-timeout-ms", "125", "--instance", gui.InstanceId);
        Assert.Equal(0, timeoutCode);
        Assert.Equal(250000, timeout.GetProperty("data").GetProperty("baudRate").GetInt32());
        Assert.Equal(125, timeout.GetProperty("data").GetProperty("byteTimeoutMs").GetInt32());

        var (baudCode, baud) = await RunCliAsync("configure", "--baud", "9600", "--instance", gui.InstanceId);
        Assert.Equal(0, baudCode);
        Assert.Equal(9600, baud.GetProperty("data").GetProperty("baudRate").GetInt32());
        Assert.Equal(125, baud.GetProperty("data").GetProperty("byteTimeoutMs").GetInt32());
        Assert.Equal(0, gui.Device.SendCount);
        Assert.False(gui.Device.Reserved);
    }

    [Theory]
    [InlineData()]
    [InlineData("--byte-timeout-ms", "0")]
    [InlineData("--byte-timeout-ms", "5001")]
    [InlineData("--byte-timeout-ms", "2.5")]
    [InlineData("--baud", "0")]
    [InlineData("--baud", "10000001")]
    [InlineData("--baud", "2147483648")]
    [InlineData("--baud", "9600", "--text", "status")]
    public async Task ExecutableRejectsInvalidConfigurationBeforeContactingAnyGui(params string[] options)
    {
        var (code, result) = await RunCliAsync(["configure", .. options, "--instance", "instance-does-not-exist"]);
        Assert.Equal(1, code);
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("cli_error", result.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExecutableHexRequestAndPagingPreserveBinaryBytesWithOneTx()
    {
        await using var gui = await SimulatedGui.StartAsync();
        var (code, result) = await RunCliAsync("request", "--hex", "AA 00 01 FF", "--ending", "crlf",
            "--duration-ms", "0", "--preview-bytes", "4", "--port", "COM7", "--instance", gui.InstanceId);
        Assert.Equal(0, code);
        var data = result.GetProperty("data");
        var page = data.GetProperty("page");
        Assert.Equal("hex", page.GetProperty("format").GetString());
        Assert.Equal("00FF800D", page.GetProperty("text").GetString());
        Assert.Equal(4, page.GetProperty("nextOffset").GetInt64());
        Assert.Equal(SimulatedDevice.ResponseBytes.Length, page.GetProperty("totalBytes").GetInt64());
        Assert.True(page.GetProperty("hasMore").GetBoolean());
        Assert.Equal(new byte[] { 0xAA, 0x00, 0x01, 0xFF }, gui.Device.LastTx);
        Assert.Equal("terminal", gui.Device.Mode);
        Assert.Equal(SimulatedDevice.ResponseBytes, await File.ReadAllBytesAsync(data.GetProperty("capture").GetProperty("rawFile").GetString()!));

        var captureId = page.GetProperty("captureId").GetString()!;
        var (_, middleResult) = await RunCliAsync("read", "--capture", captureId, "--offset", "4", "--limit", "4", "--format", "hex", "--instance", gui.InstanceId);
        var middle = middleResult.GetProperty("data").GetProperty("page");
        Assert.Equal("0A7F01AB", middle.GetProperty("text").GetString());
        Assert.True(middle.GetProperty("hasMore").GetBoolean());
        var (_, tailResult) = await RunCliAsync("read", "--capture", captureId, "--offset", "8", "--limit", "4", "--format", "hex", "--instance", gui.InstanceId);
        var tail = tailResult.GetProperty("data").GetProperty("page");
        Assert.Equal("AA55", tail.GetProperty("text").GetString());
        Assert.False(tail.GetProperty("hasMore").GetBoolean());
        Assert.Equal(1, gui.Device.SendCount);
        Assert.False(gui.Device.Reserved);
    }

    [Fact]
    public async Task ExecutableHonorsExplicitHexResponseFormatForTextTx()
    {
        await using var gui = await SimulatedGui.StartAsync();
        var (code, result) = await RunCliAsync("request", "--text", "status", "--ending", "none",
            "--format", "hex", "--duration-ms", "0", "--instance", gui.InstanceId);
        Assert.Equal(0, code);
        Assert.Equal("hex", result.GetProperty("data").GetProperty("page").GetProperty("format").GetString());
        Assert.Equal(Convert.ToHexString(SimulatedDevice.ResponseBytes), result.GetProperty("data").GetProperty("page").GetProperty("text").GetString()!.Replace(" ", ""));
        Assert.Equal("status"u8.ToArray(), gui.Device.LastTx);
    }

    [Fact]
    public async Task McpConfiguresGuiAndReadsBinaryHexPagesWithoutResending()
    {
        await using var gui = await SimulatedGui.StartAsync();
        await using var mcp = await McpSession.StartAsync(gui.InstanceId);
        var configuration = await mcp.CallAsync("serial_configure", new { baudRate = 460800, byteTimeoutMs = 90, port = "COM7" });
        Assert.False(configuration.GetProperty("isError").GetBoolean());
        var configured = configuration.GetProperty("structuredContent").GetProperty("data");
        Assert.Equal(460800, configured.GetProperty("baudRate").GetInt32());
        Assert.Equal(90, configured.GetProperty("byteTimeoutMs").GetInt32());
        Assert.Equal(0, gui.Device.SendCount);

        var request = await mcp.CallAsync("serial_send_and_capture", new
        {
            command = "AA 00 01 FF", txMode = "hex", lineEnding = "crlf", durationMs = 0, previewBytes = 4, port = "COM7"
        });
        Assert.False(request.GetProperty("isError").GetBoolean());
        var data = request.GetProperty("structuredContent").GetProperty("data");
        var page = data.GetProperty("page");
        Assert.Equal("hex", page.GetProperty("format").GetString());
        Assert.Equal("00FF800D", page.GetProperty("text").GetString());
        Assert.Equal("sent", data.GetProperty("capture").GetProperty("sendStatus").GetString());
        var read = await mcp.CallAsync("serial_read_capture", new
        {
            captureId = page.GetProperty("captureId").GetString(), offset = page.GetProperty("nextOffset").GetInt64(), format = "hex"
        });
        var remaining = read.GetProperty("structuredContent").GetProperty("data").GetProperty("page");
        Assert.Equal("0A7F01ABAA55", remaining.GetProperty("text").GetString());
        Assert.False(remaining.GetProperty("hasMore").GetBoolean());
        Assert.Equal(1, gui.Device.SendCount);
        Assert.Equal(new byte[] { 0xAA, 0x00, 0x01, 0xFF }, gui.Device.LastTx);

        var explicitText = await mcp.CallAsync("serial_send_and_capture", new { command = "AA", txMode = "hex", format = "text", durationMs = 0 });
        Assert.Equal("text", explicitText.GetProperty("structuredContent").GetProperty("data").GetProperty("page").GetProperty("format").GetString());
        Assert.False(gui.Device.Reserved);
    }

    [Fact]
    public async Task McpDescribesConfigurationBoundsAndHexPreviewOverrides()
    {
        using var input = new StringReader("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/list"}

            """);
        using var output = new StringWriter();
        await new StdioMcpServer().RunAsync(input, output, default);
        var listed = JsonSerializer.Deserialize<JsonElement>(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[1]);
        var tools = listed.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        var schema = tools.Single(tool => tool.GetProperty("name").GetString() == "serial_configure").GetProperty("inputSchema");
        var properties = schema.GetProperty("properties");
        Assert.Equal(1, properties.GetProperty("baudRate").GetProperty("minimum").GetInt32());
        Assert.Equal(10000000, properties.GetProperty("baudRate").GetProperty("maximum").GetInt32());
        Assert.Equal(1, properties.GetProperty("byteTimeoutMs").GetProperty("minimum").GetInt32());
        Assert.Equal(5000, properties.GetProperty("byteTimeoutMs").GetProperty("maximum").GetInt32());
        Assert.Equal(new[] { "baudRate", "byteTimeoutMs" }, schema.GetProperty("anyOf").EnumerateArray().Select(option => option.GetProperty("required")[0].GetString()));
        foreach (var name in new[] { "serial_send", "serial_send_and_capture", "serial_capture_rx" })
        {
            var format = tools.Single(tool => tool.GetProperty("name").GetString() == name).GetProperty("inputSchema").GetProperty("properties").GetProperty("format");
            Assert.Equal(new[] { "text", "hex" }, format.GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        }
    }

    [Fact]
    public async Task CliRegistersNamedHexAndTerminalSequenceThatMcpCanInspectAndRun()
    {
        await using var gui = await SimulatedGui.StartAsync();
        await using var mcp = await McpSession.StartAsync(gui.InstanceId);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, """
                {"name":"장비 상태 확인","repeatCount":1,"steps":[
                  {"name":"HEX 상태 패킷","command":"AA 00 01 FF","txMode":"hex","lineEnding":"none","captureMs":0,"delayAfterMs":0,"expect":{"kind":"hex","value":"00 FF 80 0D 0A 7F 01 AB AA 55"}},
                  {"name":"Terminal 상태 명령","command":"status","txMode":"text","lineEnding":"none","captureMs":0,"delayAfterMs":0}
                ]}
                """);
            var (saveCode, saved) = await RunCliAsync("sequence", "save", "--file", path, "--instance", gui.InstanceId);
            Assert.Equal(0, saveCode);
            Assert.True(saved.GetProperty("success").GetBoolean());
            var loaded = await mcp.CallAsync("serial_sequence_get", new { name = "장비 상태 확인" });
            Assert.False(loaded.GetProperty("isError").GetBoolean());
            var definition = loaded.GetProperty("structuredContent").GetProperty("data");
            Assert.Equal("HEX 상태 패킷", definition.GetProperty("steps")[0].GetProperty("name").GetString());
            Assert.Equal("AA 00 01 FF", definition.GetProperty("steps")[0].GetProperty("command").GetString());
            Assert.Equal("text", definition.GetProperty("steps")[1].GetProperty("txMode").GetString());
            Assert.Equal(0, gui.Device.SendCount);

            var started = await mcp.CallAsync("serial_sequence_start", new { name = "장비 상태 확인", port = "COM7" });
            Assert.False(started.GetProperty("isError").GetBoolean());
            var runId = started.GetProperty("structuredContent").GetProperty("data").GetProperty("runId").GetString()!;
            JsonElement run;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            do
            {
                var (statusCode, status) = await RunCliAsync("sequence", "status", "--run", runId, "--instance", gui.InstanceId);
                Assert.Equal(0, statusCode);
                run = status.GetProperty("data");
                if (run.GetProperty("state").GetString() == "running") await Task.Delay(10, timeout.Token);
            } while (run.GetProperty("state").GetString() == "running");
            Assert.Equal("completed", run.GetProperty("state").GetString());
            Assert.Equal(2, run.GetProperty("completedSteps").GetInt32());
            Assert.Equal("matched", run.GetProperty("steps")[0].GetProperty("matchStatus").GetString());
            Assert.Equal(2, gui.Device.SendCount);
            Assert.Equal("status"u8.ToArray(), gui.Device.LastTx);
            Assert.False(gui.Device.Reserved);
        }
        finally { File.Delete(path); }
    }

    private static async Task<(int ExitCode, JsonElement Result)> RunCliAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(CliCommandInput).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        var error = await stderr;
        Assert.True(string.IsNullOrEmpty(error), error);
        return (process.ExitCode, JsonSerializer.Deserialize<JsonElement>(await stdout));
    }

    private sealed class SimulatedGui : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "SerialMonitorCliMcpTests", Guid.NewGuid().ToString("N"));
        private readonly RxCaptureService _captures;
        private readonly AutomationController _controller;
        private readonly AutomationPipeHost _host;
        public SimulatedDevice Device { get; }
        public string InstanceId => _host.Instance.InstanceId;

        private SimulatedGui()
        {
            _captures = new(Path.Combine(_root, "captures"));
            Device = new(_captures);
            _controller = new(Device, _captures);
            _host = new("instance-" + Guid.NewGuid().ToString("N"), _controller.HandleAsync, _ => { });
        }

        public static async Task<SimulatedGui> StartAsync()
        {
            var gui = new SimulatedGui();
            gui._host.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                while (!AutomationDiscovery.List().Any(instance => instance.InstanceId == gui.InstanceId))
                    await Task.Delay(10, timeout.Token);
                return gui;
            }
            catch { await gui.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            await _host.DisposeAsync();
            await _controller.DisposeAsync();
            await _captures.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class SimulatedDevice(RxCaptureService captures) : IAutomationDevice
    {
        public static readonly byte[] ResponseBytes = [0x00, 0xFF, 0x80, 0x0D, 0x0A, 0x7F, 0x01, 0xAB, 0xAA, 0x55];
        public CaptureConnection Connection => new(true, 1, 0);
        public bool Reserved { get; private set; }
        public int SendCount { get; private set; }
        public byte[] LastTx { get; private set; } = [];
        public string Mode => "terminal";
        private int _baudRate = 115200;
        private int _byteTimeoutMs = 40;
        private readonly Dictionary<string, AutomationSequenceDefinition> _sequences = new(StringComparer.Ordinal);

        public Task<object> InvokeAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (operation)
            {
                case "reserve":
                    if (Reserved) throw new AutomationException("busy", "Reserved.");
                    Reserved = true;
                    break;
                case "release": Reserved = false; break;
                case "configure":
                    Assert.True(Reserved);
                    var configuration = AutomationSerialConfiguration.Parse(arguments);
                    _baudRate = configuration.BaudRate ?? _baudRate;
                    _byteTimeoutMs = configuration.ByteTimeoutMs ?? _byteTimeoutMs;
                    break;
                case "sequence_save":
                    Assert.True(Reserved);
                    var definition = AutomationSequenceValidation.Parse(arguments.GetProperty("sequence"));
                    _sequences[definition.Name] = definition;
                    break;
                case "sequence_get":
                    return Task.FromResult<object>(_sequences[arguments.GetProperty("name").GetString()!]);
                case "send":
                    Assert.True(Reserved);
                    SendCount++;
                    var command = arguments.GetProperty("command").GetString()!;
                    LastTx = arguments.String("txMode") == "hex"
                        ? Convert.FromHexString(string.Concat(command.Where(character => !char.IsWhiteSpace(character))))
                        : Encoding.UTF8.GetBytes(command);
                    // Deliberately fragment a binary response across arbitrary host reads.
                    captures.Offer(1, ResponseBytes[..1], Stopwatch.GetTimestamp());
                    captures.Offer(1, ResponseBytes[1..7], Stopwatch.GetTimestamp());
                    captures.Offer(1, ResponseBytes[7..], Stopwatch.GetTimestamp());
                    break;
            }
            return Task.FromResult<object>(new AutomationDeviceState(true, "COM7", _baudRate, 1, "Utf8", 0, Reserved, null,
                Mode: Mode, ByteTimeoutMs: _byteTimeoutMs));
        }
    }

    private sealed class McpSession : IAsyncDisposable
    {
        private readonly RpcInput _input = new();
        private readonly RpcOutput _output = new();
        private readonly Task _run;
        private int _requestId = 1;

        private McpSession(string instanceId) => _run = new StdioMcpServer(instanceId).RunAsync(_input, _output, default);

        public static async Task<McpSession> StartAsync(string instanceId)
        {
            var session = new McpSession(instanceId);
            session._input.Send(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18" } });
            await session._output.NextAsync();
            session._input.Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
            return session;
        }

        public async Task<JsonElement> CallAsync(string name, object arguments)
        {
            var id = ++_requestId;
            _input.Send(new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name, arguments } });
            var response = await _output.NextAsync();
            Assert.Equal(id, response.GetProperty("id").GetInt32());
            return response.GetProperty("result");
        }

        public async ValueTask DisposeAsync()
        {
            _input.Finish();
            await _run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class RpcInput : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        private string _current = "";
        private int _offset;
        public void Send(object value) => _lines.Writer.TryWrite(JsonSerializer.Serialize(value, AutomationJson.Options) + "\n");
        public void Finish() => _lines.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset == _current.Length)
            {
                if (!await _lines.Reader.WaitToReadAsync(cancellationToken)) return 0;
                _current = await _lines.Reader.ReadAsync(cancellationToken);
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
    }

    private sealed class RpcOutput : TextWriter
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        { _lines.Writer.TryWrite(buffer.ToString()); return Task.CompletedTask; }
        public async Task<JsonElement> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return JsonSerializer.Deserialize<JsonElement>(await _lines.Reader.ReadAsync(timeout.Token));
        }
    }
}
