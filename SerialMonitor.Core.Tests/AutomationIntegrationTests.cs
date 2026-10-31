using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SerialMonitor.Cli;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed class AutomationIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SerialMonitorAutomationTests", Guid.NewGuid().ToString("N"));
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value, AutomationJson.Options);

    [Fact]
    public async Task PipeReturnsActualCaptureAndReleasesControl()
    {
        await using var captures = new RxCaptureService(Path.Combine(_root, "captures"));
        var device = new Device(captures);
        var controller = new AutomationController(device, captures);
        var errors = new List<Exception>();
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), controller.HandleAsync, errors.Add,
            Path.Combine(_root, "instances"));
        host.Start();
        // A direct in-process RPC can complete before the separate async
        // discovery publication. External CLI clients select discovery first.
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (AutomationDiscovery.List(Path.Combine(_root, "instances")).Count == 0) await Task.Delay(5, startup.Token);
        var result = await AutomationPipeClient.CallAsync(host.Instance, "send_and_capture", new { command = "status", durationMs = 10 }, default);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("abc1d234\r\n> ", result.Data.GetProperty("page").GetProperty("text").GetString());
        Assert.Equal("sent", result.Data.GetProperty("capture").GetProperty("sendStatus").GetString());
        Assert.False(device.Reserved);
        Assert.Equal(1, device.SendCount);
        Assert.Empty(errors);
        Assert.Single(AutomationDiscovery.List(Path.Combine(_root, "instances")));
    }

    [Fact]
    public async Task PipeClientCancellationReleasesReservationAndKeepsServerAlive()
    {
        await using var captures = new RxCaptureService(Path.Combine(_root, "captures"));
        var device = new Device(captures);
        var controller = new AutomationController(device, captures);
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), controller.HandleAsync, _ => { },
            Path.Combine(_root, "instances"));
        host.Start();
        using var cancel = new CancellationTokenSource();
        var request = AutomationPipeClient.CallAsync(host.Instance, "send_and_capture", new { command = "status", durationMs = 60000 }, cancel.Token);
        await device.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await device.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await AutomationPipeClient.CallAsync(host.Instance, "status", new { }, default);
        Assert.True(result.Success);
        Assert.False(device.Reserved);
    }

    [Fact]
    public async Task SmallPreviewPreservesTheRestOfActualRxForPaging()
    {
        await using var captures = new RxCaptureService(Path.Combine(_root, "captures"));
        var device = new Device(captures);
        var controller = new AutomationController(device, captures);
        var result = await controller.HandleAsync(new("send_and_capture", Args(new { command = "status", durationMs = 0, previewBytes = 4 })), default);
        Assert.True(result.Success);
        var page = result.Data.GetProperty("page");
        Assert.Equal("abc1", page.GetProperty("text").GetString());
        Assert.True(page.GetProperty("hasMore").GetBoolean());
        var remaining = await captures.ReadAsync(page.GetProperty("captureId").GetString()!, page.GetProperty("nextOffset").GetInt64());
        Assert.Equal("abc1d234\r\n> ", page.GetProperty("text").GetString() + remaining.Text);
        Assert.Equal(1, device.SendCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65537)]
    public async Task InvalidPreviewSizeCannotSendOrReserveDevice(int previewBytes)
    {
        await using var captures = new RxCaptureService(Path.Combine(_root, "captures"));
        var device = new Device(captures);
        var result = await new AutomationController(device, captures).HandleAsync(
            new("send_and_capture", Args(new { command = "status", previewBytes })), default);
        Assert.False(result.Success);
        Assert.Equal("invalid_argument", result.Error!.Code);
        Assert.Equal(0, device.SendCount);
        Assert.False(device.Reserved);
    }

    [Fact]
    public async Task McpInitializesAndListsToolsWithoutRunningGui()
    {
        var input = new TestInput();
        var output = new TestOutput();
        var run = new StdioMcpServer().RunAsync(input, output, default);
        input.Send(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } } });
        var initialized = await output.Next();
        Assert.Equal("2025-06-18", initialized.GetProperty("result").GetProperty("protocolVersion").GetString());
        input.Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
        input.Send(new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        var tools = (await output.Next()).GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        Assert.Contains(tools, tool => tool.GetProperty("name").GetString() == "serial_send_and_capture");
        Assert.Contains(tools, tool => tool.GetProperty("name").GetString() == "serial_read_capture");
        var setMode = Assert.Single(tools, tool => tool.GetProperty("name").GetString() == "serial_set_mode");
        var schema = setMode.GetProperty("inputSchema");
        Assert.Contains(schema.GetProperty("required").EnumerateArray(), item => item.GetString() == "mode");
        Assert.Equal(new[] { "terminal", "hex" }, schema.GetProperty("properties").GetProperty("mode").GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()));
        input.Finish();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task McpCanCaptureFromGuiPipeAndCancelLongRunningCapture()
    {
        // Use the actual discovery directory so the same MCP router used by
        // shipped CLI binaries discovers this simulated GUI host.
        await using var captures = new RxCaptureService(Path.Combine(_root, "captures"));
        var device = new Device(captures);
        var controller = new AutomationController(device, captures);
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), controller.HandleAsync, _ => { });
        host.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!AutomationDiscovery.List().Any(i => i.InstanceId == host.Instance.InstanceId)) await Task.Delay(10, deadline.Token);
        var input = new TestInput();
        var output = new TestOutput();
        var run = new StdioMcpServer(host.Instance.InstanceId).RunAsync(input, output, deadline.Token);
        try
        {
            input.Send(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18" } });
            await output.Next();
            input.Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
            input.Send(new { jsonrpc = "2.0", id = 10, method = "tools/call", @params = new { name = "serial_set_mode", arguments = new { mode = "hex" } } });
            var modeResult = (await output.Next()).GetProperty("result");
            Assert.False(modeResult.GetProperty("isError").GetBoolean());
            Assert.Equal("hex", modeResult.GetProperty("structuredContent").GetProperty("data").GetProperty("mode").GetString());
            Assert.Equal(0, device.SendCount);
            input.Send(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "serial_send_and_capture", arguments = new { command = "status", durationMs = 10 } } });
            var result = (await output.Next()).GetProperty("result");
            Assert.False(result.GetProperty("isError").GetBoolean());
            Assert.Equal("abc1d234\r\n> ", result.GetProperty("structuredContent").GetProperty("data").GetProperty("page").GetProperty("text").GetString());
            input.Send(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "serial_send_and_capture", arguments = new { command = "status", durationMs = 60000 } } });
            while (device.SendCount < 2) await Task.Delay(10, deadline.Token);
            input.Send(new { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = 3 } });
            var canceled = await output.Next();
            Assert.Equal(3, canceled.GetProperty("id").GetInt32());
            Assert.True(canceled.GetProperty("result").GetProperty("isError").GetBoolean());
            while (device.Reserved) await Task.Delay(10, deadline.Token);
        }
        finally { input.Finish(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private sealed class Device(RxCaptureService captures) : IAutomationDevice
    {
        public CaptureConnection Connection => new(true, 1, 0);
        public volatile bool Reserved;
        public int SendCount;
        private string _mode = "terminal";
        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<object> InvokeAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operation == "reserve")
            {
                if (Reserved) throw new AutomationException("busy", "Reserved.");
                Reserved = true;
            }
            if (operation == "release") { Reserved = false; Released.TrySetResult(); }
            if (operation == "set_mode")
            {
                Assert.True(Reserved);
                _mode = arguments.GetProperty("mode").GetString()!;
            }
            if (operation == "send")
            {
                Interlocked.Increment(ref SendCount);
                captures.Offer(1, "abc1d234\r\n> "u8.ToArray(), Stopwatch.GetTimestamp());
                Sent.TrySetResult();
            }
            return Task.FromResult<object>(new AutomationDeviceState(true, "COM7", 115200, 1, "Utf8", 0, Reserved, null, Mode: _mode));
        }
    }

    private sealed class TestInput : TextReader
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

    private sealed class TestOutput : TextWriter
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        { _lines.Writer.TryWrite(buffer.ToString()); return Task.CompletedTask; }
        public async Task<JsonElement> Next()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return JsonSerializer.Deserialize<JsonElement>(await _lines.Reader.ReadAsync(timeout.Token));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
