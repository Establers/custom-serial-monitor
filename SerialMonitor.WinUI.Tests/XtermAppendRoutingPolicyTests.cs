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
    public void PostClearBatch_IsDeferredDuringBackpressure()
    {
        Assert.Equal(
            XtermAppendRoute.Defer,
            XtermAppendRoutingPolicy.GetRoute(
                batchEndDisplayedLineCount: 5_001,
                syncedThroughDisplayedLineCount: 5_000,
                isVisualAppendSuspended: false,
                isAppendBackpressureActive: true));
    }
}
