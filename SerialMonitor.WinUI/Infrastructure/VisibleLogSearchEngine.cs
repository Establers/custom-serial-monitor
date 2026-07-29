using SerialMonitor.WinUI.ViewModels;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

internal readonly record struct VisibleLogMatchedLine(
    long LineId,
    int VisibleLineIndex,
    string FullText,
    int PayloadStart,
    int MatchCount,
    int FirstPayloadOffset,
    int LastPayloadOffset,
    int FirstTabsBeforeMatch,
    int FirstTabsBeforeMatchEnd,
    int LastTabsBeforeMatch,
    int LastTabsBeforeMatchEnd,
    long FirstGlobalMatchIndex,
    LogDirection Direction,
    VisibleLogSearchCheckpoint[]? OccurrenceCheckpoints);

internal readonly record struct VisibleLogSearchCheckpoint(
    int PayloadOffset,
    int TabsBeforeMatch,
    int TabsBeforeMatchEnd);

internal readonly record struct VisibleLogSearchPosition(
    int MatchedLineIndex,
    int OccurrenceInLine,
    int PayloadOffset,
    long GlobalMatchIndex,
    int TabsBeforeMatch,
    int TabsBeforeMatchEnd);

internal sealed class VisibleLogSearchSnapshot
{
    private const int OffsetCheckpointInterval = VisibleLogSearchEngine.OffsetCheckpointInterval;

    public VisibleLogSearchSnapshot(
        string searchText,
        StringComparison comparison,
        VisibleLogMatchedLine[] matchedLines,
        VisibleLogSearchPosition[] visibleResults,
        long totalMatchCount)
    {
        SearchText = searchText;
        Comparison = comparison;
        MatchedLines = matchedLines;
        VisibleResults = visibleResults;
        TotalMatchCount = totalMatchCount;
    }

    public string SearchText { get; }

    public StringComparison Comparison { get; }

    public VisibleLogMatchedLine[] MatchedLines { get; }

    public VisibleLogSearchPosition[] VisibleResults { get; }

    public long TotalMatchCount { get; }

    public bool TryGetFirst(out VisibleLogSearchPosition position)
    {
        if (MatchedLines.Length == 0)
        {
            position = default;
            return false;
        }

        var line = MatchedLines[0];
        position = new VisibleLogSearchPosition(
            0,
            0,
            line.FirstPayloadOffset,
            0,
            line.FirstTabsBeforeMatch,
            line.FirstTabsBeforeMatchEnd);
        return true;
    }

    public bool TryGetLast(out VisibleLogSearchPosition position)
    {
        if (MatchedLines.Length == 0)
        {
            position = default;
            return false;
        }

        var lineIndex = MatchedLines.Length - 1;
        var line = MatchedLines[lineIndex];
        position = new VisibleLogSearchPosition(
            lineIndex,
            line.MatchCount - 1,
            line.LastPayloadOffset,
            TotalMatchCount - 1,
            line.LastTabsBeforeMatch,
            line.LastTabsBeforeMatchEnd);
        return true;
    }

    public bool TryGetNext(VisibleLogSearchPosition current, out VisibleLogSearchPosition position)
    {
        if (MatchedLines.Length == 0)
        {
            position = default;
            return false;
        }

        if (current.MatchedLineIndex < 0 || current.MatchedLineIndex >= MatchedLines.Length)
        {
            return TryGetFirst(out position);
        }

        var line = MatchedLines[current.MatchedLineIndex];
        if (current.OccurrenceInLine + 1 < line.MatchCount)
        {
            var nextOffset = FindNextOffset(line, current.PayloadOffset + SearchText.Length);
            if (nextOffset >= 0)
            {
                var tabsBeforeMatch = current.TabsBeforeMatchEnd + CountTabs(
                    line,
                    current.PayloadOffset + SearchText.Length,
                    nextOffset);
                var tabsBeforeMatchEnd = tabsBeforeMatch + CountTabs(
                    line,
                    nextOffset,
                    nextOffset + SearchText.Length);
                position = new VisibleLogSearchPosition(
                    current.MatchedLineIndex,
                    current.OccurrenceInLine + 1,
                    nextOffset,
                    current.GlobalMatchIndex + 1,
                    tabsBeforeMatch,
                    tabsBeforeMatchEnd);
                return true;
            }
        }

        var nextLineIndex = (current.MatchedLineIndex + 1) % MatchedLines.Length;
        var nextLine = MatchedLines[nextLineIndex];
        var nextGlobalIndex = nextLineIndex == 0 ? 0 : nextLine.FirstGlobalMatchIndex;
        position = new VisibleLogSearchPosition(
            nextLineIndex,
            0,
            nextLine.FirstPayloadOffset,
            nextGlobalIndex,
            nextLine.FirstTabsBeforeMatch,
            nextLine.FirstTabsBeforeMatchEnd);
        return true;
    }

    public bool TryGetPrevious(VisibleLogSearchPosition current, out VisibleLogSearchPosition position)
    {
        if (MatchedLines.Length == 0)
        {
            position = default;
            return false;
        }

        if (current.MatchedLineIndex < 0 || current.MatchedLineIndex >= MatchedLines.Length)
        {
            return TryGetLast(out position);
        }

        var line = MatchedLines[current.MatchedLineIndex];
        if (current.OccurrenceInLine > 0)
        {
            if (TryGetOccurrencePosition(
                    current.MatchedLineIndex,
                    current.OccurrenceInLine - 1,
                    out position))
            {
                return true;
            }
        }

        var previousLineIndex = (current.MatchedLineIndex - 1 + MatchedLines.Length) % MatchedLines.Length;
        var previousLine = MatchedLines[previousLineIndex];
        position = new VisibleLogSearchPosition(
            previousLineIndex,
            previousLine.MatchCount - 1,
            previousLine.LastPayloadOffset,
            previousLine.FirstGlobalMatchIndex + previousLine.MatchCount - 1,
            previousLine.LastTabsBeforeMatch,
            previousLine.LastTabsBeforeMatchEnd);
        return true;
    }

    public bool TryFind(long lineId, int payloadOffset, out VisibleLogSearchPosition position)
    {
        var low = 0;
        var high = MatchedLines.Length - 1;
        while (low <= high)
        {
            var lineIndex = low + ((high - low) / 2);
            var line = MatchedLines[lineIndex];
            if (line.LineId < lineId)
            {
                low = lineIndex + 1;
                continue;
            }

            if (line.LineId > lineId)
            {
                high = lineIndex - 1;
                continue;
            }

            var checkpointIndex = FindCheckpointAtOrBefore(line, payloadOffset);
            var occurrence = checkpointIndex * OffsetCheckpointInterval;
            var checkpoint = GetCheckpoint(line, checkpointIndex);
            var offset = checkpoint.PayloadOffset;
            var tabsBeforeMatch = checkpoint.TabsBeforeMatch;
            var tabsBeforeMatchEnd = checkpoint.TabsBeforeMatchEnd;
            while (offset >= 0 && offset <= payloadOffset && occurrence < line.MatchCount)
            {
                if (offset == payloadOffset)
                {
                    position = new VisibleLogSearchPosition(
                        lineIndex,
                        occurrence,
                        offset,
                        line.FirstGlobalMatchIndex + occurrence,
                        tabsBeforeMatch,
                        tabsBeforeMatchEnd);
                    return true;
                }

                var previousOffset = offset;
                occurrence++;
                offset = FindNextOffset(line, offset + SearchText.Length);
                if (offset >= 0)
                {
                    tabsBeforeMatch = tabsBeforeMatchEnd + CountTabs(
                        line,
                        previousOffset + SearchText.Length,
                        offset);
                    tabsBeforeMatchEnd = tabsBeforeMatch + CountTabs(
                        line,
                        offset,
                        offset + SearchText.Length);
                }
            }

            break;
        }

        position = default;
        return false;
    }

    public VisibleLogMatchedLine GetLine(VisibleLogSearchPosition position) =>
        MatchedLines[position.MatchedLineIndex];

    private int FindNextOffset(VisibleLogMatchedLine line, int payloadOffset)
    {
        var absoluteStart = line.PayloadStart + Math.Max(0, payloadOffset);
        if (absoluteStart > line.FullText.Length - SearchText.Length)
        {
            return -1;
        }

        var absoluteOffset = line.FullText.IndexOf(SearchText, absoluteStart, Comparison);
        return absoluteOffset < line.PayloadStart ? -1 : absoluteOffset - line.PayloadStart;
    }

    private bool TryGetOccurrencePosition(
        int matchedLineIndex,
        int occurrenceInLine,
        out VisibleLogSearchPosition position)
    {
        var line = MatchedLines[matchedLineIndex];
        if (occurrenceInLine < 0 || occurrenceInLine >= line.MatchCount)
        {
            position = default;
            return false;
        }

        if (occurrenceInLine == line.MatchCount - 1)
        {
            position = new VisibleLogSearchPosition(
                matchedLineIndex,
                occurrenceInLine,
                line.LastPayloadOffset,
                line.FirstGlobalMatchIndex + occurrenceInLine,
                line.LastTabsBeforeMatch,
                line.LastTabsBeforeMatchEnd);
            return true;
        }

        var checkpointIndex = occurrenceInLine / OffsetCheckpointInterval;
        var currentOccurrence = checkpointIndex * OffsetCheckpointInterval;
        var checkpoint = GetCheckpoint(line, checkpointIndex);
        var offset = checkpoint.PayloadOffset;
        var tabsBeforeMatch = checkpoint.TabsBeforeMatch;
        var tabsBeforeMatchEnd = checkpoint.TabsBeforeMatchEnd;
        while (offset >= 0 && currentOccurrence < occurrenceInLine)
        {
            var previousOffset = offset;
            offset = FindNextOffset(line, offset + SearchText.Length);
            currentOccurrence++;
            if (offset >= 0)
            {
                tabsBeforeMatch = tabsBeforeMatchEnd + CountTabs(
                    line,
                    previousOffset + SearchText.Length,
                    offset);
                tabsBeforeMatchEnd = tabsBeforeMatch + CountTabs(
                    line,
                    offset,
                    offset + SearchText.Length);
            }
        }

        if (offset < 0)
        {
            position = default;
            return false;
        }

        position = new VisibleLogSearchPosition(
            matchedLineIndex,
            occurrenceInLine,
            offset,
            line.FirstGlobalMatchIndex + occurrenceInLine,
            tabsBeforeMatch,
            tabsBeforeMatchEnd);
        return true;
    }

    private static int FindCheckpointAtOrBefore(VisibleLogMatchedLine line, int payloadOffset)
    {
        var checkpoints = line.OccurrenceCheckpoints;
        if (checkpoints is null || checkpoints.Length == 0)
        {
            return 0;
        }

        var low = 0;
        var high = checkpoints.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (checkpoints[middle].PayloadOffset <= payloadOffset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return Math.Max(0, high);
    }

    private static VisibleLogSearchCheckpoint GetCheckpoint(
        VisibleLogMatchedLine line,
        int checkpointIndex)
    {
        if (checkpointIndex <= 0)
        {
            return new VisibleLogSearchCheckpoint(
                line.FirstPayloadOffset,
                line.FirstTabsBeforeMatch,
                line.FirstTabsBeforeMatchEnd);
        }

        var checkpoints = line.OccurrenceCheckpoints;
        return checkpoints is not null && checkpointIndex < checkpoints.Length
            ? checkpoints[checkpointIndex]
            : new VisibleLogSearchCheckpoint(
                line.FirstPayloadOffset,
                line.FirstTabsBeforeMatch,
                line.FirstTabsBeforeMatchEnd);
    }

    private static int CountTabs(
        VisibleLogMatchedLine line,
        int payloadStart,
        int payloadEnd) =>
        VisibleLogSearchEngine.CountTabsInRange(
            line.FullText,
            line.PayloadStart + payloadStart,
            line.PayloadStart + payloadEnd);
}

internal static class VisibleLogSearchEngine
{
    internal const int OffsetCheckpointInterval = 128;
    private const int CancellationSearchWindowCharacters = 64 * 1024;

    public static VisibleLogSearchSnapshot Build(
        IReadOnlyList<VisibleLogSearchLine> lines,
        string searchText,
        StringComparison comparison,
        int maxVisibleResults,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(searchText);
        ArgumentOutOfRangeException.ThrowIfNegative(maxVisibleResults);

        var matchedLines = new List<VisibleLogMatchedLine>();
        var visibleResults = new List<VisibleLogSearchPosition>(Math.Min(maxVisibleResults, lines.Count));
        long totalMatchCount = 0;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            if ((lineIndex & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var line = lines[lineIndex];
            var firstOffset = -1;
            var lastOffset = -1;
            var firstTabsBeforeMatch = 0;
            var firstTabsBeforeMatchEnd = 0;
            var lastTabsBeforeMatch = 0;
            var lastTabsBeforeMatchEnd = 0;
            var matchCount = 0;
            var searchStart = line.PayloadStart;
            var tabScanStart = line.PayloadStart;
            var tabsSeen = 0;
            List<VisibleLogSearchCheckpoint>? occurrenceCheckpoints = null;

            while (searchStart <= line.FullText.Length - searchText.Length)
            {
                if ((matchCount & 0xFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var absoluteOffset = IndexOfWithCancellation(
                    line.FullText,
                    searchText,
                    searchStart,
                    comparison,
                    cancellationToken);
                if (absoluteOffset < line.PayloadStart)
                {
                    break;
                }

                var payloadOffset = absoluteOffset - line.PayloadStart;
                tabsSeen += CountTabsInRange(
                    line.FullText,
                    tabScanStart,
                    absoluteOffset,
                    cancellationToken);
                var tabsBeforeMatch = tabsSeen;
                var matchEnd = absoluteOffset + searchText.Length;
                tabsSeen += CountTabsInRange(
                    line.FullText,
                    absoluteOffset,
                    matchEnd,
                    cancellationToken);
                var tabsBeforeMatchEnd = tabsSeen;
                tabScanStart = matchEnd;

                firstOffset = firstOffset < 0 ? payloadOffset : firstOffset;
                lastOffset = payloadOffset;
                if (matchCount == 0)
                {
                    firstTabsBeforeMatch = tabsBeforeMatch;
                    firstTabsBeforeMatchEnd = tabsBeforeMatchEnd;
                }

                lastTabsBeforeMatch = tabsBeforeMatch;
                lastTabsBeforeMatchEnd = tabsBeforeMatchEnd;
                if (matchCount > 0 && matchCount % OffsetCheckpointInterval == 0)
                {
                    occurrenceCheckpoints ??=
                    [
                        new VisibleLogSearchCheckpoint(
                            firstOffset,
                            firstTabsBeforeMatch,
                            firstTabsBeforeMatchEnd)
                    ];
                    occurrenceCheckpoints.Add(new VisibleLogSearchCheckpoint(
                        payloadOffset,
                        tabsBeforeMatch,
                        tabsBeforeMatchEnd));
                }

                if (visibleResults.Count < maxVisibleResults)
                {
                    visibleResults.Add(new VisibleLogSearchPosition(
                        matchedLines.Count,
                        matchCount,
                        payloadOffset,
                        totalMatchCount,
                        tabsBeforeMatch,
                        tabsBeforeMatchEnd));
                }

                matchCount++;
                totalMatchCount++;
                searchStart = absoluteOffset + searchText.Length;
            }

            if (matchCount == 0)
            {
                continue;
            }

            matchedLines.Add(new VisibleLogMatchedLine(
                line.LineId,
                lineIndex,
                line.FullText,
                line.PayloadStart,
                matchCount,
                firstOffset,
                lastOffset,
                firstTabsBeforeMatch,
                firstTabsBeforeMatchEnd,
                lastTabsBeforeMatch,
                lastTabsBeforeMatchEnd,
                totalMatchCount - matchCount,
                line.Direction,
                occurrenceCheckpoints?.ToArray()));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new VisibleLogSearchSnapshot(
            searchText,
            comparison,
            matchedLines.ToArray(),
            visibleResults.ToArray(),
            totalMatchCount);
    }

    private static int IndexOfWithCancellation(
        string source,
        string value,
        int startIndex,
        StringComparison comparison,
        CancellationToken cancellationToken)
    {
        var lastCandidateStart = source.Length - value.Length;
        var windowStart = startIndex;
        while (windowStart <= lastCandidateStart)
        {
            var windowLastCandidate = Math.Min(
                (long)lastCandidateStart,
                (long)windowStart + CancellationSearchWindowCharacters - 1);
            var searchCharacterCount = windowLastCandidate - windowStart + value.Length;
            var matchIndex = source.IndexOf(value, windowStart, (int)searchCharacterCount, comparison);
            if (matchIndex >= 0)
            {
                return matchIndex;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (windowLastCandidate >= lastCandidateStart)
            {
                return -1;
            }

            windowStart = (int)windowLastCandidate + 1;
        }

        return -1;
    }

    internal static int CountTabsInRange(
        string fullText,
        int startIndex,
        int endIndex,
        CancellationToken cancellationToken = default)
    {
        if (startIndex >= endIndex)
        {
            return 0;
        }

        var count = 0;
        var windowStart = Math.Max(0, startIndex);
        var boundedEnd = Math.Min(fullText.Length, endIndex);
        while (windowStart < boundedEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windowEnd = (int)Math.Min(
                boundedEnd,
                (long)windowStart + CancellationSearchWindowCharacters);
            var searchStart = windowStart;
            while (searchStart < windowEnd)
            {
                var absoluteOffset = fullText.IndexOf('\t', searchStart, windowEnd - searchStart);
                if (absoluteOffset < searchStart)
                {
                    break;
                }

                count++;
                if ((count & 0xFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                searchStart = absoluteOffset + 1;
            }

            windowStart = windowEnd;
        }

        return count;
    }
}
