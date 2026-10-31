using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SerialMonitor.Cli;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed class AutomationMalformedInputTests
{
    [Fact]
    public async Task NoResponseMayFollowAPartiallyWrittenBrokenOutputLine()
    {
        using var input = new StringReader("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"serial_get_capabilities","arguments":{}}}
            {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"serial_get_capabilities","arguments":{}}}

            """);
        using var output = new PartiallyBrokenOutput();
        await new StdioMcpServer().RunAsync(input, output, default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, output.Writes);
        Assert.EndsWith("partial", output.ToString());
    }

    [Fact]
    public async Task EofPreservesACompletedToolReplyWhileStillBoundingOutputDrain()
    {
        using var input = new StringReader("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"serial_get_capabilities","arguments":{}}}

            """);
        using var output = new DelayedOutput();
        await new StdioMcpServer().RunAsync(input, output, default).WaitAsync(TimeSpan.FromSeconds(3));
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        var reply = JsonSerializer.Deserialize<JsonElement>(lines[1]);
        Assert.Equal(2, reply.GetProperty("id").GetInt32());
        Assert.False(reply.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task EveryAdvertisedToolRejectsWrongJsonTypesAndUnknownArgumentsWithoutSelectingAGui()
    {
        await using var rpc = new Rpc();
        await rpc.InitializeAsync();
        rpc.Input.Send("""{"jsonrpc":"2.0","id":10,"method":"tools/list"}""");
        var tools = (await rpc.NextAsync()).GetProperty("result").GetProperty("tools");
        var requestId = 1000;
        var checkedInputs = 0;
        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString()!;
            var baseline = new Dictionary<string, JsonElement>();
            void Set(string field, object value) => baseline[field] = JsonSerializer.SerializeToElement(value);
            switch (name)
            {
                case "serial_connect": Set("port", "COM7"); break;
                case "serial_configure": Set("baudRate", 115200); break;
                case "serial_set_mode": Set("mode", "terminal"); break;
                case "serial_send": case "serial_send_and_capture": Set("command", "status"); break;
                case "serial_read_capture": Set("captureId", new string('a', 32)); break;
                case "serial_sequence_get": case "serial_sequence_delete": case "serial_sequence_start": Set("name", "test"); break;
                case "serial_sequence_cancel": Set("runId", new string('a', 32)); break;
                case "serial_sequence_save":
                    Set("sequence", new { name = "test", repeatCount = 1, steps = new[] { new { command = "status" } } });
                    break;
            }
            async Task RejectAsync(object? arguments)
            {
                rpc.Input.Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = requestId++, method = "tools/call",
                    @params = new { name, arguments } }));
                var response = await rpc.NextAsync();
                if (response.TryGetProperty("error", out var protocolError))
                    Assert.True(protocolError.GetProperty("code").GetInt32() == -32602,
                        $"Unexpected rejection for {name}, arguments={JsonSerializer.Serialize(arguments)}: {response}");
                else
                {
                    var result = response.GetProperty("result").GetProperty("structuredContent");
                    Assert.False(result.GetProperty("success").GetBoolean());
                    Assert.Equal("invalid_argument", result.GetProperty("error").GetProperty("code").GetString());
                }
                checkedInputs++;
            }
            foreach (var json in new[] { "null", "true", "123", "\"text\"", "[]" })
                await RejectAsync(JsonSerializer.Deserialize<JsonElement>(json));
            await RejectAsync(new Dictionary<string, object> { ["misspelledArgument"] = true });
            foreach (var field in tool.GetProperty("inputSchema").GetProperty("properties").EnumerateObject())
            {
                var type = field.Value.GetProperty("type").GetString();
                var invalid = type switch
                {
                    "string" => new[] { "null", "0", "false", "[]", "{}" },
                    "integer" => ["null", "true", "\"1\"", "[]", "{}", "1.5", "2147483648", "1e100"],
                    "boolean" => ["null", "0", "\"true\"", "[]", "{}"],
                    "object" => ["null", "0", "\"text\"", "[]", "true"],
                    _ => throw new InvalidOperationException("Unhandled advertised property type: " + type)
                };
                foreach (var json in invalid)
                {
                    var arguments = new Dictionary<string, JsonElement>(baseline) { [field.Name] = JsonSerializer.Deserialize<JsonElement>(json) };
                    await RejectAsync(arguments);
                }
            }
        }
        Assert.True(checkedInputs > 400, "Keep the malformed-input matrix covering every tool.");
        await rpc.PingAsync();
    }

    [Theory]
    [InlineData("{\"requestId\":\"own\",\"requestId\":\"other\"}")]
    [InlineData("{\"requestId\":\"other\",\"requestID\":\"own\"}")]
    [InlineData("{\"requestId\":\"other\",\"reason\":null}")]
    public async Task MalformedCancellationCannotCancelAnotherRequestsToken(string parameters)
    {
        var server = new StdioMcpServer();
        // Install two outstanding requests without accessing a GUI or UART.
        var calls = (ConcurrentDictionary<string, CancellationTokenSource>)typeof(StdioMcpServer)
            .GetField("_calls", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(server)!;
        using var own = new CancellationTokenSource();
        using var other = new CancellationTokenSource();
        calls["s:own"] = own;
        calls["s:other"] = other;
        var input = new InputLines();
        var output = new OutputLines();
        var run = server.RunAsync(input, output, default);
        try
        {
            input.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":" + parameters + "}");
            input.Send("""{"jsonrpc":"2.0","id":1,"method":"ping"}""");
            await output.Lines.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(own.IsCancellationRequested);
            Assert.False(other.IsCancellationRequested);
            input.Send("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"own"}}""");
            input.Send("""{"jsonrpc":"2.0","id":2,"method":"ping"}""");
            await output.Lines.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(own.IsCancellationRequested);
            Assert.False(other.IsCancellationRequested);
        }
        finally { input.Lines.Writer.TryComplete(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Theory]
    [InlineData("2", "2.0")]
    [InlineData("100", "1e2")]
    [InlineData("\"a\"", "\"\\u0061\"")]
    [InlineData("123456789012345678901234567890000", "12345678901234567890123456789e4")]
    public async Task CompletedRequestIdsCannotExecuteAnotherToolCall(string firstId, string repeatedId)
    {
        await using var rpc = new Rpc();
        await rpc.InitializeAsync();
        string Request(string id) => "{\"jsonrpc\":\"2.0\",\"id\":" + id +
            ",\"method\":\"tools/call\",\"params\":{\"name\":\"serial_get_capabilities\",\"arguments\":{}}}";
        rpc.Input.Send(Request(firstId));
        Assert.False((await rpc.NextAsync()).GetProperty("result").GetProperty("isError").GetBoolean());
        // Let the response writer finish; this checks replay after completion,
        // rather than the separate guard for simultaneous in-flight requests.
        await Task.Delay(50);
        rpc.Input.Send(Request(repeatedId));
        Assert.Equal(-32600, (await rpc.NextAsync()).GetProperty("error").GetProperty("code").GetInt32());
        await rpc.PingAsync();
    }

    [Theory]
    [InlineData("0.00000000000000000000000000001")]
    [InlineData("1.00000000000000000000000000001")]
    [InlineData("79228162514264337593543950335.1")]
    public async Task FractionalIdsCannotRoundIntoAnotherRequestsIntegerId(string id)
    {
        await using var rpc = new Rpc();
        rpc.Input.Send("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"ping\"}");
        Assert.Equal(-32600, (await rpc.NextAsync()).GetProperty("error").GetProperty("code").GetInt32());
        await rpc.PingAsync();
    }

    [Theory]
    [InlineData("1234567890123456789012345678901234567890")]
    [InlineData("1e100")]
    [InlineData("100.00e-2")]
    [InlineData("-0.0")]
    public async Task IntegerIdsRemainExactWithoutDependingOnDecimalPrecision(string id)
    {
        await using var rpc = new Rpc();
        rpc.Input.Send("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"ping\"}");
        var response = await rpc.NextAsync();
        Assert.True(response.TryGetProperty("result", out _));
        Assert.Equal(id, response.GetProperty("id").GetRawText());
    }

    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"\\ud800\",\"method\":\"ping\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"\\udfff\"}")]
    [InlineData("{\"jsonrpc\":\"\\ud800\",\"id\":2,\"method\":\"ping\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"\\ud800\"}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\",\"\\ud800\":0}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\",\"params\":{\"nested\":[\"\\ud800\"]}}")]
    public async Task InvalidUnicodeCannotTerminateMcp(string malformed)
    {
        await using var rpc = new Rpc();
        rpc.Input.Send(malformed);
        var error = await rpc.NextAsync();
        Assert.True(error.TryGetProperty("error", out _));
        await rpc.PingAsync();
    }

    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"method\":\"ping\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":2,\"id\":3,\"method\":\"ping\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"serial_get_capabilities\"},\"arguments\":{\"command\":\"ignored\"}}")]
    public async Task AmbiguousRpcEnvelopeIsRejected(string malformed)
    {
        await using var rpc = new Rpc();
        await rpc.InitializeAsync();
        rpc.Input.Send(malformed);
        Assert.Equal(-32600, (await rpc.NextAsync()).GetProperty("error").GetProperty("code").GetInt32());
        await rpc.PingAsync();
    }

    [Theory]
    [InlineData("{\"name\":\"serial_get_capabilities\",\"argumetns\":{\"command\":\"ignored\"}}")]
    [InlineData("{\"name\":\"serial_get_capabilities\",\"name\":\"serial_get_capabilities\",\"arguments\":{}}")]
    [InlineData("{\"name\":\"serial_get_capabilities\",\"arguments\":{},\"arguments\":{}}")]
    [InlineData("{\"name\":\"serial_get_capabilities\",\"arguments\":{},\"_meta\":null}")]
    public async Task AmbiguousOrMisspelledToolParametersAreNotExecuted(string parameters)
    {
        await using var rpc = new Rpc();
        await rpc.InitializeAsync();
        rpc.Input.Send("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":" + parameters + "}");
        Assert.Equal(-32602, (await rpc.NextAsync()).GetProperty("error").GetProperty("code").GetInt32());
        await rpc.PingAsync();
    }

    [Fact]
    public async Task ValidUnicodeAndToolMetadataRemainUsable()
    {
        await using var rpc = new Rpc();
        await rpc.InitializeAsync();
        rpc.Input.Send("""{"jsonrpc":"2.0","id":"장비 😀 �","method":"tools/call","params":{"name":"serial_get_capabilities","arguments":{},"_meta":{"progressToken":"확인"}}}""");
        var result = await rpc.NextAsync();
        Assert.Equal("장비 😀 �", result.GetProperty("id").GetString());
        Assert.False(result.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Theory]
    [InlineData("{\"operation\":\"status\",\"operation\":\"disconnect\",\"arguments\":{}}")]
    [InlineData("{\"operation\":\"disconnect\",\"arguments\":{},\"argumetns\":{\"port\":\"COM9\"}}")]
    [InlineData("{\"operation\":\"disconnect\",\"arguments\":{},\"arguments\":{}}")]
    [InlineData("{\"operation\":null,\"arguments\":{}}")]
    [InlineData("{\"operation\":\"disconnect\"}")]
    [InlineData("{\"operation\":\"\\ud800\",\"arguments\":{}}")]
    [InlineData("{\"operation\":\"status\",\"arguments\":{\"nested\":[\"\\ud800\"]}}")]
    [InlineData("invalid-utf8")]
    public async Task MalformedPipeEnvelopeNeverReachesDeviceHandler(string malformed)
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(AutomationMalformedInputTests), Guid.NewGuid().ToString("N"));
        var calls = 0;
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(AutomationResponse.Ok(new { }));
        }, _ => { }, root);
        host.Start();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var pipe = new NamedPipeClientStream(".", host.Instance.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(deadline.Token);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            if (malformed == "invalid-utf8")
            {
                var prefix = Encoding.UTF8.GetBytes("{\"operation\":\"status\",\"arguments\":{\"text\":\"");
                var suffix = Encoding.UTF8.GetBytes("\"}}\n");
                await pipe.WriteAsync(prefix.Concat(new byte[] { 0xff }).Concat(suffix).ToArray(), deadline.Token);
            }
            else await writer.WriteLineAsync(malformed.AsMemory(), deadline.Token);
            var line = await reader.ReadLineAsync(deadline.Token);
            Assert.NotNull(line);
            var error = JsonSerializer.Deserialize<AutomationResponse>(line, AutomationJson.Options)!;
            Assert.False(error.Success);
            Assert.Equal("invalid_argument", error.Error!.Code);
            Assert.Equal(0, calls);
            var valid = await AutomationPipeClient.CallAsync(host.Instance, "status", new { }, deadline.Token);
            Assert.True(valid.Success);
            Assert.Equal(1, calls);
        }
        finally
        {
            await host.StopAsync(default);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CorruptUtf8CannotBeSilentlyRewrittenIntoAToolCall()
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(CliCommandInput).Assembly.Location);
        start.ArgumentList.Add("mcp");
        using var child = Process.Start(start)!;
        var output = ProcessStreamReader.ReadToEndAsync(child.StandardOutput);
        var error = ProcessStreamReader.ReadToEndAsync(child.StandardError);
        var prefix = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":\"");
        var suffix = Encoding.UTF8.GetBytes("\",\"method\":\"ping\"}\n");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await child.StandardInput.BaseStream.WriteAsync(prefix, deadline.Token);
            await child.StandardInput.BaseStream.WriteAsync(new byte[] { 0xff }, deadline.Token);
            await child.StandardInput.BaseStream.WriteAsync(suffix, deadline.Token);
            await child.StandardInput.BaseStream.FlushAsync(deadline.Token);
            child.StandardInput.Close();
            await child.WaitForExitAsync(deadline.Token);
            var text = await output;
            Assert.DoesNotContain("\"result\"", text);
            Assert.DoesNotContain("�", text);
            await error;
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
    }

    private sealed class Rpc : IAsyncDisposable
    {
        public InputLines Input { get; } = new();
        private readonly OutputLines _output = new();
        private readonly Task _run;
        public Rpc() => _run = new StdioMcpServer().RunAsync(Input, _output, default);
        public async Task<JsonElement> NextAsync()
        {
            var next = _output.Lines.Reader.ReadAsync().AsTask();
            if (await Task.WhenAny(_run, next).WaitAsync(TimeSpan.FromSeconds(5)) == _run)
                await _run; // Surface process-ending parser exceptions immediately.
            return JsonSerializer.Deserialize<JsonElement>(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        public async Task InitializeAsync()
        {
            Input.Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
            Assert.True((await NextAsync()).TryGetProperty("result", out _));
            Input.Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        }
        public async Task PingAsync()
        {
            Input.Send("""{"jsonrpc":"2.0","id":99,"method":"ping"}""");
            Assert.Equal(99, (await NextAsync()).GetProperty("id").GetInt32());
        }
        public async ValueTask DisposeAsync()
        {
            Input.Lines.Writer.TryComplete();
            // The assertion above owns parser failures, avoiding a masking dispose exception.
            try { await _run.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { }
        }
    }

    private sealed class InputLines : TextReader
    {
        public Channel<string> Lines { get; } = Channel.CreateUnbounded<string>();
        private string _current = "";
        private int _position;
        public void Send(string line) => Lines.Writer.TryWrite(line + "\n");
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (_position == _current.Length)
            {
                if (!await Lines.Reader.WaitToReadAsync(cancellationToken)) return 0;
                _current = await Lines.Reader.ReadAsync(cancellationToken);
                _position = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _position);
            _current.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }

    private sealed class OutputLines : TextWriter
    {
        public Channel<string> Lines { get; } = Channel.CreateUnbounded<string>();
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        { Lines.Writer.TryWrite(buffer.ToString()); return Task.CompletedTask; }
    }

    private sealed class DelayedOutput : StringWriter
    {
        private int _writes;
        public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writes) > 1) await Task.Delay(30, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await base.WriteLineAsync(buffer, cancellationToken);
        }
    }

    private sealed class PartiallyBrokenOutput : StringWriter
    {
        public int Writes;
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref Writes) == 2)
            {
                Write("partial");
                throw new IOException("Broken output after a partial line.");
            }
            return base.WriteLineAsync(buffer, cancellationToken);
        }
    }
}
