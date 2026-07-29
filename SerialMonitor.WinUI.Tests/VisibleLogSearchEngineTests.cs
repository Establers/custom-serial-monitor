using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class VisibleLogSearchEngineTests
{
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
            StringComparison.Ordinal,
            maxVisibleResults: 1000);

        Assert.Equal(2, snapshot.TotalMatchCount);
        Assert.Single(snapshot.MatchedLines);
        Assert.Equal(2, snapshot.MatchedLines[0].MatchCount);
        Assert.Equal(2, snapshot.VisibleResults.Length);
        Assert.Equal(5, snapshot.VisibleResults[0].PayloadOffset);
        Assert.True(snapshot.VisibleResults[1].PayloadOffset > snapshot.VisibleResults[0].PayloadOffset);
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
            StringComparison.Ordinal,
            maxVisibleResults: 1000);

        var result = Assert.Single(snapshot.VisibleResults);
        Assert.Equal(1, snapshot.TotalMatchCount);
        Assert.True(result.PayloadOffset > 5);
    }

    [Fact]
    public void Build_ConsecutiveTabsRemainSeparateOccurrencesAndCoordinates()
    {
        var snapshot = VisibleLogSearchEngine.Build(
            [new VisibleLogSearchLine(1, "RX < \t\tREADY", 5)],
            "\t",
            StringComparison.Ordinal,
            maxVisibleResults: 1000);

        Assert.Equal(2, snapshot.TotalMatchCount);
        Assert.Equal([0, 1], snapshot.VisibleResults.Select(result => result.PayloadOffset));
        var line = Assert.Single(snapshot.MatchedLines);
        Assert.Null(line.OccurrenceCheckpoints);
        Assert.Equal(0, snapshot.VisibleResults[0].TabsBeforeMatch);
        Assert.Equal(1, snapshot.VisibleResults[0].TabsBeforeMatchEnd);
        Assert.Equal(1, snapshot.VisibleResults[1].TabsBeforeMatch);
        Assert.Equal(2, snapshot.VisibleResults[1].TabsBeforeMatchEnd);
    }

    [Fact]
    public void Build_OrdinalIgnoreCaseDoesNotUseJavaScriptUnicodeCaseFolding()
    {
        var snapshot = VisibleLogSearchEngine.Build(
            [new VisibleLogSearchLine(1, "RX < \tſ S K K ß ẞ", 5)],
            "S",
            StringComparison.OrdinalIgnoreCase,
            maxVisibleResults: 1000);

        var result = Assert.Single(snapshot.VisibleResults);
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
            StringComparison.Ordinal,
            maxVisibleResults: 1000);

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
            StringComparison.Ordinal,
            maxVisibleResults: 1000);

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
            StringComparison.Ordinal,
            maxVisibleResults: 1000);

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
    public void Build_DenseMatchesKeepOnlyVisibleResultLimit()
    {
        var lines = Enumerable.Range(0, 20_000)
            .Select(index => new VisibleLogSearchLine(index + 1, "RX < a a a a a a a a a a", 5))
            .ToArray();

        var snapshot = VisibleLogSearchEngine.Build(
            lines,
            "a",
            StringComparison.Ordinal,
            maxVisibleResults: 1000);

        Assert.Equal(200_000, snapshot.TotalMatchCount);
        Assert.Equal(20_000, snapshot.MatchedLines.Length);
        Assert.Equal(1000, snapshot.VisibleResults.Length);
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
                maxVisibleResults: 1000,
                cancellation.Token));
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
                maxVisibleResults: 1000,
                cancellation.Token));
    }

    [Fact]
    public void Build_FindsMatchAcrossCancellationSearchWindowBoundary()
    {
        var prefix = new string('x', (64 * 1024) - 2);
        var snapshot = VisibleLogSearchEngine.Build(
            [new VisibleLogSearchLine(1, $"RX < {prefix}READY", 5)],
            "READY",
            StringComparison.Ordinal,
            maxVisibleResults: 1000);

        var result = Assert.Single(snapshot.VisibleResults);
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
            StringComparison.Ordinal,
            maxVisibleResults: 1000);
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(400_000, snapshot.TotalMatchCount);
        Assert.Equal(lineCount, snapshot.MatchedLines.Length);
        Assert.Equal(1000, snapshot.VisibleResults.Length);
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
            StringComparison.Ordinal,
            maxVisibleResults: 1000);
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
