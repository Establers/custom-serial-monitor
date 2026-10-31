using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed class AutomationPipeAdmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(AutomationPipeAdmissionTests), Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("sequence_cancel")]
    [InlineData("cancel_capture")]
    public async Task CancellationIsAdmittedWhileAllRegularConnectionsWaitForHeaders(string operation)
    {
        var canceled = 0;
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), (request, _) =>
        {
            Assert.Equal(operation, request.Operation);
            Interlocked.Increment(ref canceled);
            return Task.FromResult(AutomationResponse.Ok(new { canceled = true }));
        }, _ => { }, _root);
        host.Start();
        var clients = new List<NamedPipeClientStream>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            for (var i = 0; i < 64; i++)
            {
                var pipe = Client(host);
                clients.Add(pipe);
                await pipe.ConnectAsync(deadline.Token);
            }
            using var cancellationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var response = await AutomationPipeClient.CallAsync(host.Instance, operation, new { }, cancellationDeadline.Token);
            Assert.True(response.Success, response.Error?.Message);
            Assert.Equal(1, Volatile.Read(ref canceled));
        }
        finally { foreach (var pipe in clients) pipe.Dispose(); }
    }

    [Theory]
    [InlineData("send")]
    [InlineData("sequence_start")]
    [InlineData("status")]
    public async Task ControlEndpointRejectsOrdinaryOperationsWithoutCallingTheHandler(string operation)
    {
        var calls = 0;
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(AutomationResponse.Ok(new { }));
        }, _ => { }, _root);
        host.Start();
        // Deliberately bypass the normal client's operation routing.
        var control = host.Instance with { PipeName = host.Instance.ControlPipeName! , ControlPipeName = null };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await AutomationPipeClient.CallAsync(control, operation, new { }, deadline.Token);
        Assert.False(result.Success);
        Assert.Equal("invalid_argument", result.Error!.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task LegacyDescriptorsUseTheOrdinaryPipeAndDiscoveryValidatesControlCapabilities()
    {
        var calls = 0;
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(AutomationResponse.Ok(new { }));
        }, _ => { }, _root);
        host.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (AutomationDiscovery.List(_root).Count == 0) await Task.Delay(5, deadline.Token);
        Assert.Equal(host.Instance.ControlPipeName, Assert.Single(AutomationDiscovery.List(_root)).ControlPipeName);
        var path = Path.Combine(_root, host.Instance.InstanceId + ".json");
        // A new CLI can still cancel an old GUI that advertises only one pipe.
        var legacy = host.Instance with { ControlPipeName = null };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(legacy, AutomationJson.Options), deadline.Token);
        var discovered = Assert.Single(AutomationDiscovery.List(_root));
        Assert.Null(discovered.ControlPipeName);
        Assert.True((await AutomationPipeClient.CallAsync(discovered, "cancel_capture", new { }, deadline.Token)).Success);
        Assert.Equal(1, calls);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(host.Instance with { ControlPipeName = "unrelated-pipe" }, AutomationJson.Options), deadline.Token);
        Assert.Empty(AutomationDiscovery.List(_root));
    }

    [Fact]
    public async Task BothEndpointsShareOneCancellationExecutionBudgetWithoutQueuing()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return AutomationResponse.Ok(new { });
        }, _ => { }, _root);
        host.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = AutomationPipeClient.CallAsync(host.Instance, "sequence_cancel", new { }, deadline.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            var legacy = host.Instance with { ControlPipeName = null };
            var second = await AutomationPipeClient.CallAsync(legacy, "cancel_capture", new { }, deadline.Token);
            Assert.False(second.Success);
            Assert.Equal("busy", second.Error!.Code);
            Assert.Equal(1, calls);
        }
        finally { release.TrySetResult(); await first.WaitAsync(deadline.Token); }
        Assert.True((await AutomationPipeClient.CallAsync(host.Instance, "cancel_capture", new { }, deadline.Token)).Success);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FourStalledControlPeersExpireBeforeTheConnectionDeadline(bool slowReply)
    {
        var calls = 0;
        var errors = new ConcurrentQueue<Exception>();
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), (_, _) =>
            Task.FromResult(AutomationResponse.Ok(new { value = Interlocked.Increment(ref calls) <= 4 && slowReply ? new string('X', 256 * 1024) : "ready" })),
            errors.Enqueue, _root);
        host.Start();
        var clients = new List<NamedPipeClientStream>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var pipe = new NamedPipeClientStream(".", host.Instance.ControlPipeName!, PipeDirection.InOut, PipeOptions.Asynchronous);
                clients.Add(pipe);
                await pipe.ConnectAsync(deadline.Token);
                if (slowReply)
                {
                    await pipe.WriteAsync(Encoding.UTF8.GetBytes("{\"operation\":\"cancel_capture\",\"arguments\":{}}\n"), deadline.Token);
                    // One response byte proves execution admission was released
                    // before adding the next peer. Leave the large remainder
                    // unread so the response still stalls on its output budget.
                    Assert.Equal(1, await pipe.ReadAsync(new byte[1], deadline.Token));
                    Assert.Equal(i + 1, Volatile.Read(ref calls));
                }
            }
            // Ordinary calls remain independent of the bounded control endpoint.
            Assert.True((await AutomationPipeClient.CallAsync(host.Instance, "status", new { }, deadline.Token)).Success);
            var result = await AutomationPipeClient.CallAsync(host.Instance, "sequence_cancel", new { }, deadline.Token);
            Assert.True(result.Success, result.Error?.Message);
            Assert.Equal("ready", result.Data.GetProperty("value").GetString());
            Assert.Equal(slowReply ? 6 : 2, calls);
            Assert.Empty(errors);
        }
        finally { foreach (var pipe in clients) pipe.Dispose(); }
    }

    [Fact]
    public async Task ShutdownClosesBothFullConnectionBudgetsAndRemovesDiscovery()
    {
        var errors = new ConcurrentQueue<Exception>();
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), (_, _) =>
            throw new InvalidOperationException("No requests should execute"), errors.Enqueue, _root);
        host.Start();
        var clients = new List<NamedPipeClientStream>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            for (var i = 0; i < 68; i++)
            {
                var pipe = new NamedPipeClientStream(".", i < 64 ? host.Instance.PipeName : host.Instance.ControlPipeName!,
                    PipeDirection.InOut, PipeOptions.Asynchronous);
                clients.Add(pipe);
                await pipe.ConnectAsync(deadline.Token);
            }
            await host.StopAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(AutomationDiscovery.List(_root));
            Assert.Empty(errors);
        }
        finally { foreach (var pipe in clients) pipe.Dispose(); }
    }

    [Fact]
    public async Task CancellationHasReservedCapacityWhenAllRegularHandlersAreOccupied()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), async (request, token) =>
        {
            if (request.Operation == "cancel_capture") return AutomationResponse.Ok(new { canceled = true });
            if (Interlocked.Increment(ref calls) == 8) entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return AutomationResponse.Ok(new { });
        }, _ => { }, _root);
        host.Start();
        var active = Enumerable.Range(0, 8).Select(_ => AutomationPipeClient.CallAsync(host.Instance, "status", new { }, default)).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var canceled = await AutomationPipeClient.CallAsync(host.Instance, "cancel_capture", new { }, deadline.Token);
            Assert.True(canceled.Success, canceled.Error?.Message);
            Assert.True(canceled.Data.GetProperty("canceled").GetBoolean());
            Assert.Equal(8, calls);
        }
        finally { release.TrySetResult(); await Task.WhenAll(active).WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task EightSlowHandlersStillAllowAnImmediateBusyReplyWithoutExecutingTheNinthRequest()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var errors = new ConcurrentQueue<Exception>();
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 8) entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return AutomationResponse.Ok(new { completed = true });
        }, errors.Enqueue, _root);
        host.Start();
        var active = Enumerable.Range(0, 8).Select(_ => AutomationPipeClient.CallAsync(host.Instance, "status", new { }, default)).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var result = await AutomationPipeClient.CallAsync(host.Instance, "send", new { command = "must not execute" }, deadline.Token);
            Assert.False(result.Success);
            Assert.Equal("busy", result.Error!.Code);
            Assert.Equal(8, Volatile.Read(ref calls));
        }
        finally { release.TrySetResult(); await Task.WhenAll(active).WaitAsync(TimeSpan.FromSeconds(5)); }
        var ready = await AutomationPipeClient.CallAsync(host.Instance, "status", new { }, default);
        Assert.True(ready.Success);
        Assert.Equal(9, calls);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task IdleConnectionsDoNotBlockStatusAndExpireWithoutExecutingARequest()
    {
        var calls = 0;
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(AutomationResponse.Ok(new { }));
        }, _ => { }, _root);
        host.Start();
        var clients = new List<NamedPipeClientStream>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var pipe = Client(host);
                clients.Add(pipe);
                await pipe.ConnectAsync(deadline.Token);
            }
            Assert.True((await AutomationPipeClient.CallAsync(host.Instance, "status", new { }, deadline.Token)).Success);
            Assert.Equal(1, calls);
            Assert.Equal(0, await clients[0].ReadAsync(new byte[1], deadline.Token));
            Assert.Equal(1, calls);
        }
        finally { foreach (var pipe in clients) pipe.Dispose(); }
    }

    [Fact]
    public async Task SlowResponseReadersDoNotOccupyHandlerCapacity()
    {
        var calls = 0;
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), (_, _) =>
            Task.FromResult(AutomationResponse.Ok(new { value = Interlocked.Increment(ref calls) <= 8 ? new string('X', 256 * 1024) : "ready" })),
            _ => { }, _root);
        host.Start();
        var clients = new List<NamedPipeClientStream>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var pipe = Client(host);
                clients.Add(pipe);
                await pipe.ConnectAsync(deadline.Token);
                await pipe.WriteAsync(Encoding.UTF8.GetBytes("{\"operation\":\"status\",\"arguments\":{}}\n"), deadline.Token);
            }
            while (Volatile.Read(ref calls) < 8) await Task.Delay(5, deadline.Token);
            var response = await AutomationPipeClient.CallAsync(host.Instance, "status", new { }, deadline.Token);
            Assert.True(response.Success, response.Error?.Message);
            Assert.Equal("ready", response.Data.GetProperty("value").GetString());
            Assert.Equal(9, calls);
        }
        finally { foreach (var pipe in clients) pipe.Dispose(); }
    }

    [Fact]
    public async Task ConnectPollingWaitsForALateListenerAndSendsExactlyOneRequest()
    {
        var calls = 0;
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(AutomationResponse.Ok(new { }));
        }, _ => { }, _root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = AutomationPipeClient.CallAsync(host.Instance, "send", new { command = "only once" }, deadline.Token);
        await Task.Delay(150, deadline.Token);
        host.Start();
        Assert.True((await pending).Success);
        Assert.Equal(1, calls);
    }

    private static NamedPipeClientStream Client(AutomationPipeHost host) => new(".", host.Instance.PipeName,
        PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
