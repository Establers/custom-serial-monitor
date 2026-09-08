using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogRestoreProgressTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public void Advance(double seconds) => Ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    [Fact]
    public void ReportsAcknowledgedProgressAndExcludesPreparationFromRateEstimate()
    {
        var clock = new Clock();
        var progress = new LogRestoreProgress(clock);
        clock.Advance(3);
        Assert.Null(progress.Snapshot().Percent);
        progress.SetTotal(1_000_000);
        clock.Advance(2);
        progress.Advance(250_000);
        var snapshot = progress.Snapshot();
        Assert.Equal(25, snapshot.Percent);
        Assert.Equal(TimeSpan.FromSeconds(5), snapshot.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(6), snapshot.Remaining);
        clock.Advance(2);
        Assert.Equal(25, progress.Snapshot().Percent); // Waiting is not progress.
        Assert.Equal(TimeSpan.FromSeconds(7), progress.Snapshot().Elapsed);
        progress.Advance(long.MaxValue);
        Assert.Equal(100, progress.Snapshot().Percent);
        Assert.Equal(TimeSpan.Zero, progress.Snapshot().Remaining);
    }

    [Fact]
    public void DoesNotInventAnEstimateUntilThereIsEnoughProgress()
    {
        var clock = new Clock();
        var progress = new LogRestoreProgress(clock);
        progress.SetTotal(100_000);
        progress.Advance(-10);
        Assert.Equal(0, progress.Snapshot().Percent);
        clock.Advance(3);
        progress.Advance(100);
        Assert.Null(progress.Snapshot().Remaining);
        progress.SetTotal(0);
        Assert.Equal(100, progress.Snapshot().Percent);
        Assert.Equal(TimeSpan.Zero, progress.Snapshot().Remaining);
    }
}
