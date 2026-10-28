using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class VisibleSearchNavigationTests
{
    private const int PageSize = 1_000;

    [Fact]
    public void InitialPrevious_SelectsLastMatchingLineAndItsPage()
    {
        var snapshot = BuildLines(1_001);

        var position = VisibleSearchNavigation.ResolveSearchPosition(snapshot, -1, null, null);

        Assert.True(position.HasValue);
        Assert.Equal(1_000, position.Value.MatchedLineIndex);
        Assert.Equal(1_000, position.Value.GlobalMatchIndex);
        var page = VisibleSearchNavigation.GetPageForPosition(snapshot, position.Value, PageSize);
        Assert.Equal(new VisibleLogSearchPage(1, 2, 1_000, 1), page);
    }

    [Theory]
    [InlineData(999, 1, 1_000, 1)]
    [InlineData(1_000, -1, 999, 0)]
    [InlineData(1_000, 1, 0, 0)]
    [InlineData(0, -1, 1_000, 1)]
    public void Navigate_PageBoundaryAndWrap_SelectPageContainingMatch(
        int initialLineIndex,
        int movement,
        int expectedLineIndex,
        int expectedPageIndex)
    {
        var snapshot = BuildLines(1_001);
        Assert.True(snapshot.TryFind(initialLineIndex + 1, 0, out var initial));

        var position = VisibleSearchNavigation.ResolveNavigationPosition(snapshot, initial, movement);

        Assert.True(position.HasValue);
        Assert.Equal(expectedLineIndex, position.Value.MatchedLineIndex);
        Assert.Equal(expectedPageIndex,
            VisibleSearchNavigation.GetPageForPosition(snapshot, position.Value, PageSize).PageIndex);
    }

    [Fact]
    public void MultipleMatchesInOneLine_PageUsesMatchingLineIndexInsteadOfMatchIndex()
    {
        var lines = Enumerable.Range(1, 1_001)
            .Select(index => new VisibleLogSearchLine(index, index == 1_000 ? "hit hit hit" : "hit", 0))
            .ToArray();
        var snapshot = VisibleLogSearchEngine.Build(lines, "hit", new VisibleLogSearchOptions());
        Assert.True(snapshot.TryFind(1_000, 0, out var firstInLine));

        var secondInLine = VisibleSearchNavigation.ResolveNavigationPosition(snapshot, firstInLine, 1);
        Assert.True(secondInLine.HasValue);
        Assert.Equal(1_000, secondInLine.Value.GlobalMatchIndex);
        Assert.Equal(0, VisibleSearchNavigation.GetPageForPosition(snapshot, secondInLine.Value, PageSize).PageIndex);

        var nextLine = VisibleSearchNavigation.ResolveNavigationPosition(snapshot, secondInLine, 2);
        Assert.True(nextLine.HasValue);
        Assert.Equal(1_000, nextLine.Value.MatchedLineIndex);
        Assert.Equal(1, VisibleSearchNavigation.GetPageForPosition(snapshot, nextLine.Value, PageSize).PageIndex);

        var previousInLine = VisibleSearchNavigation.ResolveNavigationPosition(snapshot, nextLine, -1);
        Assert.True(previousInLine.HasValue);
        Assert.Equal(2, previousInLine.Value.OccurrenceInLine);
        Assert.Equal(0, VisibleSearchNavigation.GetPageForPosition(snapshot, previousInLine.Value, PageSize).PageIndex);
    }

    [Theory]
    [InlineData(int.MaxValue, 3)]
    [InlineData(int.MinValue, 3)]
    [InlineData(0, 1)]
    public void LargeMovement_WrapsWithoutIntegerOverflow(int movement, long expectedMatchIndex)
    {
        var snapshot = BuildLines(5);
        Assert.True(snapshot.TryFind(2, 0, out var initial));

        var position = VisibleSearchNavigation.ResolveNavigationPosition(snapshot, initial, movement);

        Assert.True(position.HasValue);
        Assert.Equal(expectedMatchIndex, position.Value.GlobalMatchIndex);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(-1, 4)]
    [InlineData(2, 1)]
    [InlineData(-2, 3)]
    public void NavigationWithoutCurrentMatch_CountsInitialSelectionAsOneMovement(
        int movement,
        long expectedMatchIndex)
    {
        var snapshot = BuildLines(5);

        var position = VisibleSearchNavigation.ResolveNavigationPosition(snapshot, null, movement);

        Assert.True(position.HasValue);
        Assert.Equal(expectedMatchIndex, position.Value.GlobalMatchIndex);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 3)]
    public void RefreshExistingMatch_RestoresOrMovesRelativeToItsStableLineId(
        int movement,
        long expectedMatchIndex)
    {
        var snapshot = BuildLines(5);

        var position = VisibleSearchNavigation.ResolveSearchPosition(snapshot, movement, 3, 0);

        Assert.True(position.HasValue);
        Assert.Equal(expectedMatchIndex, position.Value.GlobalMatchIndex);
    }

    [Fact]
    public void TrimmedPreviousMatch_FallsBackToFirstOrLastAvailableMatch()
    {
        var snapshot = VisibleLogSearchEngine.Build(
            [new VisibleLogSearchLine(10, "hit", 0), new VisibleLogSearchLine(11, "hit", 0)],
            "hit",
            new VisibleLogSearchOptions());

        var next = VisibleSearchNavigation.ResolveSearchPosition(snapshot, 1, 1, 0);
        var previous = VisibleSearchNavigation.ResolveSearchPosition(snapshot, -1, 1, 0);

        Assert.True(next.HasValue);
        Assert.True(previous.HasValue);
        Assert.Equal(10, snapshot.GetLine(next.Value).LineId);
        Assert.Equal(11, snapshot.GetLine(previous.Value).LineId);
    }

    [Fact]
    public void EmptySnapshot_HasNoNavigationOrSearchPosition()
    {
        var snapshot = VisibleLogSearchEngine.Build([], "hit", new VisibleLogSearchOptions());

        Assert.Null(VisibleSearchNavigation.ResolveNavigationPosition(snapshot, null, int.MinValue));
        Assert.Null(VisibleSearchNavigation.ResolveSearchPosition(snapshot, -1, null, null));
    }

    [Fact]
    public void CanceledNavigation_DoesNotChangeSelection()
    {
        var snapshot = BuildLines(5);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            VisibleSearchNavigation.ResolveNavigationPosition(snapshot, null, 1, cancellation.Token));
    }

    private static VisibleLogSearchSnapshot BuildLines(int count) =>
        VisibleLogSearchEngine.Build(
            Enumerable.Range(1, count)
                .Select(index => new VisibleLogSearchLine(index, "hit", 0))
                .ToArray(),
            "hit",
            new VisibleLogSearchOptions());
}
