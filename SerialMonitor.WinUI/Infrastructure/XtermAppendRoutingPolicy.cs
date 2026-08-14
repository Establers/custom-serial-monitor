namespace SerialMonitor.WinUI.Infrastructure;

internal enum XtermAppendRoute
{
    Append,
    Suspend,
    AlreadyCovered,
    SnapshotResync,
    AppendAndScheduleSnapshotResync
}

internal static class XtermAppendRoutingPolicy
{
    public static XtermAppendRoute GetRoute(
        long batchEndDisplayedLineCount,
        long syncedThroughDisplayedLineCount,
        bool isVisualAppendSuspended,
        int trimCharacterCount = 0,
        bool hasAppendedText = true)
    {
        if (batchEndDisplayedLineCount <= syncedThroughDisplayedLineCount)
        {
            return XtermAppendRoute.AlreadyCovered;
        }

        if (trimCharacterCount > 0)
        {
            return isVisualAppendSuspended || !hasAppendedText
                ? XtermAppendRoute.SnapshotResync
                : XtermAppendRoute.AppendAndScheduleSnapshotResync;
        }

        return isVisualAppendSuspended
            ? XtermAppendRoute.Suspend
            : XtermAppendRoute.Append;
    }
}
