using System.Globalization;
using System.Text.RegularExpressions;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Infrastructure;

internal readonly record struct VisibleLogSearchOptions(
    bool MatchCase = false,
    bool MatchWholeWord = false,
    bool UseRegularExpression = false,
    StringComparison? LiteralComparison = null)
{
    public StringComparison Comparison => LiteralComparison ?? (MatchCase
        ? StringComparison.Ordinal
        : StringComparison.OrdinalIgnoreCase);
}

internal readonly record struct VisibleLogSearchMatch(int PayloadOffset, int Length);

internal readonly record struct VisibleLogMatchedLine(
    long LineId,
    int VisibleLineIndex,
    string FullText,
    int PayloadStart,
    int MatchCount,
    int FirstPayloadOffset,
    int FirstMatchLength,
    int LastPayloadOffset,
    int LastMatchLength,
    int FirstTabsBeforeMatch,
    int FirstTabsBeforeMatchEnd,
    int LastTabsBeforeMatch,
    int LastTabsBeforeMatchEnd,
    long FirstGlobalMatchIndex,
    LogDirection Direction,
    VisibleLogSearchCheckpoint[]? OccurrenceCheckpoints);

internal readonly record struct VisibleLogSearchCheckpoint(
    int PayloadOffset,
    int MatchLength,
    int TabsBeforeMatch,
    int TabsBeforeMatchEnd);

internal readonly record struct VisibleLogSearchPosition(
    int MatchedLineIndex,
    int OccurrenceInLine,
    int PayloadOffset,
    int MatchLength,
    long GlobalMatchIndex,
    int TabsBeforeMatch,
    int TabsBeforeMatchEnd);

internal readonly record struct VisibleLogSearchPage(
    int PageIndex,
    int PageCount,
    int StartIndex,
    int Count);

internal sealed class VisibleLogSearchSnapshot
{
    private const int OffsetCheckpointInterval = VisibleLogSearchEngine.OffsetCheckpointInterval;
    private readonly VisibleLogSearchMatcher _matcher;

    public VisibleLogSearchSnapshot(
        string searchText,
        VisibleLogSearchOptions options,
        VisibleLogMatchedLine[] matchedLines,
        long totalMatchCount)
    {
        SearchText = searchText;
        Options = options;
        Comparison = options.Comparison;
        MatchedLines = matchedLines;
        TotalMatchCount = totalMatchCount;
        _matcher = new VisibleLogSearchMatcher(searchText, options);
    }

    public string SearchText { get; }

    public VisibleLogSearchOptions Options { get; }

    public StringComparison Comparison { get; }

    public VisibleLogMatchedLine[] MatchedLines { get; }

    public long TotalMatchCount { get; }

    public VisibleLogSearchPage GetMatchedLinePage(int requestedPageIndex, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        if (MatchedLines.Length == 0)
        {
            return new VisibleLogSearchPage(0, 0, 0, 0);
        }

        var pageCount = ((MatchedLines.Length - 1) / pageSize) + 1;
        var pageIndex = Math.Clamp(requestedPageIndex, 0, pageCount - 1);
        var startIndex = pageIndex * pageSize;
        return new VisibleLogSearchPage(
            pageIndex,
            pageCount,
            startIndex,
            Math.Min(pageSize, MatchedLines.Length - startIndex));
    }

    public bool TryGetFirst(out VisibleLogSearchPosition position)
    {
        if (MatchedLines.Length == 0)
        {
            position = default;
            return false;
        }

        var line = MatchedLines[0];
        position = CreatePosition(0, 0, line.FirstGlobalMatchIndex, FirstCheckpoint(line));
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
            line.LastMatchLength,
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
            var searchStart = VisibleLogSearchMatcher.GetNextSearchStart(
                current.PayloadOffset,
                current.MatchLength);
            if (_matcher.TryFindNext(
                    line.FullText,
                    line.PayloadStart,
                    searchStart,
                    CancellationToken.None,
                    out var nextMatch))
            {
                var tabsBeforeMatch = current.TabsBeforeMatchEnd + VisibleLogSearchEngine.CountTabsInRange(
                    line.FullText,
                    line.PayloadStart + current.PayloadOffset + current.MatchLength,
                    line.PayloadStart + nextMatch.PayloadOffset);
                var tabsBeforeMatchEnd = tabsBeforeMatch + VisibleLogSearchEngine.CountTabsInRange(
                    line.FullText,
                    line.PayloadStart + nextMatch.PayloadOffset,
                    line.PayloadStart + nextMatch.PayloadOffset + nextMatch.Length);
                position = new VisibleLogSearchPosition(
                    current.MatchedLineIndex,
                    current.OccurrenceInLine + 1,
                    nextMatch.PayloadOffset,
                    nextMatch.Length,
                    current.GlobalMatchIndex + 1,
                    tabsBeforeMatch,
                    tabsBeforeMatchEnd);
                return true;
            }
        }

        var nextLineIndex = (current.MatchedLineIndex + 1) % MatchedLines.Length;
        var nextLine = MatchedLines[nextLineIndex];
        position = CreatePosition(
            nextLineIndex,
            0,
            nextLineIndex == 0 ? 0 : nextLine.FirstGlobalMatchIndex,
            FirstCheckpoint(nextLine));
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

        if (current.OccurrenceInLine > 0 &&
            TryGetOccurrencePosition(current.MatchedLineIndex, current.OccurrenceInLine - 1, out position))
        {
            return true;
        }

        var previousLineIndex = (current.MatchedLineIndex - 1 + MatchedLines.Length) % MatchedLines.Length;
        var previousLine = MatchedLines[previousLineIndex];
        position = new VisibleLogSearchPosition(
            previousLineIndex,
            previousLine.MatchCount - 1,
            previousLine.LastPayloadOffset,
            previousLine.LastMatchLength,
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
            var preparedPayload = _matcher.PreparePayload(line.FullText, line.PayloadStart);
            while (checkpoint.PayloadOffset <= payloadOffset && occurrence < line.MatchCount)
            {
                if (checkpoint.PayloadOffset == payloadOffset)
                {
                    position = CreatePosition(
                        lineIndex,
                        occurrence,
                        line.FirstGlobalMatchIndex + occurrence,
                        checkpoint);
                    return true;
                }

                if (!TryAdvance(line, checkpoint, preparedPayload, out checkpoint))
                {
                    break;
                }

                occurrence++;
            }

            break;
        }

        position = default;
        return false;
    }

    public VisibleLogMatchedLine GetLine(VisibleLogSearchPosition position) =>
        MatchedLines[position.MatchedLineIndex];

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
                line.LastMatchLength,
                line.FirstGlobalMatchIndex + occurrenceInLine,
                line.LastTabsBeforeMatch,
                line.LastTabsBeforeMatchEnd);
            return true;
        }

        var checkpointIndex = occurrenceInLine / OffsetCheckpointInterval;
        var currentOccurrence = checkpointIndex * OffsetCheckpointInterval;
        var checkpoint = GetCheckpoint(line, checkpointIndex);
        var preparedPayload = _matcher.PreparePayload(line.FullText, line.PayloadStart);
        while (currentOccurrence < occurrenceInLine)
        {
            if (!TryAdvance(line, checkpoint, preparedPayload, out checkpoint))
            {
                position = default;
                return false;
            }

            currentOccurrence++;
        }

        position = CreatePosition(
            matchedLineIndex,
            occurrenceInLine,
            line.FirstGlobalMatchIndex + occurrenceInLine,
            checkpoint);
        return true;
    }

    private bool TryAdvance(
        VisibleLogMatchedLine line,
        VisibleLogSearchCheckpoint current,
        string? preparedPayload,
        out VisibleLogSearchCheckpoint next)
    {
        var searchStart = VisibleLogSearchMatcher.GetNextSearchStart(
            current.PayloadOffset,
            current.MatchLength);
        if (!_matcher.TryFindNext(
                line.FullText,
                line.PayloadStart,
                preparedPayload,
                searchStart,
                CancellationToken.None,
                out var match))
        {
            next = default;
            return false;
        }

        var tabsBeforeMatch = current.TabsBeforeMatchEnd + VisibleLogSearchEngine.CountTabsInRange(
            line.FullText,
            line.PayloadStart + current.PayloadOffset + current.MatchLength,
            line.PayloadStart + match.PayloadOffset);
        var tabsBeforeMatchEnd = tabsBeforeMatch + VisibleLogSearchEngine.CountTabsInRange(
            line.FullText,
            line.PayloadStart + match.PayloadOffset,
            line.PayloadStart + match.PayloadOffset + match.Length);
        next = new VisibleLogSearchCheckpoint(
            match.PayloadOffset,
            match.Length,
            tabsBeforeMatch,
            tabsBeforeMatchEnd);
        return true;
    }

    private static VisibleLogSearchPosition CreatePosition(
        int lineIndex,
        int occurrence,
        long globalIndex,
        VisibleLogSearchCheckpoint checkpoint) =>
        new(
            lineIndex,
            occurrence,
            checkpoint.PayloadOffset,
            checkpoint.MatchLength,
            globalIndex,
            checkpoint.TabsBeforeMatch,
            checkpoint.TabsBeforeMatchEnd);

    private static VisibleLogSearchCheckpoint FirstCheckpoint(VisibleLogMatchedLine line) =>
        new(
            line.FirstPayloadOffset,
            line.FirstMatchLength,
            line.FirstTabsBeforeMatch,
            line.FirstTabsBeforeMatchEnd);

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
            return FirstCheckpoint(line);
        }

        var checkpoints = line.OccurrenceCheckpoints;
        return checkpoints is not null && checkpointIndex < checkpoints.Length
            ? checkpoints[checkpointIndex]
            : FirstCheckpoint(line);
    }
}

internal sealed class VisibleLogSearchMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private readonly string _searchText;
    private readonly VisibleLogSearchOptions _options;
    private readonly Regex? _regex;

    public VisibleLogSearchMatcher(string searchText, VisibleLogSearchOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(searchText);
        _searchText = searchText;
        _options = options;

        if (options.UseRegularExpression)
        {
            var pattern = options.MatchWholeWord
                ? $@"(?<!\w)(?:{searchText})(?!\w)"
                : searchText;
            var regexOptions = RegexOptions.CultureInvariant;
            if (!options.MatchCase)
            {
                regexOptions |= RegexOptions.IgnoreCase;
            }

            _regex = new Regex(pattern, regexOptions, RegexTimeout);
        }
    }

    public bool TryFindNext(
        string fullText,
        int payloadStart,
        int startPayloadOffset,
        CancellationToken cancellationToken,
        out VisibleLogSearchMatch result) =>
        TryFindNext(
            fullText,
            payloadStart,
            preparedPayload: null,
            startPayloadOffset,
            cancellationToken,
            out result);

    public bool TryFindNext(
        string fullText,
        int payloadStart,
        string? preparedPayload,
        int startPayloadOffset,
        CancellationToken cancellationToken,
        out VisibleLogSearchMatch result)
    {
        var payloadLength = Math.Max(0, fullText.Length - payloadStart);
        if (startPayloadOffset < 0 || startPayloadOffset > payloadLength)
        {
            result = default;
            return false;
        }

        if (_regex is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = preparedPayload ?? PreparePayload(fullText, payloadStart)!;
            var match = _regex.Match(payload, startPayloadOffset);
            while (match.Success && match.Length == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (match.Index >= payload.Length)
                {
                    result = default;
                    return false;
                }

                match = _regex.Match(payload, match.Index + 1);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!match.Success)
            {
                result = default;
                return false;
            }

            result = new VisibleLogSearchMatch(match.Index, match.Length);
            return true;
        }

        var absoluteStart = payloadStart + startPayloadOffset;
        while (absoluteStart <= fullText.Length - _searchText.Length)
        {
            var absoluteOffset = VisibleLogSearchEngine.IndexOfWithCancellation(
                fullText,
                _searchText,
                absoluteStart,
                _options.Comparison,
                cancellationToken);
            if (absoluteOffset < payloadStart)
            {
                break;
            }

            var payloadOffset = absoluteOffset - payloadStart;
            if (!_options.MatchWholeWord || IsWholeWord(fullText, payloadStart, payloadLength, payloadOffset, _searchText.Length))
            {
                result = new VisibleLogSearchMatch(payloadOffset, _searchText.Length);
                return true;
            }

            absoluteStart = absoluteOffset + Math.Max(1, _searchText.Length);
        }

        result = default;
        return false;
    }

    public string? PreparePayload(string fullText, int payloadStart) =>
        _regex is null
            ? null
            : payloadStart == 0
                ? fullText
                : fullText[payloadStart..];

    public static int GetNextSearchStart(int payloadOffset, int matchLength) =>
        payloadOffset + Math.Max(1, matchLength);

    private static bool IsWholeWord(
        string fullText,
        int payloadStart,
        int payloadLength,
        int payloadOffset,
        int matchLength)
    {
        var hasWordBefore = payloadOffset > 0 &&
            IsWordCharacter(fullText[payloadStart + payloadOffset - 1]);
        var matchEnd = payloadOffset + matchLength;
        var hasWordAfter = matchEnd < payloadLength &&
            IsWordCharacter(fullText[payloadStart + matchEnd]);
        return !hasWordBefore && !hasWordAfter;
    }

    private static bool IsWordCharacter(char value)
    {
        var category = char.GetUnicodeCategory(value);
        return category is UnicodeCategory.UppercaseLetter or
            UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or
            UnicodeCategory.ModifierLetter or
            UnicodeCategory.OtherLetter or
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.DecimalDigitNumber or
            UnicodeCategory.ConnectorPunctuation;
    }
}

internal static class VisibleLogSearchEngine
{
    internal const int OffsetCheckpointInterval = 128;
    private const int CancellationSearchWindowCharacters = 64 * 1024;

    public static VisibleLogSearchSnapshot Build(
        IReadOnlyList<VisibleLogSearchLine> lines,
        string searchText,
        StringComparison comparison,
        CancellationToken cancellationToken = default) =>
        Build(
            lines,
            searchText,
            new VisibleLogSearchOptions(
                MatchCase: comparison is StringComparison.Ordinal or StringComparison.CurrentCulture or StringComparison.InvariantCulture,
                LiteralComparison: comparison),
            cancellationToken);

    public static VisibleLogSearchSnapshot Build(
        IReadOnlyList<VisibleLogSearchLine> lines,
        string searchText,
        VisibleLogSearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(searchText);
        var matcher = new VisibleLogSearchMatcher(searchText, options);
        var matchedLines = new List<VisibleLogMatchedLine>();
        long totalMatchCount = 0;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            if ((lineIndex & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var line = lines[lineIndex];
            var first = default(VisibleLogSearchMatch);
            var last = default(VisibleLogSearchMatch);
            var firstTabsBeforeMatch = 0;
            var firstTabsBeforeMatchEnd = 0;
            var lastTabsBeforeMatch = 0;
            var lastTabsBeforeMatchEnd = 0;
            var matchCount = 0;
            var searchStart = 0;
            var tabScanStart = line.PayloadStart;
            var tabsSeen = 0;
            var preparedPayload = matcher.PreparePayload(line.FullText, line.PayloadStart);
            List<VisibleLogSearchCheckpoint>? occurrenceCheckpoints = null;

            while (matcher.TryFindNext(
                line.FullText,
                line.PayloadStart,
                preparedPayload,
                searchStart,
                cancellationToken,
                out var match))
            {
                if ((matchCount & 0xFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var absoluteOffset = line.PayloadStart + match.PayloadOffset;
                tabsSeen += CountTabsInRange(line.FullText, tabScanStart, absoluteOffset, cancellationToken);
                var tabsBeforeMatch = tabsSeen;
                var matchEnd = absoluteOffset + match.Length;
                tabsSeen += CountTabsInRange(line.FullText, absoluteOffset, matchEnd, cancellationToken);
                var tabsBeforeMatchEnd = tabsSeen;
                tabScanStart = matchEnd;

                if (matchCount == 0)
                {
                    first = match;
                    firstTabsBeforeMatch = tabsBeforeMatch;
                    firstTabsBeforeMatchEnd = tabsBeforeMatchEnd;
                }

                last = match;
                lastTabsBeforeMatch = tabsBeforeMatch;
                lastTabsBeforeMatchEnd = tabsBeforeMatchEnd;
                if (matchCount > 0 && matchCount % OffsetCheckpointInterval == 0)
                {
                    occurrenceCheckpoints ??=
                    [
                        new VisibleLogSearchCheckpoint(
                            first.PayloadOffset,
                            first.Length,
                            firstTabsBeforeMatch,
                            firstTabsBeforeMatchEnd)
                    ];
                    occurrenceCheckpoints.Add(new VisibleLogSearchCheckpoint(
                        match.PayloadOffset,
                        match.Length,
                        tabsBeforeMatch,
                        tabsBeforeMatchEnd));
                }

                matchCount++;
                totalMatchCount++;
                searchStart = VisibleLogSearchMatcher.GetNextSearchStart(match.PayloadOffset, match.Length);
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
                first.PayloadOffset,
                first.Length,
                last.PayloadOffset,
                last.Length,
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
            options,
            matchedLines.ToArray(),
            totalMatchCount);
    }

    internal static int IndexOfWithCancellation(
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
            var windowEnd = (int)Math.Min(boundedEnd, (long)windowStart + CancellationSearchWindowCharacters);
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
