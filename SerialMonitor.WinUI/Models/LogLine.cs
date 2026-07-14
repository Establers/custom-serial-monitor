using System.Globalization;

namespace SerialMonitor.WinUI.Models;

public enum LogDirection
{
    Rx,
    Tx,
    Mark,
    System
}

public sealed class LogLine
{
    public LogLine(
        DateTimeOffset timestamp,
        LogDirection direction,
        string text,
        byte[]? rawBytes = null,
        long? sequenceNumber = null,
        bool isPartialRxSegment = false,
        bool isPartialRxTerminator = false,
        string? displayText = null,
        LogRuleMatchMode contentMode = LogRuleMatchMode.Text)
    {
        Timestamp = timestamp;
        Direction = direction;
        Text = text;
        DisplayText = displayText ?? text;
        RawBytes = rawBytes;
        SequenceNumber = sequenceNumber;
        IsPartialRxSegment = isPartialRxSegment;
        IsPartialRxTerminator = isPartialRxTerminator;
        ContentMode = contentMode;
    }

    public DateTimeOffset Timestamp { get; }

    public LogDirection Direction { get; }

    public string Text { get; }

    // Text remains the decoded payload used by text rules and searches.
    // DisplayText is the presentation captured when the line was created
    // (for example, byte-exact HEX while the RX view is in HEX mode).
    public string DisplayText { get; }

    public string Message => Text;

    public byte[]? RawBytes { get; }

    public long? SequenceNumber { get; }

    public bool IsPartialRxSegment { get; }

    public bool IsPartialRxTerminator { get; }

    // Rules are deliberately mode-exclusive: HEX content accepts only HEX
    // rules, and terminal/text content accepts only Text rules.
    public LogRuleMatchMode ContentMode { get; }

    public string DirectionText => Direction switch
    {
        LogDirection.Tx => "TX >",
        LogDirection.Rx => "RX <",
        LogDirection.Mark => "MARK >",
        _ => "SYS"
    };

    public string Formatted =>
        $"[{Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}] {DirectionText} {DisplayText}";

    public static LogLine Rx(
        string text,
        byte[]? rawBytes = null,
        long? sequenceNumber = null,
        bool isPartialRxSegment = false,
        string? displayText = null,
        LogRuleMatchMode contentMode = LogRuleMatchMode.Text) =>
        new(
            DateTimeOffset.Now,
            LogDirection.Rx,
            text,
            rawBytes,
            sequenceNumber,
            isPartialRxSegment: isPartialRxSegment,
            displayText: displayText,
            contentMode: contentMode);

    public static LogLine RxPartialTerminator(long? sequenceNumber = null) =>
        new(DateTimeOffset.Now, LogDirection.Rx, string.Empty, null, sequenceNumber, isPartialRxTerminator: true);

    public static LogLine Tx(
        string text,
        byte[]? rawBytes = null,
        long? sequenceNumber = null,
        LogRuleMatchMode contentMode = LogRuleMatchMode.Text) =>
        new(DateTimeOffset.Now, LogDirection.Tx, text, rawBytes, sequenceNumber, contentMode: contentMode);

    public static LogLine Mark(string text) => new(DateTimeOffset.Now, LogDirection.Mark, text);

    public static LogLine System(string text) => new(DateTimeOffset.Now, LogDirection.System, text);
}
