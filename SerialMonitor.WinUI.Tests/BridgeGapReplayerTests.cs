using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class BridgeGapReplayerTests
{
    [Fact]
    public async Task SourceGaps_AreReplayedWithoutWritingBeforeTheirTargets()
    {
        var clock = new FakeBridgeClock();
        var replayer = new BridgeGapReplayer(clock);
        var sourceTimes = new long[] { 0, 2, 7, 17, 37, 137 };

        foreach (var sourceTime in sourceTimes)
        {
            var chunk = new BridgeRxChunk(new byte[] { 0x55 }, sourceTime, false, 0);
            var timing = await replayer.WaitUntilDueAsync(chunk, CancellationToken.None);
            Assert.True(clock.GetTimestamp() >= timing.TargetTimestamp);
            replayer.RecordWriteCompleted(chunk, clock.GetTimestamp());
        }

        Assert.Equal(new[] { 2d, 5d, 10d, 20d, 100d }, clock.DelaysMilliseconds);
    }

    [Fact]
    public async Task LateWriter_DoesNotWaitTheSourceGapAgain_AndReportsLateness()
    {
        var clock = new FakeBridgeClock();
        var replayer = new BridgeGapReplayer(clock);
        var first = new BridgeRxChunk(new byte[] { 1 }, 0, false, 0);
        await replayer.WaitUntilDueAsync(first, CancellationToken.None);
        replayer.RecordWriteCompleted(first, clock.GetTimestamp());

        clock.Advance(20);
        var late = await replayer.WaitUntilDueAsync(
            new BridgeRxChunk(new byte[] { 2 }, 5, false, 0),
            CancellationToken.None);

        Assert.Empty(clock.DelaysMilliseconds);
        Assert.Equal(15, late.LatenessMilliseconds, 3);
    }

    [Fact]
    public async Task NativeIdleBoundary_PreservesAtLeastTheAppliedIdleGap()
    {
        var clock = new FakeBridgeClock();
        var replayer = new BridgeGapReplayer(clock);
        var boundary = new BridgeRxChunk(new byte[] { 1 }, 0, true, 25);
        await replayer.WaitUntilDueAsync(boundary, CancellationToken.None);
        replayer.RecordWriteCompleted(boundary, clock.GetTimestamp());

        var next = new BridgeRxChunk(new byte[] { 2 }, 5, false, 0);
        var timing = await replayer.WaitUntilDueAsync(next, CancellationToken.None);

        Assert.Equal(25, clock.GetTimestamp());
        Assert.Equal(25, timing.TargetTimestamp);
        Assert.Equal(new[] { 25d }, clock.DelaysMilliseconds);
    }

    private sealed class FakeBridgeClock : IBridgeClock
    {
        private long _timestamp;

        public long Frequency => 1_000;

        public List<double> DelaysMilliseconds { get; } = new();

        public long GetTimestamp() => _timestamp;

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var milliseconds = delay.TotalMilliseconds;
            DelaysMilliseconds.Add(milliseconds);
            _timestamp += (long)Math.Ceiling(milliseconds);
            return ValueTask.CompletedTask;
        }

        public void Advance(long milliseconds) => _timestamp += milliseconds;
    }
}
