using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SerialMonitor.Cli;
using SerialMonitor.Core.Automation;
using Xunit.Abstractions;

namespace SerialMonitor.Core.Tests;

public sealed class AutomationConcurrentProcessTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RepeatedMcpInitializationAndCleanShutdownKeepStdoutValid(int repetition)
    {
        await using var gui = await TestGui.StartAsync();
        for (var round = 0; round < 30; round++)
        {
            await using var rpc = await RpcProcess.StartAsync(gui.InstanceId);
            var reply = await rpc.ToolAsync("serial_get_status", new { });
            Assert.True(reply.GetProperty("success").GetBoolean(), reply.ToString());
        }
        Assert.Equal(0, gui.Device.SendCount);
        AssertQuiescent(gui);
        output.WriteLine($"Startup repetition {repetition}: 30 actual MCP processes initialized, status checked, and stdin EOF shut down with exit 0; no TX.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MixedCliProcessesAndPersistentMcpSessionsKeepExclusiveTxUnderRepeatedContention(int repetition)
    {
        await using var gui = await TestGui.StartAsync();
        await using var first = await RpcProcess.StartAsync(gui.InstanceId);
        await using var second = await RpcProcess.StartAsync(gui.InstanceId);
        await using var third = await RpcProcess.StartAsync(gui.InstanceId);
        RpcProcess[] sessions = [first, second, third];
        var replies = new List<JsonElement>();
        var elapsed = Stopwatch.StartNew();
        var timings = new List<double>();
        for (var round = 0; round < 20; round++)
        {
            var started = Stopwatch.GetTimestamp();
            var mcp = sessions.SelectMany(session => Enumerable.Range(0, 6).Select(index => session.ToolAsync(index switch
            {
                4 => "serial_send_and_capture", 5 => "serial_configure", _ => "serial_get_status"
            }, index switch
            {
                4 => new { command = "AA55", txMode = "hex", durationMs = 80, format = "hex" } as object,
                5 => new { baudRate = 57600 }, _ => new { }
            }))).ToArray();
            var cli = Enumerable.Range(0, 8).Select(index => RunCliAsync(gui.InstanceId,
                index < 4 ? ["status"] : ["send", "--hex", "AA55", "--format", "hex"])).ToArray();
            var batch = await Task.WhenAll(mcp.Concat(cli)).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(gui.Errors.IsEmpty, string.Join(Environment.NewLine, gui.Errors.Select(error => error.ToString())));
            for (var index = 0; index < batch.Length; index++)
            {
                var response = batch[index];
                Assert.True(response.GetProperty("success").GetBoolean() || response.GetProperty("error").GetProperty("code").GetString() == "busy",
                    $"round {round}, reply {index} ({(index < 18 ? "MCP" : "CLI")}), {gui.Diagnostics}: {response}");
            }
            replies.AddRange(batch);
            timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        var confirmedTx = replies.Count(response => response.GetProperty("success").GetBoolean() &&
            response.GetProperty("data").TryGetProperty("capture", out var capture) && capture.GetProperty("sendStatus").GetString() == "sent");
        Assert.True(confirmedTx > 0);
        Assert.Equal(confirmedTx, gui.Device.SendCount);
        Assert.True(gui.Device.Tx.All(command => command == "AA55"));
        AssertQuiescent(gui);
        timings.Sort();
        var connectTimeouts = replies.Count(response => !response.GetProperty("success").GetBoolean() && response.GetProperty("error").GetProperty("code").GetString() == "gui_connect_timeout");
        Assert.Equal(0, connectTimeouts);
        output.WriteLine($"Repetition {repetition}: 20 rounds, 3 persistent MCP processes, 160 CLI processes, 520 control calls; confirmed TX {confirmedTx}; connect timeouts {connectTimeouts}; total {elapsed.Elapsed.TotalSeconds:F2}s; round p95 {timings[18]:F0}ms; reservations {gui.Device.Reservations}, releases {gui.Device.Releases}.");
    }

    [Fact]
    public async Task RepeatedCancellationAndClientTerminationReleaseTheSharedGuiWithoutExtraTx()
    {
        await using var gui = await TestGui.StartAsync();
        var elapsed = Stopwatch.StartNew();
        for (var round = 0; round < 15; round++)
        {
            await using var owner = await RpcProcess.StartAsync(gui.InstanceId);
            var expected = gui.Device.SendCount + 1;
            var pending = await owner.BeginToolAsync("serial_send_and_capture", new { command = "AA55", txMode = "hex", durationMs = 60000 });
            await UntilAsync(() => gui.Device.SendCount == expected);
            var contenders = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => RunCliAsync(gui.InstanceId,
                index < 4 ? ["send", "--hex", "BEEF"] : ["status"])));
            foreach (var response in contenders) AssertSuccessOrBusy(response);
            Assert.Equal(expected, gui.Device.SendCount);
            if (round % 3 == 0)
            {
                await owner.NotifyAsync("notifications/cancelled", new { requestId = pending.Id });
                var reply = await pending.Response.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(ToolData(reply).GetProperty("success").GetBoolean());
            }
            else if (round % 3 == 1) owner.CloseInput();
            else owner.Kill();
            await UntilAsync(() => !gui.Device.Reserved);
            var status = await RunCliAsync(gui.InstanceId, "status");
            Assert.True(status.GetProperty("success").GetBoolean(), $"cancel round {round}, {gui.Diagnostics}: {status}");
            Assert.Equal(expected, gui.Device.SendCount);
        }
        Assert.Equal(15, gui.Device.SendCount);
        AssertQuiescent(gui);
        output.WriteLine($"15 cancellation/EOF/kill rounds, 15 MCP processes and 135 CLI processes; exactly 15 TX, all reservations released, elapsed {elapsed.Elapsed.TotalSeconds:F2}s.");
    }

    [Fact]
    public async Task BackgroundSequenceStartPollCancelAndHistoryChurnStayResponsiveAcrossClients()
    {
        await using var gui = await TestGui.StartAsync();
        await using var rpc = await RpcProcess.StartAsync(gui.InstanceId);
        var accepted = new List<string>();
        var confirmedTx = 0;
        for (var round = 0; round < 30; round++)
        {
            var started = await RunCliAsync(gui.InstanceId, "sequence", "run", "--name", "burst");
            Assert.True(started.GetProperty("success").GetBoolean(), started.ToString());
            var id = started.GetProperty("data").GetProperty("runId").GetString()!;
            accepted.Add(id);
            var calls = Enumerable.Range(0, 6).Select(index => rpc.ToolAsync(index % 2 == 0 ? "serial_sequence_status" : "serial_get_status",
                index % 2 == 0 ? new { runId = id } : new { } as object)).ToList();
            if (round % 2 == 0)
                calls.Add(RunCliAsync(gui.InstanceId, "sequence", "cancel", "--run", id));
            foreach (var result in await Task.WhenAll(calls)) Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
            JsonElement final = default;
            await UntilAsync(async () =>
            {
                var status = await rpc.ToolAsync("serial_sequence_status", new { runId = id });
                Assert.True(status.GetProperty("success").GetBoolean(), status.ToString());
                final = status.GetProperty("data");
                return final.GetProperty("state").GetString() != "running";
            });
            Assert.Contains(final.GetProperty("state").GetString(), new[] { "completed", "canceled" });
            var steps = final.GetProperty("steps").EnumerateArray().ToArray();
            Assert.True(steps.All(step => step.GetProperty("sendStatus").GetString() is "sent" or "unknown" or "not_sent"));
            confirmedTx += steps.Count(step => step.GetProperty("sendStatus").GetString() == "sent");
        }
        var expired = await rpc.ToolAsync("serial_sequence_cancel", new { runId = accepted[0] });
        Assert.Equal("run_not_found", expired.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(confirmedTx, gui.Device.SendCount);
        AssertQuiescent(gui);
        output.WriteLine($"30 background sequence runs, 45 CLI invocations, at least 210 MCP tool calls, history eviction and cancellation verified; TX {gui.Device.SendCount}.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SequenceCancelReplyDoesNotWaitForADeviceCancellationCallback(bool callbackThrows)
    {
        await using var gui = await TestGui.StartAsync();
        await using var rpc = await RpcProcess.StartAsync(gui.InstanceId);
        gui.Device.ThrowCancellation = callbackThrows;
        string? id = null;
        Task? cleanup = null;
        try
        {
            var started = await rpc.ToolAsync("serial_sequence_start", new { name = "blocked" });
            id = started.GetProperty("data").GetProperty("runId").GetString()!;
            await gui.Device.BlockingSendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cancel = RunCliAsync(gui.InstanceId, "sequence", "cancel", "--run", id);
            await gui.Device.CancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await cancel.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
            Assert.True(result.GetProperty("data").GetProperty("canceled").GetBoolean());
            Assert.True((await rpc.ToolAsync("serial_get_status", new { })).GetProperty("success").GetBoolean());
            cleanup = gui.StopControllerAsync();
            Assert.Same(cleanup, gui.StopControllerAsync());
            Assert.False(cleanup.IsCompleted); // Callbacks still own resources until explicitly released.
        }
        finally { gui.Device.ReleaseCancellation.Set(); }
        if (cleanup is not null) await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        await UntilAsync(() => !gui.Device.Reserved);
        if (callbackThrows)
            await UntilAsync(async () =>
            {
                var status = await rpc.ToolAsync("serial_sequence_status", new { runId = id });
                return status.GetProperty("data").GetProperty("error").GetString()?.Contains("Injected cancellation callback failure") == true;
            });
        AssertQuiescent(gui);
    }

    private static void AssertSuccessOrBusy(JsonElement response)
    {
        if (!response.GetProperty("success").GetBoolean())
            Assert.True(response.GetProperty("error").GetProperty("code").GetString() == "busy", response.ToString());
    }

    [Fact]
    public async Task CliDescribeDoesNotHangWhenItsOutputConsumerStopsReading()
    {
        var processes = Enumerable.Range(0, 3).Select(_ => StartProcess("describe")).ToArray();
        try
        {
            foreach (var process in processes) process.StandardInput.Close();
            await Task.WhenAll(processes.Select(process => process.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(8));
            foreach (var process in processes)
            {
                Assert.Equal(1, process.ExitCode);
                Assert.Contains("output", await process.StandardError.ReadToEndAsync(), StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            foreach (var process in processes) { if (!process.HasExited) process.Kill(true); process.Dispose(); }
        }
    }

    [Fact]
    public async Task McpCancellationCanInterruptAPartialRequestWriteToAStalledGuiPipe()
    {
        using var current = Process.GetCurrentProcess();
        var id = "instance-" + Guid.NewGuid().ToString("N");
        var instance = new AutomationInstance(id, "serialmonitor-" + id, Environment.ProcessId, new DateTimeOffset(current.StartTime.ToUniversalTime()));
        var descriptor = Path.Combine(AutomationDiscovery.DirectoryPath, id + ".json");
        Directory.CreateDirectory(AutomationDiscovery.DirectoryPath);
        using var pipe = new NamedPipeServerStream(instance.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 512, 512);
        await File.WriteAllTextAsync(descriptor, JsonSerializer.Serialize(instance, AutomationJson.Options));
        try
        {
            await using var rpc = await RpcProcess.StartAsync(id);
            var connected = pipe.WaitForConnectionAsync();
            var pending = await rpc.BeginToolAsync("serial_send", new { command = new string('X', 16384) });
            await connected.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100); // Leave a partial request blocked in the 512-byte pipe buffer.
            await rpc.NotifyAsync("notifications/cancelled", new { requestId = pending.Id });
            try
            {
                var response = ToolData(await pending.Response.WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.False(response.GetProperty("success").GetBoolean());
                Assert.Equal("canceled", response.GetProperty("error").GetProperty("code").GetString());
            }
            finally { pipe.Dispose(); }
        }
        finally { File.Delete(descriptor); }
    }

    [Fact]
    public async Task CompletedRepliesWithLingeringClientHandlesCannotExhaustTheGuiListener()
    {
        await using var gui = await TestGui.StartAsync();
        var clients = new List<NamedPipeClientStream>();
        try
        {
            for (var index = 0; index < 32; index++)
            {
                var pipe = new NamedPipeClientStream(".", "serialmonitor-" + gui.InstanceId, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                clients.Add(pipe);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await pipe.ConnectAsync(deadline.Token);
                await pipe.WriteAsync(Encoding.UTF8.GetBytes("{\"operation\":\"status\",\"arguments\":{}}\n"), deadline.Token);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
                var line = await new BoundedLineReader(reader).ReadAsync(deadline.Token);
                Assert.True(JsonSerializer.Deserialize<JsonElement>(line!).GetProperty("success").GetBoolean());
                Assert.True(gui.Errors.IsEmpty, string.Join(Environment.NewLine, gui.Errors));
                // Keep the client endpoint alive after the server finished its
                // response, as a slow caller/GC can do between repeated calls.
            }
            AssertQuiescent(gui);
        }
        finally { foreach (var pipe in clients) pipe.Dispose(); }
    }

    [Fact]
    public async Task EightIdleConnectionsDoNotBlockCliOrMcpStatus()
    {
        await using var gui = await TestGui.StartAsync();
        await using var rpc = await RpcProcess.StartAsync(gui.InstanceId);
        var clients = new List<NamedPipeClientStream>();
        try
        {
            for (var index = 0; index < 8; index++)
            {
                var pipe = new NamedPipeClientStream(".", "serialmonitor-" + gui.InstanceId, PipeDirection.InOut, PipeOptions.Asynchronous);
                clients.Add(pipe);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await pipe.ConnectAsync(deadline.Token);
            }
            var responses = await Task.WhenAll(RunCliAsync(gui.InstanceId, "status"), rpc.ToolAsync("serial_get_status", new { }));
            foreach (var response in responses)
                Assert.True(response.GetProperty("success").GetBoolean(), response.ToString());
        }
        finally { foreach (var pipe in clients) pipe.Dispose(); }
        Assert.True((await RunCliAsync(gui.InstanceId, "status")).GetProperty("success").GetBoolean());
        Assert.Equal(0, gui.Device.SendCount);
        AssertQuiescent(gui);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliAndMcpCanStopALongSequenceWithAllOrdinaryConnectionsOccupied(bool cancelViaMcp)
    {
        await using var gui = await TestGui.StartAsync();
        await using var rpc = await RpcProcess.StartAsync(gui.InstanceId);
        var latencies = new List<double>();
        var startupLatencies = new List<double>();
        for (var round = 0; round < 10; round++)
        {
            var started = await RunCliAsync(gui.InstanceId, "sequence", "run", "--name", "waiting");
            Assert.True(started.GetProperty("success").GetBoolean(), started.ToString());
            var id = started.GetProperty("data").GetProperty("runId").GetString()!;
            await UntilAsync(() => gui.Device.SendCount == round + 1);
            // The product's 3-second contract bounds GUI connection, not OS/CLR
            // process startup plus output collection. Prepare the real CLI entry
            // point before saturating the pipe, then retain the same 3-second
            // command deadline. Mixed-load tests still cold-start serialctl.exe.
            var startup = Stopwatch.StartNew();
            await using var prepared = cancelViaMcp ? null : new TestProbeProcess("prepared-cli", "0", "sequence", "cancel", "--run", id, "--instance", gui.InstanceId);
            if (prepared is not null)
            {
                Assert.True((await prepared.ReadAsync(TimeSpan.FromSeconds(12))).GetProperty("ready").GetBoolean());
                startupLatencies.Add(startup.Elapsed.TotalMilliseconds);
            }
            var clients = new List<NamedPipeClientStream>();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                for (var i = 0; i < 64; i++)
                {
                    var pipe = new NamedPipeClientStream(".", gui.Instance.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    clients.Add(pipe);
                    await pipe.ConnectAsync(deadline.Token);
                }
                var watch = Stopwatch.StartNew();
                var result = await (cancelViaMcp
                    ? rpc.ToolAsync("serial_sequence_cancel", new { runId = id })
                    : InvokePreparedCliAsync(prepared!)).WaitAsync(TimeSpan.FromSeconds(3));
                latencies.Add(watch.Elapsed.TotalMilliseconds);
                Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
                Assert.True(result.GetProperty("data").GetProperty("canceled").GetBoolean());
                await UntilAsync(() => !gui.Device.Reserved);
                Assert.Equal(round + 1, gui.Device.SendCount);
            }
            finally { foreach (var pipe in clients) pipe.Dispose(); }
            var final = await rpc.ToolAsync("serial_sequence_status", new { runId = id });
            Assert.True(final.GetProperty("success").GetBoolean(), final.ToString());
            Assert.Equal("canceled", final.GetProperty("data").GetProperty("state").GetString());
            Assert.Single(final.GetProperty("data").GetProperty("steps").EnumerateArray());
            AssertQuiescent(gui);
        }
        Assert.Equal(10, gui.Device.SendCount);
        Assert.All(gui.Device.Tx, command => Assert.Equal("AA55", command));
        output.WriteLine($"{(cancelViaMcp ? "MCP" : "CLI")}: 10 long sequences canceled with 64 idle ordinary connections each; exactly 10 TX, no next step/replay; maximum cancel reply {latencies.Max():F0}ms; maximum process preparation {(startupLatencies.Count == 0 ? 0 : startupLatencies.Max()):F0}ms (separate from unchanged command deadline).");
    }

    private static async Task<JsonElement> InvokePreparedCliAsync(TestProbeProcess prepared)
    {
        prepared.InvokePreparedCli();
        return await prepared.ReadAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task CliAndMcpStillUseLegacyGuiDescriptorsWithoutAControlEndpoint()
    {
        using var current = Process.GetCurrentProcess();
        var id = "instance-" + Guid.NewGuid().ToString("N");
        var legacy = new AutomationInstance(id, "serialmonitor-" + id, Environment.ProcessId,
            new DateTimeOffset(current.StartTime.ToUniversalTime()));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var descriptor = Path.Combine(AutomationDiscovery.DirectoryPath, id + ".json");
        // A single legacy endpoint exists; there is physically no control pipe.
        var servers = Enumerable.Range(0, 2).Select(_ => new NamedPipeServerStream(legacy.PipeName, PipeDirection.InOut, 2,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly)).ToArray();
        var requests = 0;
        var handlers = servers.Select(async pipe =>
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true);
            var line = await new BoundedLineReader(reader).ReadAsync(deadline.Token);
            var request = JsonSerializer.Deserialize<AutomationRequest>(line!, AutomationJson.Options)!;
            Assert.Equal("cancel_capture", request.Operation);
            Interlocked.Increment(ref requests);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(AutomationResponse.Ok(new { canceled = false }), AutomationJson.Options) + "\n"), deadline.Token);
            Assert.Null(await new BoundedLineReader(reader).ReadAsync(deadline.Token));
        }).ToArray();
        try
        {
            Directory.CreateDirectory(AutomationDiscovery.DirectoryPath);
            await File.WriteAllTextAsync(descriptor, JsonSerializer.Serialize(legacy, AutomationJson.Options));
            await using var rpc = await RpcProcess.StartAsync(id);
            foreach (var reply in await Task.WhenAll(RunCliAsync(id, "cancel"), rpc.ToolAsync("serial_cancel_capture", new { })))
            {
                Assert.True(reply.GetProperty("success").GetBoolean(), reply.ToString());
                Assert.False(reply.GetProperty("data").GetProperty("canceled").GetBoolean());
            }
            await Task.WhenAll(handlers).WaitAsync(deadline.Token);
            Assert.Equal(2, requests);
        }
        finally
        {
            deadline.Cancel();
            foreach (var pipe in servers) pipe.Dispose();
            await Record.ExceptionAsync(() => Task.WhenAll(handlers));
            File.Delete(descriptor);
        }
    }

    [Fact]
    public async Task TheSameMcpSessionCanCancelWhenItsEightCallsAndTheOrdinaryPipeAreFull()
    {
        await using var gui = await TestGui.StartAsync();
        await using var rpc = await RpcProcess.StartAsync(gui.InstanceId);
        var latencies = new List<double>();
        for (var round = 0; round < 5; round++)
        {
            var started = await RunCliAsync(gui.InstanceId, "sequence", "run", "--name", "waiting");
            Assert.True(started.GetProperty("success").GetBoolean(), started.ToString());
            var id = started.GetProperty("data").GetProperty("runId").GetString()!;
            await UntilAsync(() => gui.Device.SendCount == round + 1);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gui.Device.StatusGate = release.Task;
            var active = Enumerable.Range(0, 8).Select(_ => rpc.ToolAsync("serial_get_status", new { })).ToArray();
            var clients = new List<NamedPipeClientStream>();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                await UntilAsync(() => gui.Device.GatedStatusCalls == (round + 1) * 8);
                for (var i = 0; i < 56; i++)
                {
                    var pipe = new NamedPipeClientStream(".", gui.Instance.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    clients.Add(pipe);
                    await pipe.ConnectAsync(deadline.Token);
                }
                var overflow = await (await rpc.BeginToolAsync("serial_send", new { command = "must not execute" })).Response;
                Assert.Equal(-32600, overflow.GetProperty("error").GetProperty("code").GetInt32());
                var watch = Stopwatch.StartNew();
                var canceled = await rpc.ToolAsync("serial_sequence_cancel", new { runId = id }).WaitAsync(TimeSpan.FromSeconds(2));
                latencies.Add(watch.Elapsed.TotalMilliseconds);
                Assert.True(canceled.GetProperty("success").GetBoolean(), canceled.ToString());
                Assert.True(canceled.GetProperty("data").GetProperty("canceled").GetBoolean());
                await UntilAsync(() => !gui.Device.Reserved);
                Assert.Equal(round + 1, gui.Device.SendCount);
            }
            finally
            {
                release.TrySetResult();
                gui.Device.StatusGate = null;
                foreach (var pipe in clients) pipe.Dispose();
                foreach (var response in await Task.WhenAll(active)) Assert.True(response.GetProperty("success").GetBoolean(), response.ToString());
            }
            var status = await rpc.ToolAsync("serial_sequence_status", new { runId = id });
            Assert.Equal("canceled", status.GetProperty("data").GetProperty("state").GetString());
            AssertQuiescent(gui);
        }
        Assert.Equal(5, gui.Device.SendCount);
        Assert.All(gui.Device.Tx, command => Assert.Equal("AA55", command));
        output.WriteLine($"5 MCP saturation rounds: 8 pending tools + 56 idle ordinary pipes; cancel admitted on the same session, extra ordinary TX rejected; exactly 5 TX; maximum cancel reply {latencies.Max():F0}ms.");
    }

    [Fact]
    public async Task McpCancellationAdmissionIsBoundedToOneAndDoesNotConsumeTheEightOrdinaryCalls()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordinaryCalls = 0;
        var cancellationCalls = 0;
        var errors = new ConcurrentQueue<Exception>();
        await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), async (request, token) =>
        {
            if (request.Operation == "cancel_capture") Interlocked.Increment(ref cancellationCalls);
            else Interlocked.Increment(ref ordinaryCalls);
            await release.Task.WaitAsync(token);
            return AutomationResponse.Ok(new { });
        }, errors.Enqueue);
        host.Start();
        await UntilAsync(() => AutomationDiscovery.List().Any(instance => instance.InstanceId == host.Instance.InstanceId));
        await using var rpc = await RpcProcess.StartAsync(host.Instance.InstanceId);
        var cancel = await rpc.BeginToolAsync("serial_cancel_capture", new { });
        var active = Enumerable.Range(0, 8).Select(_ => rpc.ToolAsync("serial_get_status", new { })).ToArray();
        try
        {
            await UntilAsync(() => Volatile.Read(ref cancellationCalls) == 1 && Volatile.Read(ref ordinaryCalls) == 8);
            var extraCancel = await (await rpc.BeginToolAsync("serial_cancel_capture", new { })).Response;
            Assert.Equal(-32600, extraCancel.GetProperty("error").GetProperty("code").GetInt32());
            var extraOrdinary = await (await rpc.BeginToolAsync("serial_send", new { command = "must not execute" })).Response;
            Assert.Equal(-32600, extraOrdinary.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(1, cancellationCalls);
            Assert.Equal(8, ordinaryCalls);
            var malformed = await (await rpc.BeginToolAsync("serial_send", 123)).Response;
            Assert.Equal(-32602, malformed.GetProperty("error").GetProperty("code").GetInt32());
            var invalidField = ToolData(await (await rpc.BeginToolAsync("serial_send", new { command = 123 })).Response);
            Assert.Equal("invalid_argument", invalidField.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            release.TrySetResult();
            Assert.True(ToolData(await cancel.Response).GetProperty("success").GetBoolean());
            foreach (var reply in await Task.WhenAll(active)) Assert.True(reply.GetProperty("success").GetBoolean(), reply.ToString());
        }
        Assert.True((await rpc.ToolAsync("serial_cancel_capture", new { })).GetProperty("success").GetBoolean());
        Assert.Equal(2, cancellationCalls);
        Assert.Equal(8, ordinaryCalls);
        Assert.Empty(errors);
    }

    private static void AssertQuiescent(TestGui gui)
    {
        Assert.False(gui.Device.Reserved);
        Assert.Equal(gui.Device.Reservations, gui.Device.Releases);
        Assert.Empty(gui.Errors);
    }

    private static async Task UntilAsync(Func<bool> condition) => await UntilAsync(() => Task.FromResult(condition()));
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!await condition()) await Task.Delay(5, timeout.Token);
    }

    private static JsonElement ToolData(JsonElement reply)
    {
        Assert.False(reply.TryGetProperty("error", out _), reply.ToString());
        return reply.GetProperty("result").GetProperty("structuredContent");
    }

    private static Process StartProcess(params string[] arguments)
    {
        var assembly = typeof(CliCommandInput).Assembly.Location;
        var executable = Path.ChangeExtension(assembly, ".exe");
        var useAppHost = OperatingSystem.IsWindows() && File.Exists(executable);
        var info = new ProcessStartInfo(useAppHost ? executable : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (!useAppHost) info.ArgumentList.Add(assembly);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info)!;
    }

    private static async Task<JsonElement> RunCliAsync(string instance, params string[] args)
    {
        using var process = StartProcess([.. args, "--instance", instance]);
        process.StandardInput.Close();
        var stdout = ReadProcessOutputAsync(process.StandardOutput);
        var stderr = ReadProcessOutputAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
            Assert.Empty(await stderr);
            var result = JsonSerializer.Deserialize<JsonElement>(await stdout);
            Assert.Equal(result.GetProperty("success").GetBoolean() ? 0 : 1, process.ExitCode);
            return result;
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }

    // Windows redirected process streams use synchronous anonymous pipes. Async
    // reads can pin pool workers until process exit, starving the GUI fixture
    // sharing this test process. Give each bounded child stream its own reader.
    private static Task<string> ReadProcessOutputAsync(StreamReader reader) => ProcessStreamReader.ReadToEndAsync(reader);

    private sealed class RpcProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stderr;
        private readonly Task _pump;
        private readonly SemaphoreSlim _input = new(1, 1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private int _id;
        private bool _closed, _killed;
        private RpcProcess(string instance)
        {
            _process = StartProcess("mcp", "--instance", instance);
            _stderr = ReadProcessOutputAsync(_process.StandardError);
            _pump = Task.Factory.StartNew(Pump, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public static async Task<RpcProcess> StartAsync(string instance)
        {
            var rpc = new RpcProcess(instance);
            try
            {
                var initial = await rpc.BeginAsync("initialize", new { protocolVersion = "2025-06-18" });
                Assert.Equal("2025-06-18", (await initial.Response).GetProperty("result").GetProperty("protocolVersion").GetString());
                await rpc.NotifyAsync("notifications/initialized", new { });
                return rpc;
            }
            catch (Exception startupError)
            {
                try { await rpc.DisposeAsync(); }
                catch (Exception cleanupError) { throw new AggregateException("MCP initialization and cleanup failed.", startupError, cleanupError); }
                throw;
            }
        }

        public async Task<JsonElement> ToolAsync(string name, object arguments) => ToolData(await (await BeginToolAsync(name, arguments)).Response);
        public Task<(int Id, Task<JsonElement> Response)> BeginToolAsync(string name, object arguments) => BeginAsync("tools/call", new { name, arguments });
        private async Task<(int Id, Task<JsonElement> Response)> BeginAsync(string method, object args)
        {
            var id = Interlocked.Increment(ref _id);
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(_pending.TryAdd(id, completion));
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params = args });
            var response = completion.Task.WaitAsync(TimeSpan.FromSeconds(12));
            _ = response.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return (id, response);
        }

        public Task NotifyAsync(string method, object args) => WriteAsync(new { jsonrpc = "2.0", method, @params = args });
        private async Task WriteAsync(object message)
        {
            await _input.WaitAsync();
            try
            {
                await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message, AutomationJson.Options)).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { _input.Release(); }
        }

        private void Pump()
        {
            Exception? error = null;
            try
            {
                while (_process.StandardOutput.ReadLine() is { } line)
                {
                    var reply = JsonSerializer.Deserialize<JsonElement>(line);
                    Assert.True(reply.TryGetProperty("id", out var id), reply.ToString());
                    Assert.True(_pending.TryRemove(id.GetInt32(), out var completion), "Unexpected/duplicate response: " + reply);
                    completion!.TrySetResult(reply);
                }
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                foreach (var item in _pending.ToArray())
                    if (_pending.TryRemove(item.Key, out var completion)) completion.TrySetException(error ?? new IOException("MCP process closed."));
            }
            if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        public void CloseInput() { if (_closed) return; _closed = true; _process.StandardInput.Close(); }
        public void Kill() { _killed = true; if (!_process.HasExited) _process.Kill(true); }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_killed) CloseInput();
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                await _pump.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Empty(await _stderr);
                if (!_killed) Assert.True(_process.ExitCode == 0, $"MCP exited {_process.ExitCode}.");
            }
            finally { if (!_process.HasExited) _process.Kill(true); _process.Dispose(); _input.Dispose(); }
        }
    }

    private sealed class TestGui : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(AutomationConcurrentProcessTests), Guid.NewGuid().ToString("N"));
        private readonly RxCaptureService _captures;
        private readonly AutomationController _controller;
        private readonly AutomationPipeHost _host;
        public readonly ConcurrentQueue<Exception> Errors = new();
        public TestDevice Device { get; }
        public AutomationInstance Instance => _host.Instance;
        public string InstanceId => Instance.InstanceId;
        public string Diagnostics => InstanceId + ", " + string.Join(", ", new[] { "_connections", "_requests", "_loop" }.Select(name =>
        {
            var value = typeof(AutomationPipeHost).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(_host);
            return name + "=" + (value is SemaphoreSlim semaphore ? semaphore.CurrentCount : value is Task task ? task.Status : value);
        }));
        public Task StopControllerAsync() => _controller.DisposeAsync().AsTask();
        private TestGui()
        {
            _captures = new(_root);
            Device = new(_captures);
            _controller = new(Device, _captures);
            _host = new("instance-" + Guid.NewGuid().ToString("N"), _controller.HandleAsync, Errors.Enqueue);
        }
        public static async Task<TestGui> StartAsync()
        {
            var gui = new TestGui();
            gui._host.Start();
            try { await UntilAsync(() => AutomationDiscovery.List().Any(item => item.InstanceId == gui.InstanceId)); return gui; }
            catch { await gui.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            Device.ReleaseCancellation.Set();
            await _host.DisposeAsync();
            await _controller.DisposeAsync();
            await _captures.DisposeAsync();
            Device.ReleaseCancellation.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    // Serialize UI-owned operations like the WinUI dispatcher and use the actual
    // production ownership gate; the test adapter never opens a serial port.
    private sealed class TestDevice(RxCaptureService captures) : IAutomationDevice
    {
        private readonly SemaphoreSlim _dispatcher = new(1, 1);
        private readonly AutomationControlGate _control = new();
        private int _baudRate = 115200, _sends, _reservations, _releases, _gatedStatusCalls;
        public Task? StatusGate { get; set; }
        public int GatedStatusCalls => Volatile.Read(ref _gatedStatusCalls);
        public int SendCount => Volatile.Read(ref _sends);
        public int Reservations => Volatile.Read(ref _reservations);
        public int Releases => Volatile.Read(ref _releases);
        public bool Reserved => _control.IsAutomationReserved;
        public ConcurrentQueue<string> Tx { get; } = new();
        public CaptureConnection Connection => new(true, 1, 0);
        public TaskCompletionSource BlockingSendEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseCancellation { get; } = new(false);
        public bool ThrowCancellation { get; set; }
        public async Task<object> InvokeAsync(string operation, JsonElement args, CancellationToken token)
        {
            if (operation == "status" && StatusGate is { } gate)
            {
                Interlocked.Increment(ref _gatedStatusCalls);
                await gate.WaitAsync(token);
            }
            if (operation == "send" && args.String("command") == "BLOCK_CANCEL")
            {
                using var registration = token.Register(() =>
                {
                    CancellationEntered.TrySetResult();
                    ReleaseCancellation.Wait(TimeSpan.FromSeconds(20));
                    if (ThrowCancellation) throw new InvalidOperationException("Injected cancellation callback failure");
                });
                BlockingSendEntered.TrySetResult();
                await Task.Delay(60000, token);
            }
            await _dispatcher.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                switch (operation)
                {
                    case "reserve":
                        if (!_control.TryReserveAutomation()) throw new AutomationException("busy", "Reserved by another client.");
                        Interlocked.Increment(ref _reservations); break;
                    case "release":
                        Assert.True(Reserved);
                        _control.ReleaseAutomation(); Interlocked.Increment(ref _releases); break;
                    case "configure": _baudRate = args.Integer("baudRate", _baudRate); break;
                    case "sequence_get":
                        return new AutomationSequenceDefinition(args.String("name")!, 1,
                            args.String("name") == "blocked" ? [new(null, "BLOCK_CANCEL", CaptureMs: 0, DelayAfterMs: 0)] :
                            args.String("name") == "waiting" ? [new(null, "AA55", "hex", "none", 0, 60000), new(null, "BEEF", "hex", "none", 0, 0)] :
                            [new(null, "AA55", "hex", "none", 40, 10), new(null, "AA55", "hex", "none", 40, 10), new(null, "AA55", "hex", "none", 40, 0)]);
                    case "send":
                        Assert.True(Reserved);
                        var command = args.String("command")!;
                        Tx.Enqueue(command); Interlocked.Increment(ref _sends);
                        captures.Offer(1, [0xFF, 0xAA, 0x55, 0, 0xFE, 0x11], Stopwatch.GetTimestamp());
                        break;
                }
                return new AutomationDeviceState(true, "TEST-COM", _baudRate, 1, "Utf8", 0, Reserved, null);
            }
            finally { _dispatcher.Release(); }
        }
    }
}
