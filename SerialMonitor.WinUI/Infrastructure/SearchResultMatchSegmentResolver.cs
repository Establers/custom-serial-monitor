using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

public static class SearchResultMatchSegmentResolver
{
    private const int MaxHighlightedMatches = 64;

    public static IReadOnlyList<EventTextSegment> Resolve(
        string message,
        string? searchText,
        StringComparison comparison)
    {
        var options = new VisibleLogSearchOptions(
            MatchCase: comparison is StringComparison.Ordinal or StringComparison.CurrentCulture or StringComparison.InvariantCulture,
            LiteralComparison: comparison);
        return Resolve(message, searchText, options);
    }

    internal static IReadOnlyList<EventTextSegment> Resolve(
        string message,
        string? searchText,
        VisibleLogSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(searchText))
        {
            return [new EventTextSegment(message, isMatch: false)];
        }

        var matcher = new VisibleLogSearchMatcher(searchText, options);
        return Resolve(message, matcher, CancellationToken.None);
    }

    internal static IReadOnlyList<EventTextSegment> Resolve(
        string message,
        VisibleLogSearchMatcher matcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var segments = new List<EventTextSegment>();
        var consumedPosition = 0;
        var searchPosition = 0;
        var highlightedMatches = 0;
        while (highlightedMatches < MaxHighlightedMatches &&
               matcher.TryFindNext(
                   message,
                   payloadStart: 0,
                   searchPosition,
                   cancellationToken,
                   out var match))
        {
            if (match.PayloadOffset > consumedPosition)
            {
                segments.Add(new EventTextSegment(
                    message[consumedPosition..match.PayloadOffset],
                    isMatch: false));
            }

            segments.Add(new EventTextSegment(
                message.Substring(match.PayloadOffset, match.Length),
                isMatch: true));
            consumedPosition = match.PayloadOffset + match.Length;
            searchPosition = VisibleLogSearchMatcher.GetNextSearchStart(
                match.PayloadOffset,
                match.Length);
            highlightedMatches++;
        }

        if (consumedPosition < message.Length)
        {
            segments.Add(new EventTextSegment(message[consumedPosition..], isMatch: false));
        }

        return segments.Count == 0
            ? [new EventTextSegment(message, isMatch: false)]
            : segments;
    }
}
