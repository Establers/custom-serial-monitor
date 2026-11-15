using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    public string SearchResultCountsText => _activeSearchSnapshot is null
        ? UiText.Get("SearchScope", "Retained logs · manual refresh")
        : UiText.Format("SearchResultCounts", "{0:N0} lines · match {2:N0}/{1:N0} · retained logs",
            SearchResultMatchedLineCount, SearchMatchCount, CurrentSearchMatchIndex);

    public string SearchResultPageText => SearchResultPageCount == 0
        ? string.Empty
        : UiText.Format("SearchResultPage", "Page {0:N0}/{1:N0}",
            SearchResultPageNumber, SearchResultPageCount);

    private void UpdateSearchResultStatus() => SearchResultStatusText = GetSearchResultStatus();

    private string GetSearchResultStatus()
    {
        if (_isSearchRunning) return UiText.Get("SearchStateRunning", "Searching…");
        if (!string.IsNullOrWhiteSpace(_lastSearchError))
            return UiText.Format("SearchStateFailed", "Search failed — {0}",
                FailureDetail(_lastSearchError, "Search failed: "));
        if (!string.IsNullOrWhiteSpace(_lastSearchResultBuildError))
            return UiText.Format("SearchPageStateFailed", "Could not display results — {0}",
                FailureDetail(_lastSearchResultBuildError, "Search result build failed: "));
        if (_activeSearchSnapshot is null && SearchResults.Count == 0)
            return UiText.Get("SearchStateInitial", "Enter search text, then search.");
        if (_areSearchCriteriaStale)
            return UiText.Get("SearchStateCriteriaChanged", "Conditions changed — search again or Refresh.");
        if (AreSearchResultsStale)
            return UiText.Get("SearchStateLogsChanged", "Logs changed — Refresh to include the latest logs.");
        return SearchMatchCount == 0
            ? UiText.Get("SearchStateNoMatches", "No matches. Change the search conditions.")
            : UiText.Get("SearchStateCurrent", "Manual refresh · Enter on a result opens its source log.");
    }

    private static string FailureDetail(string diagnostic, string prefix) =>
        diagnostic.StartsWith(prefix, StringComparison.Ordinal) ? diagnostic[prefix.Length..] : diagnostic;

    private void ClearSearchResultBuildError()
    {
        if (string.IsNullOrEmpty(_lastSearchResultBuildError)) return;
        _lastSearchResultBuildError = string.Empty;
        OnPropertyChanged(nameof(LastSearchResultBuildError));
    }
}
