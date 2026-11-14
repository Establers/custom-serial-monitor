namespace SerialMonitor.WinUI.Services;

internal static class FileLogShutdownStatus
{
    public static string? DescribeFailure(IFileLogWriter writer)
    {
        var state = writer.State;
        var accepted = writer.AcceptedLineCount;
        var flushed = writer.DurableLineCount;
        var unconfirmed = Math.Max(0, accepted - flushed);
        var abandoned = writer.AbandonedLineCount;
        var rejected = writer.DroppedLineCount;
        var pending = writer.PendingRequestCount;
        var detail = writer.LastFault?.Message ?? writer.LastFileError;

        // StopAsync may return normally after the worker records a storage
        // fault. Repeated stops can restore Stopped, and a restart clears the
        // latest fault, but lifetime loss counters still describe an incomplete log.
        if (state == FileLogWriterState.Stopped && unconfirmed == 0 &&
            abandoned == 0 && rejected == 0 && pending == 0 &&
            string.IsNullOrWhiteSpace(detail))
            return null;

        var summary = $"Shutdown file logging incomplete: state: {state}; accepted: {accepted:N0}; " +
            $"flushed: {flushed:N0}; unconfirmed: {unconfirmed:N0}; abandoned: {abandoned:N0}; " +
            $"rejected: {rejected:N0}; pending: {pending:N0}; uncertain/replayed: {writer.UncertainLineCount:N0}.";
        return string.IsNullOrWhiteSpace(detail) ? summary : $"{summary} {detail}";
    }
}
