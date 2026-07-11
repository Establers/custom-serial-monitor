using System.Globalization;

namespace SerialMonitor.WinUI.Models;

public sealed class DetectedEvent
{
    public DetectedEvent(
        DateTimeOffset timestamp,
        string ruleName,
        string keyword,
        LogDirection direction,
        string message,
        LogLine? sourceLogLine = null,
        IReadOnlyList<LogLine>? beforeContextLines = null,
        IReadOnlyList<LogLine>? afterContextLines = null,
        Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Timestamp = timestamp;
        RuleName = ruleName;
        Keyword = keyword;
        Direction = direction;
        Message = message;
        SourceLogLine = sourceLogLine;
        BeforeContextLines = beforeContextLines ?? Array.Empty<LogLine>();
        AfterContextLines = afterContextLines ?? Array.Empty<LogLine>();
    }

    public Guid Id { get; }

    public DateTimeOffset Timestamp { get; }

    public DateTimeOffset DetectedAt => Timestamp;

    public string TimestampText => Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string TimeText => Timestamp.LocalDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string RuleName { get; }

    public string Keyword { get; }

    public LogDirection Direction { get; }

    public string Message { get; }

    public string MessagePreview => Message;

    public LogLine? SourceLogLine { get; }

    public LogLine? SourceLine => SourceLogLine;

    public IReadOnlyList<LogLine> BeforeContextLines { get; }

    public IReadOnlyList<LogLine> AfterContextLines { get; }

    public string DirectionText => Direction switch
    {
        LogDirection.Tx => "TX >",
        LogDirection.Rx => "RX <",
        _ => "SYS"
    };

    public string CompactDirectionText => Direction switch
    {
        LogDirection.Tx => "TX",
        LogDirection.Rx => "RX",
        _ => "SYS"
    };

    public string Formatted => $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] {RuleName} {DirectionText} {Message}";
}
