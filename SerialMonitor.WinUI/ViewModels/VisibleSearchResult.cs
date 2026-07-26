using System.Globalization;

namespace SerialMonitor.WinUI.ViewModels;

public sealed class VisibleSearchResult
{
    public VisibleSearchResult(
        int matchIndex,
        int visibleLineIndex,
        long lineId,
        string timeText,
        string directionText,
        string messagePreview,
        string fullText)
    {
        MatchIndex = matchIndex;
        VisibleLineIndex = visibleLineIndex;
        LineId = lineId;
        TimeText = timeText;
        DirectionText = directionText;
        MessagePreview = messagePreview;
        FullText = fullText;
    }

    public int MatchIndex { get; }

    public int VisibleLineIndex { get; }

    public long LineId { get; }

    public string TimeText { get; }

    public string DirectionText { get; }

    public string MessagePreview { get; }

    public string FullText { get; }
}

internal static class VisibleSearchResultParser
{
    public static VisibleSearchResult Create(
        int matchIndex,
        int visibleLineIndex,
        long lineId,
        string fullText)
    {
        var timeText = string.Empty;
        var bodyText = fullText;

        TrySplitTimestamp(fullText, out timeText, out bodyText);
        ParseBody(bodyText, out var directionText, out var messagePreview);

        return new VisibleSearchResult(
            matchIndex,
            visibleLineIndex,
            lineId,
            timeText,
            directionText,
            messagePreview,
            fullText);
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
        bodyText = text[(closingBracketIndex + 1)..].TrimStart();
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

        if (text.StartsWith("SYS", StringComparison.Ordinal))
        {
            directionText = "SYS";
            messagePreview = text.Length > 4 ? text[4..] : string.Empty;
        }
    }
}
