using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

internal readonly record struct SearchResultPreview(string Text, IReadOnlyList<EventTextSegment> Segments)
{
    // Keep rows bounded and put the first match near the start even in a narrow inspector.
    private const int MaximumLength = 160;
    private const int LeadingContextLength = 12;
    private const int MaximumHighlights = 64;

    public static SearchResultPreview Create(
        string message, VisibleLogSearchMatcher? matcher, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var match = default(VisibleLogSearchMatch);
        var found = matcher is not null &&
            matcher.TryFindNext(message, 0, 0, cancellationToken, out match);
        var start = found && match.PayloadOffset > 32
            ? Math.Max(0, match.PayloadOffset - LeadingContextLength)
            : 0;
        if (start > 0 && char.IsLowSurrogate(message[start]) && char.IsHighSurrogate(message[start - 1])) start--;
        var end = start + Math.Min(MaximumLength, message.Length - start);
        if (end < message.Length && char.IsHighSurrogate(message[end - 1]) && char.IsLowSurrogate(message[end])) end--;

        var segments = new List<EventTextSegment>();
        if (start > 0) segments.Add(new EventTextSegment("…", isMatch: false));
        var cursor = start;
        var highlights = 0;
        // Match against the original payload, not the snippet: anchors, lookarounds,
        // and whole-word boundaries must have exactly the same meaning as the search.
        while (found && match.PayloadOffset < end && highlights++ < MaximumHighlights)
        {
            var matchStart = Math.Max(start, match.PayloadOffset);
            var matchEnd = Math.Min(end, match.PayloadOffset + match.Length);
            if (cursor < matchStart) segments.Add(new EventTextSegment(message[cursor..matchStart], isMatch: false));
            if (matchStart < matchEnd) segments.Add(new EventTextSegment(message[matchStart..matchEnd], isMatch: true));
            cursor = matchEnd;
            if (cursor >= end) break;
            found = matcher!.TryFindNext(message, 0,
                VisibleLogSearchMatcher.GetNextSearchStart(match.PayloadOffset, match.Length),
                cancellationToken, out match);
        }
        if (cursor < end) segments.Add(new EventTextSegment(message[cursor..end], isMatch: false));
        if (end < message.Length) segments.Add(new EventTextSegment("…", isMatch: false));
        return new SearchResultPreview(string.Concat(segments.Select(segment => segment.Text)), segments);
    }
}
