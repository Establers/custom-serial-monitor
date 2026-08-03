using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;
using System.Text.RegularExpressions;

namespace SerialMonitor.WinUI.Tests;

public sealed class VisibleLogSearchEngineTests
{
    [Fact]
    public void Build_WholeWord_ExcludesIdentifierSubstrings()
    {
        var snapshot = VisibleLogSearchEngine.Build(
            [new VisibleLogSearchLine(1, "RX < error errorCode preerror error-error", 5)],
            "error",
            new VisibleLogSearchOptions(MatchWholeWord: true));

        Assert.Equal(3, snapshot.TotalMatchCount);
        Assert.True(snapshot.TryGetFirst(out var first));
        Assert.Equal(0, first.PayloadOffset);
        Assert.True(snapshot.TryGetNext(first, out var second));
        Assert.Equal(25, second.PayloadOffset);
    }

    [Fact]
    public void Build_Regex_TracksVariableMatchLengthsDuringNavigation()
    {
        var snapshot = VisibleLogSearchEngine.Build(
            [new VisibleLogSearchLine(1, "RX < id=7 id=2048", 5)],
            @"\d+",
            new VisibleLogSearchOptions(UseRegularExpression: true));

        Assert.Equal(2, snapshot.TotalMatchCount);
        Assert.True(snapshot.TryGetFirst(out var first));
        Assert.Equal((3, 1), (first.PayloadOffset, first.MatchLength));
        Assert.True(snapshot.TryGetNext(first, out var second));
        Assert.Equal((8, 4), (second.PayloadOffset, second.MatchLength));
        Assert.True(snapshot.TryGetPrevious(second, out var previous));
        Assert.Equal(first, previous);
    }

    [Fact]
    public void Build_RegexOptions_CombineCaseAndWholeWord()
    {
        var insensitive = VisibleLogSearchEngine.Build(
            [new VisibleLogSearchLine(1, "RX < WARN warning warn", 5)],
            "warn(?:ing)?",
            new VisibleLogSearchOptions(
                MatchWholeWord: true,
                UseRegularExpression: true));
        var sensitive = VisibleLogSearchEngine.Build(
            [new VisibleLogSearchLine(1, "RX < WARN warning warn", 5)],
            "warn(?:ing)?",
            new VisibleLogSearchOptions(
                MatchCase: true,
                MatchWholeWord: true,
                UseRegularExpression: true));

        Assert.Equal(3, insensitive.TotalMatchCount);
        Assert.Equal(2, sensitive.TotalMatchCount);
    }

    [Fact]
    public void Build_RegexAnchorsAtPayloadInsteadOfDisplayMetadata()
    {
        var snapshot = VisibleLogSearchEngine.Build(
            [new VisibleLogSearchLine(1, "RX < READY", 5)],
            "^READY$",
            new VisibleLogSearchOptions(UseRegularExpression: true));

        Assert.Equal(1, snapshot.TotalMatchCount);
        Assert.True(snapshot.TryGetFirst(out var match));
        Assert.Equal(0, match.PayloadOffset);
    }

    [Fact]
    public void Build_ZeroLengthRegex_IsExcludedFromResults()
    {
        var snapshot = VisibleLogSearchEngine.Build(
            [new VisibleLogSearchLine(1, "RX < aa", 5)],
            "(?=a)",
            new VisibleLogSearchOptions(UseRegularExpression: true));

        Assert.Equal(0, snapshot.TotalMatchCount);
        Assert.Empty(snapshot.MatchedLines);
        Assert.False(snapshot.TryGetFirst(out _));
    }

    [Fact]
    public void Build_StringComparisonOverloadPreservesCultureAndCaseContract()
    {
        var sensitive = VisibleLogSearchEngine.Build(
            [new VisibleLogSearchLine(1, "RX < a A", 5)],
            "A",
            StringComparison.InvariantCulture);
        var insensitive = VisibleLogSearchEngine.Build(
            [new VisibleLogSearchLine(1, "RX < a A", 5)],
            "A",
            StringComparison.InvariantCultureIgnoreCase);

        Assert.Equal(1, sensitive.TotalMatchCount);
        Assert.Equal(StringComparison.InvariantCulture, sensitive.Comparison);
        Assert.Equal(2, insensitive.TotalMatchCount);
        Assert.Equal(StringComparison.InvariantCultureIgnoreCase, insensitive.Comparison);
    }

    [Fact]
    public void Build_InvalidRegex_ReportsArgumentError()
    {
        Assert.Throws<RegexParseException>(() => VisibleLogSearchEngine.Build(
            [new VisibleLogSearchLine(1, "RX < READY", 5)],
            "[",
            new VisibleLogSearchOptions(UseRegularExpression: true)));
    }

    [Fact]
    public void Build_CountsEveryNonOverlappingOccurrenceInOneLine()
    {
        var lines = new[]
        {
            new VisibleLogSearchLine(7, "RX < open_rx_mq() ...  rx_mq", 5)
        };

        var snapshot = VisibleLogSearchEngine.Build(
            lines,
            "rx_mq",
            StringComparison.Ordinal);

        Assert.Equal(2, snapshot.TotalMatchCount);
        Assert.Single(snapshot.MatchedLines);
        Assert.Equal(2, snapshot.MatchedLines[0].MatchCount);
        Assert.True(snapshot.TryGetFirst(out var first));
        Assert.True(snapshot.TryGetNext(first, out var second));
        Assert.Equal(5, first.PayloadOffset);
        Assert.True(second.PayloadOffset > first.PayloadOffset);
    }

    [Fact]
    public void Build_LeadingSpaceSelectsOnlyTheRightOccurrence()
    {
        var lines = new[]
        {
            new VisibleLogSearchLine(7, "RX < open_rx_mq() ...  rx_mq", 5)
        };

        var snapshot = VisibleLogSearchEngine.Build(
            lines,
            " rx_mq",
            StringComparison.Ordinal);

        Assert.True(snapshot.TryGetFirst(out var result));
        Assert.Equal(1, snapshot.TotalMatchCount);
        Assert.True(result.PayloadOffset > 5);
    }

    [Fact]
    public void Build_ConsecutiveTabsRemainSeparateOccurrencesAndCoordinates()
    {
        var snapshot = VisibleLogSearchEngine.Build(
            [new VisibleLogSearchLine(1, "RX < \t\tREADY", 5)],
            "\t",
            StringComparison.Ordinal);

        Assert.Equal(2, snapshot.TotalMatchCount);
        Assert.True(snapshot.TryGetFirst(out var first));
        Assert.True(snapshot.TryGetNext(first, out var second));
        Assert.Equal([0, 1], new[] { first.PayloadOffset, second.PayloadOffset });
        var line = Assert.Single(snapshot.MatchedLines);
        Assert.Null(line.OccurrenceCheckpoints);
        Assert.Equal(0, first.TabsBeforeMatch);
        Assert.Equal(1, first.TabsBeforeMatchEnd);
        Assert.Equal(1, second.TabsBeforeMatch);
        Assert.Equal(2, second.TabsBeforeMatchEnd);
    }

    [Fact]
    public void Build_OrdinalIgnoreCaseDoesNotUseJavaScriptUnicodeCaseFolding()
    {
        var snapshot = VisibleLogSearchEngine.Build(
            [new VisibleLogSearchLine(1, "RX < \tſ S K K ß ẞ", 5)],
            "S",
            StringComparison.OrdinalIgnoreCase);

        Assert.True(snapshot.TryGetFirst(out var result));
        Assert.Equal(1, snapshot.TotalMatchCount);
        Assert.Equal(3, result.PayloadOffset);
        Assert.Equal(1, result.TabsBeforeMatch);
        Assert.Equal(1, result.TabsBeforeMatchEnd);
    }

    [Fact]
    public void Navigation_VisitsOccurrencesWithinLineBeforeWrapping()
    {
        var lines = new[]
        {
            new VisibleLogSearchLine(1, "RX < rx_mq rx_mq", 5),
            new VisibleLogSearchLine(2, "RX < rx_mq", 5)
        };
        var snapshot = VisibleLogSearchEngine.Build(
            lines,
            "rx_mq",
            StringComparison.Ordinal);

        Assert.True(snapshot.TryGetFirst(out var first));
        Assert.True(snapshot.TryGetNext(first, out var second));
        Assert.Equal(first.MatchedLineIndex, second.MatchedLineIndex);
        Assert.Equal(1, second.OccurrenceInLine);

        Assert.True(snapshot.TryGetNext(second, out var third));
        Assert.Equal(1, third.MatchedLineIndex);

        Assert.True(snapshot.TryGetNext(third, out var wrapped));
        Assert.Equal(first, wrapped);

        Assert.True(snapshot.TryGetPrevious(first, out var previous));
        Assert.Equal(third, previous);
    }

    [Fact]
    public void PreviousNavigation_UsesTheSameNonOverlappingOffsetsAsBuild()
    {
        var snapshot = VisibleLogSearchEngine.Build(
            [new VisibleLogSearchLine(1, "RX < abababa", 5)],
            "aba",
            StringComparison.Ordinal);

        Assert.True(snapshot.TryGetLast(out var last));
        Assert.Equal(4, last.PayloadOffset);
        Assert.True(snapshot.TryGetPrevious(last, out var previous));
        Assert.Equal(0, previous.PayloadOffset);
    }

    [Fact]
    public void PreviousNavigation_DenseLineUsesBoundedOffsetCheckpoints()
    {
        const int occurrenceCount = 10_000;
        var snapshot = VisibleLogSearchEngine.Build(
            [new VisibleLogSearchLine(1, $"RX < {new string('a', occurrenceCount)}", 5)],
            "a",
            StringComparison.Ordinal);

        var line = Assert.Single(snapshot.MatchedLines);
        var checkpoints = Assert.IsType<VisibleLogSearchCheckpoint[]>(line.OccurrenceCheckpoints);
        Assert.Equal(
            ((occurrenceCount - 1) / VisibleLogSearchEngine.OffsetCheckpointInterval) + 1,
            checkpoints.Length);
        Assert.Equal(0, checkpoints[0].PayloadOffset);
        Assert.Equal(VisibleLogSearchEngine.OffsetCheckpointInterval, checkpoints[1].PayloadOffset);

        Assert.True(snapshot.TryGetLast(out var position));
        for (var expectedOccurrence = occurrenceCount - 2;
             expectedOccurrence >= occurrenceCount - 500;
             expectedOccurrence--)
        {
            Assert.True(snapshot.TryGetPrevious(position, out position));
            Assert.Equal(expectedOccurrence, position.OccurrenceInLine);
            Assert.Equal(expectedOccurrence, position.PayloadOffset);
        }
    }

    [Fact]
    public void MatchedLinePages_StayBoundedByLinesRatherThanOccurrences()
    {
        var lines = Enumerable.Range(0, 20_000)
            .Select(index => new VisibleLogSearchLine(index + 1, "RX < a a a a a a a a a a", 5))
            .ToArray();

        var snapshot = VisibleLogSearchEngine.Build(
            lines,
            "a",
            StringComparison.Ordinal);

        Assert.Equal(200_000, snapshot.TotalMatchCount);
        Assert.Equal(20_000, snapshot.MatchedLines.Length);
        var firstPage = snapshot.GetMatchedLinePage(0, 1000);
        var lastPage = snapshot.GetMatchedLinePage(int.MaxValue, 1000);
        Assert.Equal(new VisibleLogSearchPage(0, 20, 0, 1000), firstPage);
        Assert.Equal(new VisibleLogSearchPage(19, 20, 19_000, 1000), lastPage);
    }

    [Fact]
    public void MatchedLinePages_ClampToPartialNewestPage()
    {
        var lines = Enumerable.Range(0, 2_001)
            .Select(index => new VisibleLogSearchLine(index + 1, "RX < READY READY", 5))
            .ToArray();
        var snapshot = VisibleLogSearchEngine.Build(
            lines,
            "READY",
            StringComparison.Ordinal);

        Assert.Equal(4_002, snapshot.TotalMatchCount);
        Assert.Equal(2_001, snapshot.MatchedLines.Length);
        Assert.Equal(
            new VisibleLogSearchPage(2, 3, 2_000, 1),
            snapshot.GetMatchedLinePage(int.MaxValue, 1_000));
        Assert.Equal(
            new VisibleLogSearchPage(0, 3, 0, 1_000),
            snapshot.GetMatchedLinePage(-1, 1_000));
    }

    [Fact]
    public void Build_HonorsCancellation()
    {
        var lines = Enumerable.Range(0, 1000)
            .Select(index => new VisibleLogSearchLine(index + 1, "RX < READY", 5))
            .ToArray();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            VisibleLogSearchEngine.Build(
                lines,
                "READY",
                StringComparison.Ordinal,
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public void Build_HonorsCancellationRequestedWhileReadingOneDenseLine()
    {
        using var cancellation = new CancellationTokenSource();
        var lines = new CancelWhenReadList(
            new VisibleLogSearchLine(1, $"RX < {new string('a', 1_000_000)}", 5),
            cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            VisibleLogSearchEngine.Build(
                lines,
                "a",
                StringComparison.Ordinal,
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public void Build_FindsMatchAcrossCancellationSearchWindowBoundary()
    {
        var prefix = new string('x', (64 * 1024) - 2);
        var snapshot = VisibleLogSearchEngine.Build(
            [new VisibleLogSearchLine(1, $"RX < {prefix}READY", 5)],
            "READY",
            StringComparison.Ordinal);

        Assert.True(snapshot.TryGetFirst(out var result));
        Assert.Equal(prefix.Length, result.PayloadOffset);
    }

    [Fact]
    public void Build_TwoHundredThousandLines_RemainsBoundedByLinesNotOccurrences()
    {
        const int lineCount = 200_000;
        var lines = new VisibleLogSearchLine[lineCount];
        for (var index = 0; index < lines.Length; index++)
        {
            lines[index] = new VisibleLogSearchLine(index + 1, "RX < rx_mq ... rx_mq", 5);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var snapshot = VisibleLogSearchEngine.Build(
            lines,
            "rx_mq",
            StringComparison.Ordinal);
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(400_000, snapshot.TotalMatchCount);
        Assert.Equal(lineCount, snapshot.MatchedLines.Length);
        Assert.True(
            allocatedBytes < 64L * 1024 * 1024,
            $"Search engine allocated {allocatedBytes:N0} bytes.");
    }

    [Fact]
    public void Build_TwoHundredThousandTabHeavyLines_DoesNotAllocatePerLineTabArrays()
    {
        const int lineCount = 200_000;
        var fullText = $"RX < {new string('\t', 32)}READY";
        var lines = new VisibleLogSearchLine[lineCount];
        for (var index = 0; index < lines.Length; index++)
        {
            lines[index] = new VisibleLogSearchLine(index + 1, fullText, 5);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var snapshot = VisibleLogSearchEngine.Build(
            lines,
            "READY",
            StringComparison.Ordinal);
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(lineCount, snapshot.TotalMatchCount);
        Assert.Equal(lineCount, snapshot.MatchedLines.Length);
        Assert.All(snapshot.MatchedLines.Take(100), line => Assert.Null(line.OccurrenceCheckpoints));
        Assert.True(
            allocatedBytes < 96L * 1024 * 1024,
            $"Tab-heavy search engine allocated {allocatedBytes:N0} bytes.");
    }

    private sealed class CancelWhenReadList(
        VisibleLogSearchLine line,
        CancellationTokenSource cancellation) : IReadOnlyList<VisibleLogSearchLine>
    {
        public int Count => 1;

        public VisibleLogSearchLine this[int index]
        {
            get
            {
                Assert.Equal(0, index);
                cancellation.Cancel();
                return line;
            }
        }

        public IEnumerator<VisibleLogSearchLine> GetEnumerator()
        {
            yield return this[0];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
