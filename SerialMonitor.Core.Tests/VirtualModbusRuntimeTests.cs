using System.Diagnostics;
using System.Collections.Concurrent;
using SerialMonitor.Core.Protocols;
using SerialMonitor.Core.Protocols.VirtualModbus;
using static SerialMonitor.Core.Tests.VirtualModbusDeviceTests;

namespace SerialMonitor.Core.Tests;

public sealed class VirtualModbusRuntimeTests
{
    private static VirtualModbusDeviceStore Store()
    { var store = new VirtualModbusDeviceStore(); store.Add(Map(Point())); return store; }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    [Fact]
    public async Task SplitRequest_IsAssembled_AndWrongSessionsIgnored()
    {
        var sent = new ConcurrentQueue<byte[]>();
        await using var runtime = new VirtualModbusDeviceRuntime(Store(), (bytes, _, _) => { sent.Enqueue(bytes); return Task.CompletedTask; });
        runtime.Start(7, 100);
        var frame = Adu("010300000001"); var time = Stopwatch.GetTimestamp();
        Assert.False(runtime.TryEnqueue(frame, 6));
        runtime.TryEnqueue(frame.AsMemory(0, 3), 7, time);
        runtime.TryEnqueue(frame.AsMemory(3), 7, time + 1, true);
        await Until(() => sent.Count == 1);
        Assert.Equal(Adu("01030200FA"), sent.Single());
    }

    [Fact]
    public async Task CrcValidPrefixWithTrailingGarbage_NeverWritesOrReplies()
    {
        var store = Store(); var count = 0;
        await using var runtime = new VirtualModbusDeviceRuntime(store, (_, _, _) => { Interlocked.Increment(ref count); return Task.CompletedTask; });
        runtime.Start(1, 1.75);
        runtime.TryEnqueue(Adu("010600000001").Concat(new byte[] { 255 }).ToArray(), 1, qualifiedBoundary: true);
        await Until(() => runtime.Snapshot().Invalid == 1);
        Assert.Equal(0, count); Assert.Equal("250", store.Page(1, 0)[0].Value);
    }

    [Fact]
    public async Task KnownIdleGap_DoesNotJoinFragments()
    {
        var store = Store();
        await using var runtime = new VirtualModbusDeviceRuntime(store, (_, _, _) => throw new Exception("Must not send"));
        runtime.Start(1, 1.75);
        var frame = Adu("010600000001"); var time = Stopwatch.GetTimestamp();
        runtime.TryEnqueue(frame.AsMemory(0, 3), 1, time);
        runtime.TryEnqueue(frame.AsMemory(3), 1, time + (long)(Stopwatch.Frequency * .004), true);
        await Until(() => runtime.Snapshot().Invalid == 2);
        Assert.Equal("250", store.Page(1, 0)[0].Value);
    }

    [Fact]
    public async Task OrdinaryAdapter_RepeatedIdenticalWrites_AllReceiveReplies()
    {
        var count = 0;
        await using var runtime = new VirtualModbusDeviceRuntime(Store(), (_, _, _) => { Interlocked.Increment(ref count); return Task.CompletedTask; });
        runtime.Start(1, 1.75);
        for (var i = 0; i < 3; i++)
        {
            runtime.TryEnqueue(Adu("010600000001"), 1, qualifiedBoundary: true);
            await Until(() => Volatile.Read(ref count) == i + 1);
        }
        Assert.Equal(0, runtime.Snapshot().Echoes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    public async Task ExplicitEchoMode_ConsumesOneOwnEcho_RegardlessOfDriverDelay(int delay)
    {
        VirtualModbusDeviceRuntime? runtime = null;
        var count = 0;
        runtime = new VirtualModbusDeviceRuntime(Store(), async (bytes, generation, token) =>
        {
            Interlocked.Increment(ref count);
            await Task.Delay(delay, token);
            runtime!.TryEnqueue(bytes, generation, qualifiedBoundary: true);
        });
        await using (runtime)
        {
            runtime.Start(1, 1.75, suppressLocalEcho: true);
            runtime.TryEnqueue(Adu("010600000001"), 1, qualifiedBoundary: true);
            await Until(() => runtime.Snapshot().Echoes == 1);
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public async Task StopCancelsInFlight_AndDoesNotReuseQueuedRequestsOnRestart()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var runtime = new VirtualModbusDeviceRuntime(Store(), async (_, _, token) =>
        { Interlocked.Increment(ref count); entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); });
        runtime.Start(1, 1.75);
        runtime.TryEnqueue(Adu("010300000001"), 1, qualifiedBoundary: true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.TryEnqueue(Adu("010300000001"), 1, qualifiedBoundary: true);
        await runtime.StopAsync();
        runtime.Start(2, 1.75);
        Assert.False(runtime.TryEnqueue(Adu("010300000001"), 1, qualifiedBoundary: true));
        Assert.Equal(0, runtime.Snapshot().PendingBytes); Assert.Equal(1, count);
    }

    [Fact]
    public async Task OverflowAndSendFailure_StopWithVisibleError()
    {
        await using var overflow = new VirtualModbusDeviceRuntime(Store(), (_, _, _) => Task.CompletedTask);
        overflow.Start(1, 1.75);
        Assert.False(overflow.TryEnqueue(new byte[VirtualModbusDeviceRuntime.QueueByteLimit + 1], 1));
        await overflow.StopAsync(); Assert.Contains("overflow", overflow.Snapshot().Error);
        Assert.Equal(0, overflow.Snapshot().PendingBytes);
        await using var failed = new VirtualModbusDeviceRuntime(Store(), (_, _, _) => throw new IOException("fixture failure"));
        failed.Start(1, 1.75); failed.TryEnqueue(Adu("010300000001"), 1, qualifiedBoundary: true);
        await Until(() => failed.Snapshot().Error is not null);
        Assert.Contains("fixture failure", failed.Snapshot().Error); Assert.False(failed.Snapshot().Running);
    }

    [Theory]
    [InlineData("01030400FA0064DBE9")]
    [InlineData("0103020019798E")]
    [InlineData("01100000000241C8")]
    [InlineData("010600000001480A")]
    public async Task ExplicitResponseRole_AnalyzesServerTxAsResponse(string hex)
    {
        await using var analyzer = new ModbusRtuAnalyzer(); analyzer.SetEnabled(true);
        analyzer.TryEnqueue(Convert.FromHexString(hex), ProtocolDirection.Tx, 1, DateTimeOffset.Now,
            new ProtocolInputTiming(Role: ProtocolFrameRole.Response));
        await Until(() => analyzer.Snapshot().TotalFrames > 0);
        var frame = Assert.Single(analyzer.Snapshot().Frames!);
        Assert.Equal(ModbusFrameKind.Response, frame.Kind); Assert.Equal(ModbusCrcStatus.Valid, frame.CrcStatus);
    }
}
