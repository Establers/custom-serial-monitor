using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class XtermAppendRoutingPolicyTests
{
    [Fact]
    public void LiveQueueClearThenReminimize_SuspendsOnlyPostClearBatch()
    {
        var barrier = new XtermClearBarrier();
        const long clearGeneration = 8;
        const long clearBoundary = 5_000;
        long[] liveQueue = [clearBoundary, clearBoundary + 1];
        var suspendedQueue = new List<long>();

        barrier.Begin(clearGeneration);
        Assert.False(barrier.ShouldStartPump(
            pumpRunning: false,
            recoveryPending: false,
            queuedBatchCount: liveQueue.Length));

        Assert.True(barrier.TryComplete(clearGeneration));
        foreach (var batchEndDisplayedLineCount in liveQueue)
        {
            var route = XtermAppendRoutingPolicy.GetRoute(
                batchEndDisplayedLineCount,
                syncedThroughDisplayedLineCount: clearBoundary,
                isVisualAppendSuspended: true);
            if (route == XtermAppendRoute.Suspend)
            {
                suspendedQueue.Add(batchEndDisplayedLineCount);
            }
        }

        Assert.Equal([clearBoundary + 1], suspendedQueue);
    }

    [Fact]
    public void PostClearBatch_IsSuspendedWhenWindowIsMinimized()
    {
        Assert.Equal(
            XtermAppendRoute.Suspend,
            XtermAppendRoutingPolicy.GetRoute(
                batchEndDisplayedLineCount: 5_001,
                syncedThroughDisplayedLineCount: 5_000,
                isVisualAppendSuspended: true));
    }

    [Fact]
    public void PostClearBatch_IsAppendedWhenWindowIsVisible()
    {
        Assert.Equal(
            XtermAppendRoute.Append,
            XtermAppendRoutingPolicy.GetRoute(
                batchEndDisplayedLineCount: 5_001,
                syncedThroughDisplayedLineCount: 5_000,
                isVisualAppendSuspended: false));
    }

    [Fact]
    public void RetentionTrim_AppendsLiveDeltaButSuspendedTrimUsesSnapshotOnly()
    {
        Assert.Equal(
            XtermAppendRoute.AppendAndScheduleSnapshotResync,
            XtermAppendRoutingPolicy.GetRoute(
                batchEndDisplayedLineCount: 5_001,
                syncedThroughDisplayedLineCount: 5_000,
                isVisualAppendSuspended: false,
                trimCharacterCount: 128));

        Assert.Equal(
            XtermAppendRoute.SnapshotResync,
            XtermAppendRoutingPolicy.GetRoute(
                batchEndDisplayedLineCount: 5_001,
                syncedThroughDisplayedLineCount: 5_000,
                isVisualAppendSuspended: true,
                trimCharacterCount: 128));
    }

    [Fact]
    public void TrimOnlyMutation_RequestsReconciliationWithoutDeltaAppend()
    {
        Assert.Equal(
            XtermAppendRoute.SnapshotResync,
            XtermAppendRoutingPolicy.GetRoute(
                batchEndDisplayedLineCount: 5_001,
                syncedThroughDisplayedLineCount: 5_000,
                isVisualAppendSuspended: false,
                trimCharacterCount: 128,
                hasAppendedText: false));

        Assert.Equal(
            XtermAppendRoute.SnapshotResync,
            XtermAppendRoutingPolicy.GetRoute(
                batchEndDisplayedLineCount: 5_001,
                syncedThroughDisplayedLineCount: 5_000,
                isVisualAppendSuspended: true,
                trimCharacterCount: 128,
                hasAppendedText: false));

        Assert.Equal(
            XtermAppendRoute.AlreadyCovered,
            XtermAppendRoutingPolicy.GetRoute(
                batchEndDisplayedLineCount: 5_001,
                syncedThroughDisplayedLineCount: 5_001,
                isVisualAppendSuspended: false,
                trimCharacterCount: 128,
                hasAppendedText: false));
    }

    [Fact]
    public void ThousandsOfTrimOnlyMutations_CoalesceIntoOneCooldownRequest()
    {
        var clock = new ManualClock();
        var limiter = new XtermRetentionResyncRateLimiter(
            TimeSpan.FromSeconds(30),
            clock.GetTimestamp,
            clock.Frequency);
        limiter.RecordSnapshotStarted();
        limiter.RecordSnapshotCompleted();

        var scheduled = 0;
        for (var sequence = 1L; sequence <= 10_000; sequence++)
        {
            Assert.Equal(
                XtermAppendRoute.SnapshotResync,
                XtermAppendRoutingPolicy.GetRoute(
                    sequence,
                    syncedThroughDisplayedLineCount: 0,
                    isVisualAppendSuspended: false,
                    trimCharacterCount: 1,
                    hasAppendedText: false));
            var decision = limiter.Request();
            if (decision.Disposition == XtermRetentionResyncDisposition.Schedule)
            {
                scheduled++;
            }
        }

        Assert.Equal(1, scheduled);
        Assert.True(limiter.HasPendingRequest);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(
            XtermRetentionResyncDisposition.RunNow,
            limiter.ConsumeDelayedRequest().Disposition);
        Assert.True(limiter.HasPendingRequest);
        limiter.RecordSnapshotStarted();
        Assert.False(limiter.HasPendingRequest);
    }

    [Fact]
    public void SteadyStateTrim_DeltaIsConsumedOnceWhileOneSnapshotIsDelayed()
    {
        const int count = 10_000;
        var appendedSequences = new HashSet<long>();
        var syncedThrough = 0L;
        var clock = new ManualClock();
        var limiter = new XtermRetentionResyncRateLimiter(
            TimeSpan.FromSeconds(30),
            clock.GetTimestamp,
            clock.Frequency);
        limiter.RecordSnapshotStarted();
        limiter.RecordSnapshotCompleted();

        var scheduled = 0;
        for (var sequence = 1L; sequence <= count; sequence++)
        {
            var route = XtermAppendRoutingPolicy.GetRoute(
                sequence,
                syncedThrough,
                isVisualAppendSuspended: false,
                trimCharacterCount: 1);
            Assert.Equal(XtermAppendRoute.AppendAndScheduleSnapshotResync, route);
            Assert.True(appendedSequences.Add(sequence));
            syncedThrough = sequence;

            var decision = limiter.Request();
            scheduled += decision.Disposition == XtermRetentionResyncDisposition.Schedule ? 1 : 0;
        }

        Assert.Equal(count, appendedSequences.Count);
        Assert.Equal(1, scheduled);

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(
            XtermRetentionResyncDisposition.RunNow,
            limiter.ConsumeDelayedRequest().Disposition);
        limiter.RecordSnapshotStarted();
        var snapshotBoundary = syncedThrough;
        limiter.RecordSnapshotCompleted();

        Assert.Equal(
            XtermAppendRoute.AlreadyCovered,
            XtermAppendRoutingPolicy.GetRoute(
                snapshotBoundary,
                syncedThroughDisplayedLineCount: snapshotBoundary,
                isVisualAppendSuspended: false,
                trimCharacterCount: 1));
        Assert.Equal(count, appendedSequences.Count);
    }

    [Fact]
    public void SnapshotBoundary_CoversStaleQueuedDeltaWithoutReappendOrAccountingLeak()
    {
        var pendingBatchCount = 2;
        var appendedDeltaCount = 0;

        var trimRoute = XtermAppendRoutingPolicy.GetRoute(
            batchEndDisplayedLineCount: 101,
            syncedThroughDisplayedLineCount: 100,
            isVisualAppendSuspended: false,
            trimCharacterCount: 64);
        Assert.Equal(XtermAppendRoute.AppendAndScheduleSnapshotResync, trimRoute);

        // The full snapshot wins the append gate and includes both queued batches.
        // Re-evaluation at the gate must account them as covered, not append them.
        const long snapshotBoundary = 102;
        var coveredTrimRoute = XtermAppendRoutingPolicy.GetRoute(
            batchEndDisplayedLineCount: 101,
            syncedThroughDisplayedLineCount: snapshotBoundary,
            isVisualAppendSuspended: false,
            trimCharacterCount: 64);
        if (coveredTrimRoute is XtermAppendRoute.Append or
            XtermAppendRoute.AppendAndScheduleSnapshotResync)
        {
            appendedDeltaCount++;
        }
        pendingBatchCount--;

        var staleDeltaRoute = XtermAppendRoutingPolicy.GetRoute(
            batchEndDisplayedLineCount: 102,
            syncedThroughDisplayedLineCount: snapshotBoundary,
            isVisualAppendSuspended: false);
        if (staleDeltaRoute is XtermAppendRoute.Append or
            XtermAppendRoute.AppendAndScheduleSnapshotResync)
        {
            appendedDeltaCount++;
        }
        pendingBatchCount--;

        Assert.Equal(XtermAppendRoute.AlreadyCovered, coveredTrimRoute);
        Assert.Equal(XtermAppendRoute.AlreadyCovered, staleDeltaRoute);
        Assert.Equal(0, appendedDeltaCount);
        Assert.Equal(0, pendingBatchCount);
    }

    [Fact]
    public void FullRerenderQueue_CoalescesToOneQueuedAndOneTrailingSnapshot()
    {
        var requests = new XtermFullRerenderRequestQueue();

        Assert.Equal(
            XtermRerenderRequestDisposition.Schedule,
            requests.Request("settings change", isRestoreRender: false));
        Assert.Equal(
            XtermRerenderRequestDisposition.Coalesced,
            requests.Request("filter change", isRestoreRender: false));
        Assert.Equal(
            XtermRerenderRequestDisposition.Coalesced,
            requests.Request("restore", isRestoreRender: true));

        Assert.True(requests.TryStart(out var first));
        Assert.True(first.IsRestoreRender);
        Assert.Contains("settings change", first.Reason);

        Assert.Equal(
            XtermRerenderRequestDisposition.Coalesced,
            requests.Request("recovery while running", isRestoreRender: false));
        Assert.True(requests.Complete());
        Assert.True(requests.TryStart(out var trailing));
        Assert.Equal("recovery while running", trailing.Reason);
        Assert.False(requests.Complete());
        Assert.False(requests.HasQueuedRequestReady);
    }

    private sealed class ManualClock
    {
        private long _timestamp;

        public long Frequency => 1_000;

        public long GetTimestamp() => Volatile.Read(ref _timestamp);

        public void Advance(TimeSpan duration) =>
            Interlocked.Add(ref _timestamp, (long)(duration.TotalSeconds * Frequency));
    }
}
