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
        bool isPartialRxTerminator = false)
    {
        Timestamp = timestamp;
        Direction = direction;
        Text = text;
        RawBytes = rawBytes;
        SequenceNumber = sequenceNumber;
        IsPartialRxSegment = isPartialRxSegment;
        IsPartialRxTerminator = isPartialRxTerminator;
    }

    public DateTimeOffset Timestamp { get; }

    public LogDirection Direction { get; }

    public string Text { get; }

    public string Message => Text;

    public byte[]? RawBytes { get; }

    public long? SequenceNumber { get; }

    public bool IsPartialRxSegment { get; }

    public bool IsPartialRxTerminator { get; }

    public string DirectionText => Direction switch
    {
        LogDirection.Tx => "TX >",
        LogDirection.Rx => "RX <",
        LogDirection.Mark => "MARK >",
        _ => "SYS"
    };

    public string Formatted =>
        $"[{Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}] {DirectionText} {Text}";

    public static LogLine Rx(
        string text,
        byte[]? rawBytes = null,
        long? sequenceNumber = null,
        bool isPartialRxSegment = false) =>
        new(DateTimeOffset.Now, LogDirection.Rx, text, rawBytes, sequenceNumber, isPartialRxSegment: isPartialRxSegment);

    public static LogLine RxPartialTerminator(long? sequenceNumber = null) =>
        new(DateTimeOffset.Now, LogDirection.Rx, string.Empty, null, sequenceNumber, isPartialRxTerminator: true);

    public static LogLine Tx(string text, byte[]? rawBytes = null, long? sequenceNumber = null) =>
        new(DateTimeOffset.Now, LogDirection.Tx, text, rawBytes, sequenceNumber);

    public static LogLine Mark(string text) => new(DateTimeOffset.Now, LogDirection.Mark, text);

    public static LogLine System(string text) => new(DateTimeOffset.Now, LogDirection.System, text);
}
