namespace SerialMonitor.WinUI.Infrastructure;

internal enum VisibleSearchApplyResult
{
    Applied,
    Canceled,
    Failed
}

internal static class VisibleSearchApplyPolicy
{
    public static int GetRequestedPageIndex(
        bool searchCriteriaWereStale,
        bool showLatestResultsPage,
        int currentPageIndex)
    {
        if (showLatestResultsPage)
        {
            return int.MaxValue;
        }

        return searchCriteriaWereStale ? 0 : currentPageIndex;
    }

    public static string FormatAppliedCriteria(
        string searchText,
        VisibleLogSearchOptions options) =>
        $"Applied: {searchText} · Aa:{FormatOption(options.MatchCase)} · Word:{FormatOption(options.MatchWholeWord)} · Regex:{FormatOption(options.UseRegularExpression)}";

    public static bool CanCommit(
        long expectedSearchGeneration,
        long currentSearchGeneration,
        bool cancellationRequested) =>
        !cancellationRequested && expectedSearchGeneration == currentSearchGeneration;

    public static bool ShouldRequestXterm(VisibleSearchApplyResult result) =>
        result == VisibleSearchApplyResult.Applied;

    private static string FormatOption(bool enabled) => enabled ? "On" : "Off";
}
