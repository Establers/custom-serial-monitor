using System.Text.Json;
using System.Text;
using System.Diagnostics;
using SerialMonitor.Core.Automation;
using Xunit.Abstractions;

namespace SerialMonitor.Core.Tests;

public sealed class AutomationSchedulingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("raw", false)]
    [InlineData("dedicated", true)]
    public async Task RedirectedChildReadersDoNotOccupyTheGuiFixturesWorker(string adapter, bool expectedHeartbeat)
    {
        await using var probe = new TestProbeProcess("process-reader", adapter);
        var result = await probe.ReadAsync();
        output.WriteLine(result.ToString());
        Assert.False(result.GetProperty("asyncHandle").GetBoolean());
        Assert.Equal(expectedHeartbeat, result.GetProperty("heartbeatRan").GetBoolean());
    }

    [Fact]
    public async Task SlowProcessPreparationDoesNotMasqueradeAsAThreeSecondGuiConnectionTimeout()
    {
        var requests = 0;
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), (request, _) =>
        {
            Assert.Equal("cancel_capture", request.Operation);
            Interlocked.Increment(ref requests);
            return Task.FromResult(AutomationResponse.Ok(new { canceled = false }));
        }, _ => { });
        host.Start();
        var preparedAt = Stopwatch.StartNew();
        await using var probe = new TestProbeProcess("prepared-cli", "3500", "cancel", "--instance", host.Instance.InstanceId);
        var ready = probe.ReadAsync(TimeSpan.FromSeconds(12));
        // Demonstrate the old end-to-end measurement failing even though no GUI
        // connection has been attempted. This is not a longer connection budget.
        await Assert.ThrowsAsync<TimeoutException>(() => ready.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True((await ready).GetProperty("ready").GetBoolean());
        Assert.Equal(0, requests);
        var preparationMs = preparedAt.Elapsed.TotalMilliseconds;
        var command = Stopwatch.StartNew();
        probe.InvokePreparedCli();
        var reply = await probe.ReadAsync(TimeSpan.FromSeconds(3));
        Assert.True(reply.GetProperty("success").GetBoolean(), reply.ToString());
        Assert.Equal(1, requests);
        output.WriteLine($"Controlled startup {preparationMs:F0}ms exceeds the old total 3-second check without any request; actual CLI cancel {command.Elapsed.TotalMilliseconds:F0}ms under the unchanged 3-second command deadline; no TX.");
    }

    [Fact]
    public async Task ActualMcpWithOneWorkerServicesEightGuiCallsWhileStdinIsIdle()
    {
        var calls = 0;
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(AutomationResponse.Ok(new { ready = true }));
        }, _ => { });
        host.Start();
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!AutomationDiscovery.List().Any(instance => instance.InstanceId == host.Instance.InstanceId)) await Task.Delay(5, startup.Token);
        await using var probe = new TestProbeProcess("mcp", "--instance", host.Instance.InstanceId);
        probe.Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
        Assert.True((await probe.ReadAsync()).TryGetProperty("result", out _));
        probe.Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        for (var id = 2; id < 10; id++)
            probe.Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name = "serial_get_status", arguments = new { } } }));
        var watch = Stopwatch.StartNew();
        var ids = new HashSet<int>();
        for (var index = 0; index < 8; index++)
        {
            var reply = await probe.ReadAsync(TimeSpan.FromSeconds(2));
            Assert.True(ids.Add(reply.GetProperty("id").GetInt32()));
            Assert.True(reply.GetProperty("result").GetProperty("structuredContent").GetProperty("success").GetBoolean(), reply.ToString());
        }
        Assert.Equal(8, calls);
        output.WriteLine($"Actual MCP, worker maximum 1, stdin left open: all 8 GUI calls replied in {watch.Elapsed.TotalMilliseconds:F0}ms; no extra stdin message or TX.");
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(8192)]
    public async Task CompleteUnicodeJsonAtNativeReadBoundariesDoesNotNeedAnotherMessage(int wireBytes)
    {
        await using var probe = new TestProbeProcess("mcp");
        probe.Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
        Assert.True((await probe.ReadAsync()).TryGetProperty("result", out _));
        const string prefix = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\",\"params\":{\"text\":\"";
        const string suffix = "\"}}\n";
        var padding = wireBytes - Encoding.UTF8.GetByteCount(prefix + suffix);
        var bytes = Encoding.UTF8.GetBytes(prefix + new string('가', padding / 3) + new string('x', padding % 3) + suffix);
        Assert.Equal(wireBytes, bytes.Length);
        probe.SendBytes(bytes);
        Assert.Equal(3, (await probe.ReadAsync(TimeSpan.FromSeconds(2))).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task FourByteUnicodeSplitAtReadBoundaryKeepsTheWholeJsonLine()
    {
        await using var probe = new TestProbeProcess("mcp");
        probe.Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
        Assert.True((await probe.ReadAsync()).TryGetProperty("result", out _));
        const string prefix = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\",\"params\":{\"text\":\"";
        const string suffix = "\"}}\n";
        var firstPadding = 4096 - Encoding.UTF8.GetByteCount(prefix) - 3;
        var secondPadding = 8192 - Encoding.UTF8.GetByteCount(prefix + suffix) - firstPadding - 4;
        var bytes = Encoding.UTF8.GetBytes(prefix + new string('x', firstPadding) + "😀" + new string('x', secondPadding) + suffix);
        Assert.Equal(8192, bytes.Length);
        Assert.Equal(new byte[] { 0xf0, 0x9f, 0x98, 0x80 }, bytes[4093..4097]);
        probe.SendBytes(bytes);
        Assert.Equal(3, (await probe.ReadAsync(TimeSpan.FromSeconds(2))).GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData("raw", false)]
    [InlineData("production", true)]
    public async Task WaitingForStdinDoesNotTakeTheWorkerNeededForTools(string adapter, bool expectedHeartbeat)
    {
        await using var probe = new TestProbeProcess("stdin", adapter);
        var result = await probe.ReadAsync();
        output.WriteLine(result.ToString());
        Assert.False(result.GetProperty("inputCompleted").GetBoolean());
        Assert.Equal(expectedHeartbeat, result.GetProperty("heartbeatRan").GetBoolean());
    }

    [Theory]
    [InlineData("raw", false)]
    [InlineData("production", true)]
    public async Task AvailableGuiIsReachedWhileStdinStaysOpenAndOnlyOneWorkerIsAvailable(string adapter, bool expectedCompletion)
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(AutomationSchedulingTests), Guid.NewGuid().ToString("N"));
        var calls = 0;
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(AutomationResponse.Ok(new { }));
        }, error => throw new InvalidOperationException("Unexpected host failure", error), root);
        try
        {
            await using var probe = new TestProbeProcess("pipe", adapter, JsonSerializer.Serialize(host.Instance, AutomationJson.Options));
            Assert.Equal("connecting", (await probe.ReadAsync()).GetProperty("stage").GetString());
            await Task.Delay(100); // The first probe finds no listener; require an asynchronous retry.
            host.Start();
            var result = await probe.ReadAsync();
            output.WriteLine(result.ToString());
            Assert.Equal(expectedCompletion, result.GetProperty("completed").GetBoolean());
            Assert.Equal(expectedCompletion ? 1 : 0, Volatile.Read(ref calls));
        }
        finally
        {
            await host.StopAsync(default);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ActualMcpWithOneWorkerRespondsWithoutAnotherStdinMessage()
    {
        await using var probe = new TestProbeProcess("mcp");
        probe.Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
        Assert.True((await probe.ReadAsync()).TryGetProperty("result", out _));
        probe.Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        probe.Send("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"serial_get_capabilities","arguments":{}}}""");
        var result = await probe.ReadAsync(TimeSpan.FromSeconds(2));
        output.WriteLine(result.ToString());
        Assert.Equal(2, result.GetProperty("id").GetInt32());
        Assert.False(result.GetProperty("result").GetProperty("isError").GetBoolean());
    }
}
