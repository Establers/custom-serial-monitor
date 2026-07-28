namespace SerialMonitor.WinUI.Infrastructure;

internal enum XtermAppendRoute
{
    Append,
    Suspend,
    AlreadyCovered
}

internal static class XtermAppendRoutingPolicy
{
    public static XtermAppendRoute GetRoute(
        long batchEndDisplayedLineCount,
        long syncedThroughDisplayedLineCount,
        bool isVisualAppendSuspended)
    {
        if (batchEndDisplayedLineCount <= syncedThroughDisplayedLineCount)
        {
            return XtermAppendRoute.AlreadyCovered;
        }

        return isVisualAppendSuspended
            ? XtermAppendRoute.Suspend
            : XtermAppendRoute.Append;
    }
}
