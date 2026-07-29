using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class SearchResultFreshnessPolicyTests
{
    [Fact]
    public void ExistingSnapshotPageRender_PreservesStaleState()
    {
        Assert.True(SearchResultFreshnessPolicy.AfterRendering(
            isCurrentlyStale: true,
            snapshotIsFresh: false));
    }

    [Fact]
    public void FreshSnapshotRender_ClearsStaleState()
    {
        Assert.False(SearchResultFreshnessPolicy.AfterRendering(
            isCurrentlyStale: true,
            snapshotIsFresh: true));
    }
}
