namespace SerialMonitor.WinUI.Infrastructure;

internal static class SearchResultFreshnessPolicy
{
    public static bool AfterRendering(bool isCurrentlyStale, bool snapshotIsFresh) =>
        snapshotIsFresh ? false : isCurrentlyStale;
}
