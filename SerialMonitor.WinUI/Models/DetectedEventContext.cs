namespace SerialMonitor.WinUI.Models;

public sealed class DetectedEventContext
{
    public DetectedEventContext(
        DetectedEvent detectedEvent,
        IReadOnlyList<LogLine> beforeContextLines,
        IReadOnlyList<LogLine> afterContextLines,
        int beforeContextLineLimit,
        int afterContextLineLimit,
        DateTimeOffset completedAt)
    {
        Event = detectedEvent;
        EventId = detectedEvent.Id;
        BeforeContextLines = beforeContextLines;
        AfterContextLines = afterContextLines;
        BeforeContextLineLimit = Math.Max(0, beforeContextLineLimit);
        AfterContextLineLimit = Math.Max(0, afterContextLineLimit);
        CompletedAt = completedAt;
    }

    public Guid EventId { get; }

    public DetectedEvent Event { get; }

    public IReadOnlyList<LogLine> BeforeContextLines { get; }

    public IReadOnlyList<LogLine> AfterContextLines { get; }

    public int BeforeContextLineLimit { get; }

    public int AfterContextLineLimit { get; }

    public DateTimeOffset CompletedAt { get; }
}
