using System.Globalization;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed class VisibleSearchResult
{
    public VisibleSearchResult(
        long matchIndex,
        int visibleLineIndex,
        long lineId,
        int payloadOffset,
        int matchCountInLine,
        string timeText,
        string directionText,
        string messagePreview,
        string fullText,
        IReadOnlyList<EventTextSegment>? messageSegments = null)
    {
        MatchIndex = matchIndex;
        VisibleLineIndex = visibleLineIndex;
        LineId = lineId;
        PayloadOffset = payloadOffset;
        MatchCountInLine = matchCountInLine;
        TimeText = timeText;
        DirectionText = directionText;
        MessagePreview = messagePreview;
        FullText = fullText;
        MessageSegments = messageSegments ?? [new EventTextSegment(messagePreview, isMatch: false)];
    }

    public long MatchIndex { get; }

    public int VisibleLineIndex { get; }

    public long LineId { get; }

    public int PayloadOffset { get; }

    public int MatchCountInLine { get; }

    public string TimeText { get; }

    public string DirectionText { get; }

    public string MessagePreview { get; }

    public IReadOnlyList<EventTextSegment> MessageSegments { get; }

    public string FullText { get; }
}

internal static class VisibleSearchResultParser
{
    public static VisibleSearchResult Create(
        long matchIndex,
        int visibleLineIndex,
        long lineId,
        int payloadOffset,
        int matchCountInLine,
        string fullText,
        LogDirection direction = LogDirection.System,
        string? searchText = null,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase,
        VisibleLogSearchOptions? searchOptions = null,
        VisibleLogSearchMatcher? searchMatcher = null,
        CancellationToken cancellationToken = default,
        int? payloadStart = null,
        long? timestampUnixMilliseconds = null)
    {
        var timeText = string.Empty;
        string directionText;
        string messagePreview;
        if (payloadStart is { } knownPayloadStart)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(knownPayloadStart, nameof(payloadStart));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(knownPayloadStart, fullText.Length, nameof(payloadStart));
            // Formatting already knows the metadata boundary. Device payloads can
            // begin with timestamps, direction-like text, and meaningful spaces.
            TrySplitTimestamp(fullText[..knownPayloadStart], out timeText, out _);
            directionText = direction switch
            {
                LogDirection.Rx => "RX",
                LogDirection.Tx => "TX",
                LogDirection.Mark => "MARK",
                _ => "SYS"
            };
            messagePreview = fullText[knownPayloadStart..];
        }
        else
        {
            TrySplitTimestamp(fullText, out timeText, out var bodyText);
            ParseBody(bodyText, out directionText, out messagePreview);
            if (string.IsNullOrEmpty(directionText))
            {
                directionText = direction switch
                {
                    LogDirection.Rx => "RX",
                    LogDirection.Tx => "TX",
                    _ => directionText
                };
            }
        }

        // Search rows keep their reception time even when the main log hides
        // timestamps or displays them with reduced precision.
        if (timestampUnixMilliseconds is { } timestamp)
        {
            timeText = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).LocalDateTime
                .ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        var options = searchOptions ?? new VisibleLogSearchOptions(
            MatchCase: comparison is StringComparison.Ordinal or StringComparison.CurrentCulture or StringComparison.InvariantCulture,
            LiteralComparison: comparison);
        var messageSegments = searchMatcher is not null
            ? SearchResultMatchSegmentResolver.Resolve(
                messagePreview,
                searchMatcher,
                cancellationToken)
            : SearchResultMatchSegmentResolver.Resolve(
                messagePreview,
                searchText,
                options);

        return new VisibleSearchResult(
            matchIndex,
            visibleLineIndex,
            lineId,
            payloadOffset,
            matchCountInLine,
            timeText,
            directionText,
            messagePreview,
            fullText,
            messageSegments);
    }

    private static bool TrySplitTimestamp(
        string text,
        out string timeText,
        out string bodyText)
    {
        timeText = string.Empty;
        bodyText = text;

        if (text.Length < 3 || text[0] != '[')
        {
            return false;
        }

        var closingBracketIndex = text.IndexOf(']');
        if (closingBracketIndex <= 1)
        {
            return false;
        }

        var timestampText = text[1..closingBracketIndex];
        var lastSpaceIndex = timestampText.LastIndexOf(' ');
        var timeCandidate = lastSpaceIndex >= 0
            ? timestampText[(lastSpaceIndex + 1)..]
            : timestampText;

        if (!IsSupportedTime(timeCandidate))
        {
            return false;
        }

        if (lastSpaceIndex >= 0)
        {
            var dateCandidate = timestampText[..lastSpaceIndex];
            if (!DateTime.TryParseExact(
                    dateCandidate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _))
            {
                return false;
            }
        }

        timeText = timeCandidate;
        var bodyStart = closingBracketIndex + 1;
        if (bodyStart < text.Length && text[bodyStart] == ' ')
        {
            bodyStart++;
        }
        bodyText = text[bodyStart..];
        return true;
    }

    private static bool IsSupportedTime(string text) =>
        DateTime.TryParseExact(
            text,
            "HH:mm:ss.fff",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _) ||
        DateTime.TryParseExact(
            text,
            "HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);

    private static void ParseBody(
        string text,
        out string directionText,
        out string messagePreview)
    {
        directionText = string.Empty;
        messagePreview = text;

        if (text.StartsWith("RX <", StringComparison.Ordinal) ||
            text.StartsWith("TX >", StringComparison.Ordinal))
        {
            directionText = text[..2];
            messagePreview = text.Length > 5 ? text[5..] : string.Empty;
            return;
        }

        if (text.StartsWith("MARK >", StringComparison.Ordinal))
        {
            directionText = "MARK";
            messagePreview = text.Length > 7 ? text[7..] : string.Empty;
            return;
        }

        if (text == "SYS" || text.StartsWith("SYS ", StringComparison.Ordinal))
        {
            directionText = "SYS";
            messagePreview = text.Length > 4 ? text[4..] : string.Empty;
        }
    }
}
