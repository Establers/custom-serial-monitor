using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

internal static class EventViewMemoryPolicy
{
    internal const long VisibleBytes = 16L * 1024 * 1024;
    internal const long PendingBytes = 16L * 1024 * 1024;
    internal const long DetectorInputBytes = 32L * 1024 * 1024;
    internal const long DetectorOutputBytes = 64L * 1024 * 1024;
    internal const long ContextWorkingBytes = 64L * 1024 * 1024;

    internal static long Estimate(DetectedEvent item)
    {
        if (item.ArchivedSource is not null) return 1024L + ContextBytes(item.BeforeContextLines) + ContextBytes(item.AfterContextLines);
        var bytes = 512L + 2L * item.Message.Length;
        if (item.Evidence is not null) bytes += EventEvidenceReference.ReservedMemoryBytes;
        if (item.SourceLogLine is { } source) bytes += LogViewMemoryPolicy.EstimateRetainedBytes(source);
        bytes += ContextBytes(item.BeforeContextLines) + ContextBytes(item.AfterContextLines);
        foreach (var segment in item.MessageSegments)
            bytes += 64L + (ReferenceEquals(segment.Text, item.Message) ? 0 : 2L * segment.Text.Length);
        return bytes;
    }

    internal static long Estimate(DetectedEventContext item) => Estimate(item.Event) + 128L +
        ContextBytes(item.BeforeContextLines) + ContextBytes(item.AfterContextLines);

    private static long ContextBytes(IReadOnlyList<LogLine> lines) => lines is Services.ArchivedLogLines
        ? 64L + 64L * lines.Count : lines.Sum(LogViewMemoryPolicy.EstimateRetainedBytes);
}
