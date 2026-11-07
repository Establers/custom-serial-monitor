using SerialMonitor.Core.Protocols;

namespace SerialMonitor.Core.Tests;

public sealed class ModbusRtuAnalyzerTests
{
    [Fact]
    public async Task DefaultDisabled_AndTenThousandPacketRetention_AreBounded()
    {
        await using var analyzer = new ModbusRtuAnalyzer();
        Assert.False(analyzer.TryEnqueue(ModbusRtuTests.Request, ProtocolDirection.Rx, 1, DateTimeOffset.Now));
        analyzer.SetEnabled(true);
        Assert.True(analyzer.TryEnqueue(Enumerable.Range(0, 10005).SelectMany(_ => ModbusRtuTests.Request).ToArray(),
            ProtocolDirection.Rx, 1, DateTimeOffset.Now));
        var snapshot = await Wait(analyzer, state => state.TotalFrames == 10005);
        Assert.Equal(10000, snapshot.RetainedCount);
        Assert.Equal(5, snapshot.EvictedFrames);
        Assert.Equal(6, snapshot.Frames![0].Id);
        Assert.InRange(snapshot.RetainedBytes, 1, ModbusRtuAnalyzer.DefaultHistoryByteLimit);
        Assert.Null(analyzer.Snapshot(snapshot.Version).Frames);
    }

    [Fact]
    public async Task ByteBudget_EvictsResultsIndependentlyOfCount()
    {
        await using var analyzer = new ModbusRtuAnalyzer(historyByteLimit: 600);
        analyzer.SetEnabled(true);
        analyzer.TryEnqueue(Enumerable.Range(0, 10).SelectMany(_ => ModbusRtuTests.Request).ToArray(), ProtocolDirection.Tx, 1, DateTimeOffset.Now);
        var snapshot = await Wait(analyzer, state => state.TotalFrames == 10);
        Assert.Equal(2, snapshot.RetainedCount);
        Assert.InRange(snapshot.RetainedBytes, 1, 600);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionAndEnableBoundaries_DoNotJoinPartialFrames(bool changeSession)
    {
        await using var analyzer = new ModbusRtuAnalyzer();
        analyzer.SetEnabled(true);
        analyzer.TryEnqueue(ModbusRtuTests.Request.AsMemory(0, 3), ProtocolDirection.Rx, 1, DateTimeOffset.Now);
        await Wait(analyzer, state => state.PendingBytes == 0);
        if (!changeSession) { analyzer.SetEnabled(false); analyzer.SetEnabled(true); }
        analyzer.TryEnqueue(ModbusRtuTests.Request.AsMemory(3), ProtocolDirection.Rx, changeSession ? 2 : 1, DateTimeOffset.Now);
        analyzer.TryEnqueue(ModbusRtuTests.Request, ProtocolDirection.Rx, changeSession ? 2 : 1, DateTimeOffset.Now);
        var snapshot = await Wait(analyzer, state => state.TotalFrames >= 1 && state.PendingBytes == 0);
        snapshot = await Wait(analyzer, state => state.Frames!.Any(frame => frame.CrcStatus == ModbusCrcStatus.Valid));
        Assert.Single(snapshot.Frames!.Where(frame => frame.CrcStatus == ModbusCrcStatus.Valid));
    }

    [Fact]
    public async Task OverloadIsVisible_AndMissingChunkCannotBecomeMatchEvidence()
    {
        await using var analyzer = new ModbusRtuAnalyzer(queueByteLimit: 8);
        analyzer.SetEnabled(true);
        analyzer.TryEnqueue(ModbusRtuTests.Request.AsMemory(0, 3), ProtocolDirection.Rx, 1, DateTimeOffset.Now);
        await Wait(analyzer, state => state.PendingBytes == 0);
        Assert.False(analyzer.TryEnqueue(new byte[9], ProtocolDirection.Rx, 1, DateTimeOffset.Now));
        Assert.True(analyzer.TryEnqueue(ModbusRtuTests.Request.AsMemory(3), ProtocolDirection.Rx, 1, DateTimeOffset.Now));
        await Wait(analyzer, state => state.PendingBytes == 0);
        Assert.True(analyzer.TryEnqueue(ModbusRtuTests.Request, ProtocolDirection.Rx, 1, DateTimeOffset.Now));
        var snapshot = await Wait(analyzer, state => state.Frames!.Any(frame => frame.CrcStatus == ModbusCrcStatus.Valid));
        Assert.Equal(1, snapshot.DroppedChunks);
        Assert.Equal(9, snapshot.DroppedBytes);
        Assert.Single(snapshot.Frames!.Where(frame => frame.CrcStatus == ModbusCrcStatus.Valid));
        Assert.InRange(snapshot.PendingBytes, 0, 8);
    }

    [Fact]
    public async Task ClearAndDisable_PreserveExplicitHistorySemantics()
    {
        await using var analyzer = new ModbusRtuAnalyzer();
        analyzer.SetEnabled(true);
        analyzer.TryEnqueue(ModbusRtuTests.Request, ProtocolDirection.Rx, 1, DateTimeOffset.Now);
        await Wait(analyzer, state => state.TotalFrames == 1);
        analyzer.SetEnabled(false);
        Assert.Single(analyzer.Snapshot().Frames!);
        analyzer.Clear();
        Assert.Empty(analyzer.Snapshot().Frames!);
        Assert.Equal(1, analyzer.Snapshot().TotalFrames);
        analyzer.ResetStatistics();
        Assert.Equal(0, analyzer.Snapshot().TotalFrames);
        analyzer.SetEnabled(true);
        analyzer.TryEnqueue(ModbusRtuTests.Request, ProtocolDirection.Rx, 2, DateTimeOffset.Now);
        Assert.Equal(2, (await Wait(analyzer, state => state.TotalFrames == 1)).Frames![0].Session);
        await analyzer.DisposeAsync();
        Assert.False(analyzer.TryEnqueue(ModbusRtuTests.Request, ProtocolDirection.Rx, 2, DateTimeOffset.Now));
    }

    internal static async Task<ProtocolAnalysisSnapshot> Wait(ModbusRtuAnalyzer analyzer, Func<ProtocolAnalysisSnapshot, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var snapshot = analyzer.Snapshot();
            if (predicate(snapshot)) return snapshot;
            await Task.Delay(5, timeout.Token);
        }
    }

    [Theory]
    [InlineData(true, 5, false)]
    [InlineData(true, 1, true)]
    [InlineData(false, 5, true)]
    public async Task NativeIdleBoundary_IsUsedOnlyWhenQualified(bool nativeBoundary, int timeoutMs, bool mayJoin)
    {
        await using var analyzer = new ModbusRtuAnalyzer();
        analyzer.SetEnabled(true);
        analyzer.TryEnqueue(ModbusRtuTests.Request.AsMemory(0, 3), ProtocolDirection.Rx, 1, DateTimeOffset.Now,
            new ProtocolInputTiming(System.Diagnostics.Stopwatch.GetTimestamp(), nativeBoundary, timeoutMs,
                ProtocolInputTiming.FrameGapMilliseconds(9600, 11)));
        await Wait(analyzer, state => state.PendingBytes == 0);
        await Task.Delay(20);
        analyzer.TryEnqueue(ModbusRtuTests.Request.AsMemory(3), ProtocolDirection.Rx, 1, DateTimeOffset.Now);
        var snapshot = await Wait(analyzer, state => state.TotalFrames >= (mayJoin ? 1 : 2) && state.PendingBytes == 0);
        Assert.Equal(mayJoin, snapshot.Frames!.Any(frame => frame.CrcStatus == ModbusCrcStatus.Valid));
    }

    [Fact]
    public async Task CountersSurviveEvictionAndClear_AndResetDoesNotClearHistory()
    {
        await using var analyzer = new ModbusRtuAnalyzer(historyCapacity: 2);
        analyzer.SetEnabled(true);
        var bad = ModbusRtuTests.Request.ToArray(); bad[^1] ^= 1;
        analyzer.TryEnqueue(bad, ProtocolDirection.Rx, 1, DateTimeOffset.Now,
            new ProtocolInputTiming(0, true, 5, 4));
        await Wait(analyzer, state => state.TotalFrames == 1);
        analyzer.TryEnqueue(ModbusRtuTests.Frame(1, 0x83, 2), ProtocolDirection.Rx, 1, DateTimeOffset.Now);
        analyzer.TryEnqueue(ModbusRtuTests.Request, ProtocolDirection.Tx, 1, DateTimeOffset.Now);
        var snapshot = await Wait(analyzer, state => state.TotalFrames == 3);
        Assert.Equal(2, snapshot.Statistics.Rx);
        Assert.Equal(1, snapshot.Statistics.Tx);
        Assert.Equal(1, snapshot.Statistics.CrcErrors);
        Assert.Equal(0, snapshot.Statistics.Incomplete);
        Assert.Equal(1, snapshot.Statistics.Exceptions);
        Assert.Equal(2, snapshot.Statistics.CrcValid);
        Assert.NotNull(snapshot.Statistics.LastCrcValidAt);
        Assert.Equal(1, snapshot.EvictedFrames);
        analyzer.ResetStatistics();
        Assert.Equal(0, analyzer.Snapshot().Statistics.Rx);
        Assert.Equal(2, analyzer.Snapshot().RetainedCount);
        analyzer.Clear();
        Assert.Equal(0, analyzer.Snapshot().RetainedCount);
    }

    [Fact]
    public async Task OneDirectionTraffic_DoesNotKeepOtherDirectionPartialOpen()
    {
        await using var analyzer = new ModbusRtuAnalyzer();
        analyzer.SetEnabled(true);
        analyzer.TryEnqueue(ModbusRtuTests.Request.AsMemory(0, 3), ProtocolDirection.Rx, 1, DateTimeOffset.Now);
        for (var i = 0; i < 15; i++)
        {
            analyzer.TryEnqueue(ModbusRtuTests.Request, ProtocolDirection.Tx, 1, DateTimeOffset.Now);
            await Task.Delay(10);
        }
        var snapshot = await Wait(analyzer, state => state.Statistics.Incomplete == 1);
        Assert.Equal(1, snapshot.Statistics.Rx);
    }

    [Fact]
    public async Task AmbiguousPrefix_AndDelayedObservation_DoNotCorruptCountersOrLatestTime()
    {
        await using var analyzer = new ModbusRtuAnalyzer();
        analyzer.SetEnabled(true);
        var latest = DateTimeOffset.Now;
        analyzer.TryEnqueue(ModbusRtuTests.Request, ProtocolDirection.Tx, 1, latest);
        await Wait(analyzer, state => state.TotalFrames == 1);
        analyzer.TryEnqueue(Convert.FromHexString("010304000001853A00"), ProtocolDirection.Rx, 1, latest.AddSeconds(-1),
            new ProtocolInputTiming(System.Diagnostics.Stopwatch.GetTimestamp(), true, 40, 1.75));
        var snapshot = await Wait(analyzer, state => state.TotalFrames == 2);
        Assert.Equal(1, snapshot.Statistics.Rx);
        Assert.Equal(0, snapshot.Statistics.Incomplete);
        Assert.Equal(latest, snapshot.Statistics.LastCrcValidAt);
        Assert.Equal(ModbusFrameKind.Response, snapshot.Frames![1].Kind);
    }

    [Fact]
    public async Task CompleteCorruptResponse_IsOneCrcError_NotAnInventedRequest()
    {
        await using var analyzer = new ModbusRtuAnalyzer();
        analyzer.SetEnabled(true);
        analyzer.TryEnqueue(Convert.FromHexString("01030A010300000002C40B1122D8F9"), ProtocolDirection.Rx, 1, DateTimeOffset.Now,
            new ProtocolInputTiming(System.Diagnostics.Stopwatch.GetTimestamp(), true, 40, 1.75));
        var snapshot = await Wait(analyzer, state => state.TotalFrames >= 1);
        Assert.Equal(1, snapshot.Statistics.Rx);
        Assert.Equal(1, snapshot.Statistics.CrcErrors);
        Assert.Equal(0, snapshot.Statistics.CrcValid);
        Assert.Equal(0, snapshot.Statistics.Incomplete);
    }

    [Fact]
    public async Task BufferedInputChunks_RetainIndependentTimesInFramesAndStatistics()
    {
        await using var analyzer = new ModbusRtuAnalyzer();
        analyzer.SetEnabled(true);
        var time = DateTimeOffset.Now;
        var ticks = System.Diagnostics.Stopwatch.GetTimestamp();
        analyzer.TryEnqueue(ModbusRtuTests.Request, ProtocolDirection.Rx, 1, time, new ProtocolInputTiming(ticks));
        analyzer.TryEnqueue(ModbusRtuTests.Request, ProtocolDirection.Rx, 1, time.AddMilliseconds(20),
            new ProtocolInputTiming(ticks + System.Diagnostics.Stopwatch.Frequency / 50));
        var snapshot = await Wait(analyzer, state => state.TotalFrames == 2);
        Assert.Equal(time, snapshot.Frames![0].Timestamp);
        Assert.Equal(time.AddMilliseconds(20), snapshot.Frames[1].Timestamp);
        Assert.Equal(time.AddMilliseconds(20), snapshot.Statistics.LastCrcValidAt);
    }
}
