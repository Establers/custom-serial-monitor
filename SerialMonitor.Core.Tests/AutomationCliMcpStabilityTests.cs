using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SerialMonitor.Cli;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed partial class AutomationCliMcpTests
{
    [Theory]
    [InlineData("disconnect", "--port", "COM9")]
    [InlineData("status", "--hex", "AA55")]
    [InlineData("capture", "--text", "reset")]
    [InlineData("send", "--duration-ms", "100")]
    [InlineData("send", "--text", "--json")]
    [InlineData("mcp", "--port", "COM7")]
    public async Task CliRejectsMisplacedOptionsBeforeAnyControl(string command, string flag, string value)
    {
        await using var gui = await SimulatedGui.StartAsync();
        var (code, result) = await RunCliAsync(command, flag, value, "--instance", gui.InstanceId);
        Assert.Equal(1, code);
        Assert.Equal("cli_error", result.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, gui.Device.SendCount);
        Assert.Equal(0, gui.Device.DisconnectCount);
    }

    [Fact]
    public async Task CliSupportsExplicitOptionLikeTextWithoutConsumingAnotherFlag()
    {
        await using var gui = await SimulatedGui.StartAsync();
        var (code, result) = await RunCliAsync("send", "--text=--json", "--ending", "none", "--instance", gui.InstanceId);
        Assert.Equal(0, code);
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(Encoding.UTF8.GetBytes("--json"), gui.Device.LastTx);
        Assert.Equal(1, gui.Device.SendCount);
    }

    [Theory]
    [InlineData("{\"command\":\"AA55\",\"txmode\":\"hex\"}")]
    [InlineData("{\"command\":\"reset\",\"Port\":\"COM9\"}")]
    [InlineData("{\"command\":\"a\",\"command\":\"b\"}")]
    [InlineData("{\"command\":\"reset\",\"txMode\":null}")]
    [InlineData("{\"command\":\"reset\",\"instanceId\":null}")]
    [InlineData("{\"command\":\"reset\",\"previewBytes\":4.5}")]
    public async Task InvalidArgumentsAreRejectedAtRouterAndGuiBoundariesBeforeTx(string json)
    {
        var args = JsonSerializer.Deserialize<JsonElement>(json);
        var error = await Assert.ThrowsAsync<AutomationException>(() => AutomationToolRouter.CallAsync("send", args, "configured-gui", default));
        Assert.Equal("invalid_argument", error.Code);
        var root = Path.Combine(Path.GetTempPath(), "AutomationArgumentsTests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var captures = new RxCaptureService(root);
            var device = new SimulatedDevice(captures);
            await using var controller = new AutomationController(device, captures);
            var result = await controller.HandleAsync(new("send", args), default);
            Assert.False(result.Success);
            Assert.Equal("invalid_argument", result.Error!.Code);
            Assert.False(device.Reserved);
            Assert.Equal(0, device.SendCount);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("capture_result")]
    [InlineData("release")]
    public async Task CliRetainsTxAndCaptureEvidenceAfterGuiNotificationOrCleanupFailure(string operation)
    {
        await using var gui = await SimulatedGui.StartAsync();
        gui.Device.FailOperation = operation;
        var (code, result) = await RunCliAsync("send", "--hex", "AA55", "--instance", gui.InstanceId);
        Assert.Equal(1, code);
        Assert.False(result.GetProperty("success").GetBoolean());
        var capture = result.GetProperty("data").GetProperty("capture");
        Assert.Equal("sent", capture.GetProperty("sendStatus").GetString());
        Assert.Equal("completed", capture.GetProperty("captureStatus").GetString());
        Assert.True(File.Exists(capture.GetProperty("rawFile").GetString()));
        Assert.Equal(1, gui.Device.SendCount);
        Assert.False(gui.Device.Reserved);
    }

    [Theory]
    [InlineData("hex")]
    [InlineData("text")]
    public async Task CliReadRetainsTheInitialPreviewFormatAcrossProcesses(string format)
    {
        await using var gui = await SimulatedGui.StartAsync();
        gui.Device.ResponseOverride = [0xFF, 0xFF, 0xFF, 0xFF, 0xAA, 0x55];
        var (code, first) = await RunCliAsync("send", "--hex", "AA55", "--format", format, "--preview-bytes", "4", "--instance", gui.InstanceId);
        Assert.Equal(0, code);
        var page = first.GetProperty("data").GetProperty("page");
        var id = page.GetProperty("captureId").GetString()!;
        var offset = page.GetProperty("nextOffset").GetInt64();
        var (readCode, next) = await RunCliAsync("read", "--capture", id, "--offset", offset.ToString(), "--instance", gui.InstanceId);
        Assert.Equal(0, readCode);
        var readPage = next.GetProperty("data").GetProperty("page");
        Assert.Equal(format, readPage.GetProperty("format").GetString());
        if (format == "hex") Assert.Equal("AA55", readPage.GetProperty("text").GetString());
    }

    [Fact]
    public async Task CliRetainsCommittedConfigurationWhenControlReleaseFails()
    {
        await using var gui = await SimulatedGui.StartAsync();
        gui.Device.FailOperation = "release";
        var (code, result) = await RunCliAsync("configure", "--baud", "57600", "--instance", gui.InstanceId);
        Assert.Equal(1, code);
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(57600, result.GetProperty("data").GetProperty("baudRate").GetInt32());
        Assert.Equal(0, gui.Device.SendCount);
        Assert.False(gui.Device.Reserved);
    }

    [Fact]
    public async Task CliPagesLargeSavedCommandsWithoutTruncatingOrBreakingThePipe()
    {
        await using var gui = await SimulatedGui.StartAsync();
        gui.Device.SavedCommandCount = 100;
        var offset = 0;
        do
        {
            var (code, result) = await RunCliAsync("commands", "--offset", offset.ToString(), "--limit", "100", "--instance", gui.InstanceId);
            Assert.Equal(0, code);
            var page = result.GetProperty("data");
            var commands = page.GetProperty("commands").EnumerateArray().ToArray();
            Assert.NotEmpty(commands);
            for (var index = 0; index < commands.Length; index++)
            {
                Assert.Equal("Command " + (offset + index), commands[index].GetProperty("name").GetString());
                Assert.Equal(new string('\u0001', 16384), commands[index].GetProperty("command").GetString());
            }
            offset = page.GetProperty("nextOffset").GetInt32();
            Assert.Equal(offset < 100, page.GetProperty("hasMore").GetBoolean());
        } while (offset < 100);
        Assert.Equal(100, offset);
        var (status, _) = await RunCliAsync("status", "--instance", gui.InstanceId);
        Assert.Equal(0, status);
    }

    [Theory]
    [InlineData("\"a\"", "\"\\u0061\"")]
    [InlineData("12", "12.0")]
    public async Task McpUsesLogicalIdsForBothDuplicateRejectionAndCancellation(string original, string equivalent)
    {
        await using var gui = await SimulatedGui.StartAsync();
        var input = new RpcInput();
        var output = new RpcOutput();
        var run = new StdioMcpServer(gui.InstanceId).RunAsync(input, output, default);
        try
        {
            input.Send(new { jsonrpc = "2.0", id = 0, method = "initialize", @params = new { protocolVersion = "2025-06-18" } });
            await output.NextAsync();
            input.Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
            input.SendRaw("{\"jsonrpc\":\"2.0\",\"id\":" + original + ",\"method\":\"tools/call\",\"params\":{\"name\":\"serial_send_and_capture\",\"arguments\":{\"command\":\"status\",\"durationMs\":60000}}}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (gui.Device.SendCount == 0) await Task.Delay(10, timeout.Token);
            input.SendRaw("{\"jsonrpc\":\"2.0\",\"id\":" + equivalent + ",\"method\":\"tools/call\",\"params\":{\"name\":\"serial_send\",\"arguments\":{\"command\":\"again\"}}}");
            Assert.Equal(-32600, (await output.NextAsync()).GetProperty("error").GetProperty("code").GetInt32());
            input.SendRaw("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":" + equivalent + "}}");
            var canceled = (await output.NextAsync()).GetProperty("result").GetProperty("structuredContent");
            Assert.False(canceled.GetProperty("success").GetBoolean());
            Assert.Equal(1, gui.Device.SendCount);
            while (gui.Device.Reserved) await Task.Delay(10, timeout.Token);
        }
        finally { input.Finish(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task McpProcessRecoversFromAnOversizedToolMessageAndRemainsUsable()
    {
        await using var gui = await SimulatedGui.StartAsync();
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add(typeof(CliCommandInput).Assembly.Location);
        start.ArgumentList.Add("mcp"); start.ArgumentList.Add("--instance"); start.ArgumentList.Add(gui.InstanceId);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}");
            var initialized = JsonSerializer.Deserialize<JsonElement>((await process.StandardOutput.ReadLineAsync(timeout.Token))!);
            Assert.Equal("2025-06-18", initialized.GetProperty("result").GetProperty("protocolVersion").GetString());
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new
            { name = "serial_send", arguments = new { command = new string('A', 128 * 1024) } } }));
            var rejected = JsonSerializer.Deserialize<JsonElement>((await process.StandardOutput.ReadLineAsync(timeout.Token))!);
            Assert.Equal(-32600, rejected.GetProperty("error").GetProperty("code").GetInt32());
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\"}");
            var pong = JsonSerializer.Deserialize<JsonElement>((await process.StandardOutput.ReadLineAsync(timeout.Token))!);
            Assert.Equal(3, pong.GetProperty("id").GetInt32());
            Assert.Equal(0, gui.Device.SendCount);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await process.StandardError.ReadToEndAsync(timeout.Token));
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpEofFinishesEvenWhenAResponseWriterBlocks(bool ignoresCancellation)
    {
        var input = new RpcInput();
        var output = new GatedOutput(ignoresCancellation);
        var run = new StdioMcpServer().RunAsync(input, output, default);
        try
        {
            input.Send(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18" } });
            await output.PassThrough.NextAsync();
            input.Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
            output.Block = true;
            input.Send(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "serial_get_capabilities", arguments = new { } } });
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            input.Finish();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { output.Release.TrySetResult(); input.Finish(); await run.WaitAsync(TimeSpan.FromSeconds(6)); }
    }

    [Fact]
    public async Task McpProcessExitsWhenItsStdoutPipeIsFullAndTheCallerClosesStdin()
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, StandardInputEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add(typeof(CliCommandInput).Assembly.Location);
        start.ArgumentList.Add("mcp");
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(9));
        try
        {
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}");
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            for (var id = 2; id < 18; id++)
                await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/list\"}");
            process.StandardInput.Close();
            // Intentionally never drain stdout before exit: the tools/list reply
            // exceeds the OS pipe buffer, just like a stalled MCP client.
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(1, process.ExitCode);
            Assert.Contains("output", await process.StandardError.ReadToEndAsync(timeout.Token), StringComparison.OrdinalIgnoreCase);
            var written = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            Assert.Contains("serverInfo", written);
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }

    [Fact]
    public async Task McpClosesTheTransportAfterAnOutputDeadlineWithoutWaitingForMoreInput()
    {
        var input = new RpcInput();
        var output = new GatedOutput(true) { Block = true };
        var run = new StdioMcpServer().RunAsync(input, output, default);
        try
        {
            input.Send(new { jsonrpc = "2.0", id = 1, method = "ping" });
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await run.WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally { output.Release.TrySetResult(); input.Finish(); await Record.ExceptionAsync(() => run); }
    }

    [Fact]
    public async Task LineReaderCancellationDoesNotDependOnTheUnderlyingReaderCooperating()
    {
        var input = new NonCooperativeInput();
        using var stop = new CancellationTokenSource();
        var read = new BoundedLineReader(input).ReadRecoverableAsync(stop.Token);
        stop.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(1))); }
        finally { input.Release.TrySetResult(0); }
    }

    [Fact]
    public async Task KillingAnActiveCliCaptureReleasesControlWithoutReplayingTx()
    {
        await using var gui = await SimulatedGui.StartAsync();
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { typeof(CliCommandInput).Assembly.Location, "request", "--text", "status", "--duration-ms", "60000", "--instance", gui.InstanceId })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (gui.Device.SendCount == 0) await Task.Delay(10, timeout.Token);
            process.Kill(true);
            await process.WaitForExitAsync(timeout.Token);
            while (gui.Device.Reserved) await Task.Delay(10, timeout.Token);
            var (code, _) = await RunCliAsync("status", "--instance", gui.InstanceId);
            Assert.Equal(0, code);
            Assert.Equal(1, gui.Device.SendCount);
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }

    private sealed class NonCooperativeInput : TextReader
    {
        public TaskCompletionSource<int> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) => new(Release.Task);
    }

    private sealed class GatedOutput(bool ignoresCancellation) : TextWriter
    {
        public RpcOutput PassThrough { get; } = new();
        public bool Block { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (!Block) return PassThrough.WriteLineAsync(buffer, cancellationToken);
            Entered.TrySetResult();
            return ignoresCancellation ? Release.Task : Release.Task.WaitAsync(cancellationToken);
        }
    }
}
