using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed class AutomationPipeClientReplyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SplitFourByteUnicodeAtNativeBoundaryDoesNotNeedPeerEof(bool requestSide)
    {
        var instance = Instance();
        var prefix = requestSide ? "{\"operation\":\"send\",\"arguments\":{\"command\":\"" : "{\"success\":true,\"data\":{\"text\":\"";
        const string suffix = "\"}}\n";
        var first = 4096 - Encoding.UTF8.GetByteCount(prefix) - 3;
        var second = 8192 - Encoding.UTF8.GetByteCount(prefix + suffix) - first - 4;
        var text = new string('x', first) + "😀" + new string('x', second);
        var bytes = Encoding.UTF8.GetBytes(prefix + text + suffix);
        Assert.Equal(8192, bytes.Length);
        Assert.Equal(new byte[] { 0xf0, 0x9f, 0x98, 0x80 }, bytes[4093..4097]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        if (requestSide)
        {
            await using var host = new AutomationPipeHost(instance.InstanceId, (request, _) =>
            {
                Assert.Equal(text, request.Arguments.GetProperty("command").GetString());
                return Task.FromResult(AutomationResponse.Ok(new { handled = true }));
            }, _ => { });
            host.Start();
            using var pipe = new NamedPipeClientStream(".", instance.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(deadline.Token);
            await pipe.WriteAsync(bytes, deadline.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true);
            Assert.True(JsonSerializer.Deserialize<AutomationResponse>((await reader.ReadLineAsync(deadline.Token))!, AutomationJson.Options)!.Success);
        }
        else
        {
            using var pipe = new NamedPipeServerStream(instance.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var connected = pipe.WaitForConnectionAsync(deadline.Token);
            var call = AutomationPipeClient.CallAsync(instance, "status", new { }, deadline.Token);
            await connected;
            using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true);
            Assert.NotNull(await reader.ReadLineAsync(deadline.Token));
            await pipe.WriteAsync(bytes, deadline.Token);
            Assert.Equal(text, (await call).Data.GetProperty("text").GetString());
        }
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(8192)]
    public async Task ClientReadsCompleteUnicodeReplyWhileServerWaitsForClientEof(int wireBytes)
    {
        var instance = Instance();
        using var pipe = new NamedPipeServerStream(instance.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var connected = pipe.WaitForConnectionAsync(deadline.Token);
        var call = AutomationPipeClient.CallAsync(instance, "status", new { }, deadline.Token);
        await connected;
        using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true);
        Assert.NotNull(await reader.ReadLineAsync(deadline.Token));
        var (text, bytes) = UnicodeFrame(value => JsonSerializer.Serialize(AutomationResponse.Ok(new { text = value }), AutomationJson.Options), wireBytes);
        await pipe.WriteAsync(bytes, deadline.Token);
        // Keep the server open until the client has consumed the response and
        // closed its endpoint. No peer EOF may be needed to recognize the line.
        var reply = await call;
        Assert.True(reply.Success);
        Assert.Equal(text, reply.Data.GetProperty("text").GetString());
        Assert.Null(await reader.ReadLineAsync(deadline.Token));
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(8192)]
    public async Task HostHandlesCompleteUnicodeRequestWhileClientWaitsForReply(int wireBytes)
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(AutomationPipeClientReplyTests), Guid.NewGuid().ToString("N"));
        var calls = 0;
        string? received = null;
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), (request, _) =>
        {
            received = request.Arguments.GetProperty("command").GetString();
            Interlocked.Increment(ref calls);
            return Task.FromResult(AutomationResponse.Ok(new { handled = true }));
        }, _ => { }, root);
        host.Start();
        try
        {
            using var pipe = new NamedPipeClientStream(".", host.Instance.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await pipe.ConnectAsync(deadline.Token);
            var (text, bytes) = UnicodeFrame(value => JsonSerializer.Serialize(new AutomationRequest("send",
                JsonSerializer.SerializeToElement(new { command = value })), AutomationJson.Options), wireBytes);
            await pipe.WriteAsync(bytes, deadline.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true);
            var reply = JsonSerializer.Deserialize<AutomationResponse>((await reader.ReadLineAsync(deadline.Token))!, AutomationJson.Options)!;
            Assert.True(reply.Success);
            Assert.Equal(1, calls);
            Assert.Equal(text, received);
        }
        finally
        {
            await host.StopAsync(default);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static (string Text, byte[] Bytes) UnicodeFrame(Func<string, string> serialize, int wireBytes)
    {
        var padding = wireBytes - Encoding.UTF8.GetByteCount(serialize("") + "\n");
        var text = new string('가', padding / 3) + new string('x', padding % 3);
        var bytes = Encoding.UTF8.GetBytes(serialize(text) + "\n");
        Assert.Equal(wireBytes, bytes.Length);
        return (text, bytes);
    }

    [Fact]
    public async Task CompleteUnicodeReplyFollowedByEofDoesNotFailDuringClientCleanup()
    {
        var instance = Instance();
        using var pipe = new NamedPipeServerStream(instance.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connected = pipe.WaitForConnectionAsync(deadline.Token);
        var call = AutomationPipeClient.CallAsync(instance, "status", new { }, deadline.Token);
        await connected;
        using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true);
        Assert.Equal("status", JsonSerializer.Deserialize<AutomationRequest>((await reader.ReadLineAsync(deadline.Token))!, AutomationJson.Options)!.Operation);
        var emptyBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(AutomationResponse.Ok(new { text = "" }), AutomationJson.Options) + "\n");
        var text = "가" + new string('x', 4096 - emptyBytes - 3);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(AutomationResponse.Ok(new { text }), AutomationJson.Options) + "\n");
        Assert.Equal(4096, bytes.Length);
        await pipe.WriteAsync(bytes, deadline.Token);
        // StreamReader can need EOF to finish this short character read even
        // though the native byte buffer and the JSON line are both complete.
        pipe.Dispose();
        var reply = await call;
        Assert.True(reply.Success);
        Assert.Equal(text, reply.Data.GetProperty("text").GetString());
    }

    [Fact]
    public async Task DisconnectBeforeAReplyKeepsTheOriginalErrorInsteadOfAWriterFlushError()
    {
        var instance = Instance();
        using var pipe = new NamedPipeServerStream(instance.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connected = pipe.WaitForConnectionAsync(deadline.Token);
        var call = AutomationPipeClient.CallAsync(instance, "status", new { }, deadline.Token);
        await connected;
        using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true);
        Assert.NotNull(await reader.ReadLineAsync(deadline.Token));
        pipe.Dispose();
        var error = await Assert.ThrowsAsync<IOException>(() => call);
        Assert.Equal("GUI disconnected.", error.Message);
    }

    private static AutomationInstance Instance()
    {
        using var process = Process.GetCurrentProcess();
        var id = "instance-" + Guid.NewGuid().ToString("N");
        return new(id, "serialmonitor-" + id, Environment.ProcessId, new DateTimeOffset(process.StartTime.ToUniversalTime()));
    }
}
