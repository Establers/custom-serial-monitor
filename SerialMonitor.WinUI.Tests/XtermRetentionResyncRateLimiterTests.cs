using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class XtermRetentionResyncRateLimiterTests
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    [Fact]
    public void ThousandsOfSteadyStateTrims_KeepOneDelayedRequestAndOneRunPerCooldown()
    {
        var clock = new ManualMonotonicClock();
        var limiter = CreateLimiter(clock);

        Assert.Equal(
            XtermRetentionResyncDisposition.RunNow,
            limiter.Request().Disposition);
        limiter.RecordSnapshotStarted();
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(
            XtermRetentionResyncDisposition.None,
            limiter.RecordSnapshotCompleted().Disposition);

        var scheduled = 0;
        var coalesced = 0;
        for (var index = 0; index < 10_000; index++)
        {
            var decision = limiter.Request();
            scheduled += decision.Disposition == XtermRetentionResyncDisposition.Schedule ? 1 : 0;
            coalesced += decision.Disposition == XtermRetentionResyncDisposition.Coalesced ? 1 : 0;
        }

        Assert.Equal(1, scheduled);
        Assert.Equal(9_999, coalesced);
        Assert.True(limiter.HasPendingRequest);

        clock.Advance(TimeSpan.FromMilliseconds(29_999));
        var early = limiter.ConsumeDelayedRequest();
        Assert.Equal(XtermRetentionResyncDisposition.Schedule, early.Disposition);
        Assert.InRange(early.Delay, TimeSpan.FromTicks(1), TimeSpan.FromMilliseconds(1));

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(
            XtermRetentionResyncDisposition.RunNow,
            limiter.ConsumeDelayedRequest().Disposition);
        limiter.RecordSnapshotStarted();
        Assert.False(limiter.HasPendingRequest);
    }

    [Fact]
    public void SnapshotLongerThanCooldown_StillWaitsFullCooldownAfterCompletion()
    {
        var clock = new ManualMonotonicClock();
        var limiter = CreateLimiter(clock);

        Assert.Equal(
            XtermRetentionResyncDisposition.RunNow,
            limiter.Request().Disposition);
        limiter.RecordSnapshotStarted();

        for (var index = 0; index < 10_000; index++)
        {
            Assert.Equal(
                XtermRetentionResyncDisposition.Coalesced,
                limiter.Request().Disposition);
        }

        clock.Advance(TimeSpan.FromSeconds(45));
        var afterLongRender = limiter.RecordSnapshotCompleted();
        Assert.Equal(XtermRetentionResyncDisposition.Schedule, afterLongRender.Disposition);
        Assert.Equal(Cooldown, afterLongRender.Delay);

        Assert.Equal(
            XtermRetentionResyncDisposition.Schedule,
            limiter.ConsumeDelayedRequest().Disposition);
        clock.Advance(TimeSpan.FromMilliseconds(29_999));
        Assert.Equal(
            XtermRetentionResyncDisposition.Schedule,
            limiter.ConsumeDelayedRequest().Disposition);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(
            XtermRetentionResyncDisposition.RunNow,
            limiter.ConsumeDelayedRequest().Disposition);
    }

    [Fact]
    public void NonRetentionRerender_RemainsImmediateAndReplacesDelayedRetentionRequest()
    {
        var clock = new ManualMonotonicClock();
        var limiter = CreateLimiter(clock);
        var fullRenders = new XtermFullRerenderRequestQueue();
        limiter.RecordSnapshotStarted();
        limiter.RecordSnapshotCompleted();

        Assert.Equal(
            XtermRetentionResyncDisposition.Schedule,
            limiter.Request().Disposition);
        Assert.True(limiter.HasPendingRequest);

        Assert.True(limiter.CancelPendingRequest());
        Assert.Equal(
            XtermRerenderRequestDisposition.Schedule,
            fullRenders.Request("settings changed", isRestoreRender: false));
        Assert.True(fullRenders.TryStart(out var request));
        Assert.Equal("settings changed", request.Reason);
        Assert.False(limiter.HasPendingRequest);
    }

    [Fact]
    public void PauseMinimizeClearOrCloseCancellation_LeavesNoDelayedWork()
    {
        var clock = new ManualMonotonicClock();
        var limiter = CreateLimiter(clock);
        limiter.RecordSnapshotStarted();
        limiter.RecordSnapshotCompleted();
        Assert.Equal(
            XtermRetentionResyncDisposition.Schedule,
            limiter.Request().Disposition);

        Assert.True(limiter.CancelPendingRequest());
        clock.Advance(Cooldown);
        Assert.Equal(
            XtermRetentionResyncDisposition.None,
            limiter.ConsumeDelayedRequest().Disposition);
        Assert.False(limiter.HasPendingRequest);
        Assert.False(limiter.CancelPendingRequest());
    }

    [Fact]
    public void CancellationDuringSnapshot_DropsPendingTrailingReconciliation()
    {
        var clock = new ManualMonotonicClock();
        var limiter = CreateLimiter(clock);
        Assert.Equal(
            XtermRetentionResyncDisposition.RunNow,
            limiter.Request().Disposition);
        limiter.RecordSnapshotStarted();
        Assert.Equal(
            XtermRetentionResyncDisposition.Coalesced,
            limiter.Request().Disposition);
        Assert.True(limiter.HasPendingRequest);

        Assert.True(limiter.CancelPendingRequest());
        clock.Advance(TimeSpan.FromSeconds(45));
        Assert.Equal(
            XtermRetentionResyncDisposition.None,
            limiter.RecordSnapshotCompleted().Disposition);
        Assert.False(limiter.HasPendingRequest);
    }

    private static XtermRetentionResyncRateLimiter CreateLimiter(ManualMonotonicClock clock) =>
        new(Cooldown, clock.GetTimestamp, clock.Frequency);

    private sealed class ManualMonotonicClock
    {
        private long _timestamp;

        public long Frequency => 1_000;

        public long GetTimestamp() => Volatile.Read(ref _timestamp);

        public void Advance(TimeSpan duration)
        {
            Interlocked.Add(
                ref _timestamp,
                checked((long)Math.Round(duration.TotalSeconds * Frequency)));
        }
    }
}
