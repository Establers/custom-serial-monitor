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
    public static XtermAppendRoute GetRoute(
        long batchEndDisplayedLineCount,
        long syncedThroughDisplayedLineCount,
        bool isVisualAppendSuspended,
        bool isAppendBackpressureActive = false)
    {
        if (batchEndDisplayedLineCount <= syncedThroughDisplayedLineCount)
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
