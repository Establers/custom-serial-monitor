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
        VisibleLogSearchOptions options)
    {
        var labels = new List<string>
        {
            options.MatchCase
                ? UiText.Get("SearchOptionCase", "Case sensitive")
                : UiText.Get("SearchOptionIgnoreCase", "Ignore case"),
            options.UseRegularExpression
                ? UiText.Get("SearchOptionRegex", "Regex")
                : UiText.Get("SearchOptionLiteral", "Literal")
        };
        if (options.MatchWholeWord) labels.Add(UiText.Get("SearchOptionWholeWord", "Whole word"));
        var displayText = searchText.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        return $"“{displayText}” · {string.Join(" · ", labels)}";
    }

    public static bool CanCommit(
        long expectedSearchGeneration,
        long currentSearchGeneration,
        bool cancellationRequested) =>
        !cancellationRequested && expectedSearchGeneration == currentSearchGeneration;

    public static bool ShouldRequestXterm(VisibleSearchApplyResult result) =>
        result == VisibleSearchApplyResult.Applied;

}
