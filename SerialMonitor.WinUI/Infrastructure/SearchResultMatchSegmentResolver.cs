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
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(searchText))
        {
            return [new EventTextSegment(message, isMatch: false)];
        }

        var segments = new List<EventTextSegment>();
        var position = 0;
        var highlightedMatches = 0;
        while (position <= message.Length - searchText.Length &&
               highlightedMatches < MaxHighlightedMatches)
        {
            var matchStart = message.IndexOf(searchText, position, comparison);
            if (matchStart < 0)
            {
                break;
            }

            if (matchStart > position)
            {
                segments.Add(new EventTextSegment(message[position..matchStart], isMatch: false));
            }

            segments.Add(new EventTextSegment(
                message.Substring(matchStart, searchText.Length),
                isMatch: true));
            position = matchStart + searchText.Length;
            highlightedMatches++;
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
