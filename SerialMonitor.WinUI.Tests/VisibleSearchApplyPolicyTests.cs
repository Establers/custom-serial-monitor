using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class VisibleSearchApplyPolicyTests
{
    [Fact]
    public void ChangedCriteria_StartsFreshSearchOnFirstPage()
    {
        var pageIndex = VisibleSearchApplyPolicy.GetRequestedPageIndex(
            searchCriteriaWereStale: true,
            showLatestResultsPage: false,
            currentPageIndex: 9);

        Assert.Equal(0, pageIndex);
    }

    [Fact]
    public void LatestPageRequest_OverridesChangedCriteriaAndUsesLastPage()
    {
        var pageIndex = VisibleSearchApplyPolicy.GetRequestedPageIndex(
            searchCriteriaWereStale: true,
            showLatestResultsPage: true,
            currentPageIndex: 9);

        Assert.Equal(int.MaxValue, pageIndex);
    }

    [Fact]
    public void UnchangedCriteria_PreservesCurrentPage()
    {
        var pageIndex = VisibleSearchApplyPolicy.GetRequestedPageIndex(
            searchCriteriaWereStale: false,
            showLatestResultsPage: false,
            currentPageIndex: 9);

        Assert.Equal(9, pageIndex);
    }

    [Fact]
    public void AppliedCriteria_IdentifiesSnapshotTextAndEveryOption()
    {
        var text = VisibleSearchApplyPolicy.FormatAppliedCriteria(
            "old search",
            new VisibleLogSearchOptions(
                MatchCase: true,
                MatchWholeWord: false,
                UseRegularExpression: true));

        Assert.Equal(
            "Applied: old search · Aa:On · Word:Off · Regex:On",
            text);
    }

    [Fact]
    public void MatchingGenerationWithoutCancellation_CanCommitAtomically()
    {
        Assert.True(VisibleSearchApplyPolicy.CanCommit(
            expectedSearchGeneration: 7,
            currentSearchGeneration: 7,
            cancellationRequested: false));
    }

    [Fact]
    public void InputChangeOrCancellation_CannotCommitPendingResults()
    {
        Assert.False(VisibleSearchApplyPolicy.CanCommit(
            expectedSearchGeneration: 7,
            currentSearchGeneration: 8,
            cancellationRequested: false));
        Assert.False(VisibleSearchApplyPolicy.CanCommit(
            expectedSearchGeneration: 7,
            currentSearchGeneration: 7,
            cancellationRequested: true));
    }

    [Fact]
    public void CanceledOrFailedSearch_DoesNotRequestXterm()
    {
        Assert.False(VisibleSearchApplyPolicy.ShouldRequestXterm(
            VisibleSearchApplyResult.Canceled));
        Assert.False(VisibleSearchApplyPolicy.ShouldRequestXterm(
            VisibleSearchApplyResult.Failed));
    }

    [Fact]
    public void AppliedSearch_RequestsXterm()
    {
        Assert.True(VisibleSearchApplyPolicy.ShouldRequestXterm(
            VisibleSearchApplyResult.Applied));
    }
}
