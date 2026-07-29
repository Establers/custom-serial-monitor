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
        bool trayNotificationEnabled = false,
        bool soundNotificationEnabled = false,
        bool popupNotificationEnabled = false,
        int notificationCooldownSeconds = 30,
        bool showInEventList = true,
        string? triggerSequenceName = null,
        IReadOnlyList<TextMatchRange>? matchRanges = null,
        string? matchForegroundColor = null,
        string? matchBackgroundColor = null,
        Guid? id = null,
        bool buildMessageSegments = true)
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
        TrayNotificationEnabled = trayNotificationEnabled;
        SoundNotificationEnabled = soundNotificationEnabled;
        PopupNotificationEnabled = popupNotificationEnabled;
        NotificationCooldownSeconds = Math.Clamp(notificationCooldownSeconds, 5, 3_600);
        ShowInEventList = showInEventList;
        TriggerSequenceName = string.IsNullOrWhiteSpace(triggerSequenceName)
            ? null
            : triggerSequenceName.Trim();
        MessageSegments = buildMessageSegments
            ? CreateMessageSegments(
                Message,
                matchRanges,
                matchForegroundColor,
                matchBackgroundColor)
            : Array.Empty<EventTextSegment>();
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

    public bool TrayNotificationEnabled { get; }

    public bool SoundNotificationEnabled { get; }

    public bool PopupNotificationEnabled { get; }

    public int NotificationCooldownSeconds { get; }

    public bool ShowInEventList { get; }

    public string? TriggerSequenceName { get; }

    public IReadOnlyList<EventTextSegment> MessageSegments { get; }

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

    private static IReadOnlyList<EventTextSegment> CreateMessageSegments(
        string message,
        IReadOnlyList<TextMatchRange>? matchRanges,
        string? foregroundColor,
        string? backgroundColor)
    {
        if (string.IsNullOrEmpty(message) || matchRanges is not { Count: > 0 })
        {
            return [new EventTextSegment(message, isMatch: false)];
        }

        var segments = new List<EventTextSegment>((matchRanges.Count * 2) + 1);
        var position = 0;
        foreach (var range in matchRanges.OrderBy(range => range.Start))
        {
            if (range.Start < position || range.Length <= 0 || range.Start + range.Length > message.Length)
            {
                continue;
            }

            if (range.Start > position)
            {
                segments.Add(new EventTextSegment(message[position..range.Start], isMatch: false));
            }

            segments.Add(new EventTextSegment(
                message.Substring(range.Start, range.Length),
                isMatch: true,
                foregroundColor,
                backgroundColor));
            position = range.Start + range.Length;
        }

        if (position < message.Length)
        {
            segments.Add(new EventTextSegment(message[position..], isMatch: false));
        }

        return segments.Count == 0
            ? [new EventTextSegment(message, isMatch: false)]
            : segments;
    }
}
