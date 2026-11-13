namespace SerialMonitor.WinUI.Infrastructure;

internal enum XtermAppendRoute
{
    Append,
    Suspend,
    Defer,
    AlreadyCovered
}

internal static class XtermAppendRoutingPolicy
{
    // An idempotent settings trim can arrive after a newer snapshot. It must
    // never make an already rendered text batch eligible for replay.
    public static long AdvanceSyncedThrough(long current, long batchEnd) => Math.Max(current, batchEnd);

    public static XtermAppendRoute GetRoute(
        long batchEndDisplayedLineCount,
        long syncedThroughDisplayedLineCount,
        bool isVisualAppendSuspended,
        bool isAppendBackpressureActive = false,
        bool isPrefixTrimOnly = false)
    {
        if (batchEndDisplayedLineCount <= syncedThroughDisplayedLineCount && !isPrefixTrimOnly)
        {
            return XtermAppendRoute.AlreadyCovered;
        }

        if (isVisualAppendSuspended)
        {
            return XtermAppendRoute.Suspend;
        }

        return isAppendBackpressureActive
            ? XtermAppendRoute.Defer
            : XtermAppendRoute.Append;
    }
}
