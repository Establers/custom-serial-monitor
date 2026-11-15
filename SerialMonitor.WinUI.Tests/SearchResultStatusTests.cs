using System.Reflection;
using System.Runtime.CompilerServices;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class SearchResultStatusTests
{
    [Fact]
    public async Task PageFailure_SurvivesIncomingLogsAndIsClearedBySuccessfulPageBuild()
    {
        var model = CreateSearchState(hasPreviousResults: true);
        var snapshot = VisibleLogSearchEngine.Build(
            [new VisibleLogSearchLine(1, "READY", 0)], "READY", new VisibleLogSearchOptions());
        SetField(model, "_activeSearchSnapshot", snapshot);
        SetField(model, "_lastSearchResultBuildError", "Search result build failed: page unavailable");

        Invoke(model, "MarkSearchResultsStale");
        Invoke(model, "NotifySearchResultPageProperties");
        Assert.Contains("page unavailable", model.SearchResultStatusText);
        Assert.DoesNotContain("Logs changed", model.SearchResultStatusText);

        var build = typeof(MainViewModel).GetMethod("UpdateSearchResultsPageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True(await (Task<bool>)build.Invoke(model, [snapshot, 0, 0L, CancellationToken.None, null])!);
        Assert.Equal(string.Empty, model.LastSearchResultBuildError);
        Assert.DoesNotContain("page unavailable", model.SearchResultStatusText);
        Assert.Contains("Logs changed", model.SearchResultStatusText);
    }

    [Fact]
    public void IncomingLogsAndEditedCriteria_HaveDifferentInstructionsAndKeepAppliedQuery()
    {
        var model = CreateSearchState(hasPreviousResults: true);
        var snapshot = VisibleLogSearchEngine.Build(
            [new VisibleLogSearchLine(1, "READY READY", 0)], "READY", new VisibleLogSearchOptions());
        SetField(model, "_activeSearchSnapshot", snapshot);
        SetField(model, "_searchMatchCount", 2L);

        Invoke(model, "MarkSearchResultsStale");
        Assert.Contains("Logs changed", model.SearchResultStatusText);
        Assert.Contains("1 lines", model.SearchResultCountsText);
        Assert.Contains("/2", model.SearchResultCountsText);

        model.SearchText = "ERROR";
        Assert.Contains("Conditions changed", model.SearchResultStatusText);
        Assert.Contains("READY", model.AppliedSearchCriteriaText);
        Assert.DoesNotContain("ERROR", model.AppliedSearchCriteriaText);
        Invoke(model, "MarkSearchResultsStale");
        Assert.Contains("Conditions changed", model.SearchResultStatusText);
    }

    [Fact]
    public void IncomingLogs_BeforeFirstSearch_DoNotClaimThereAreOldResults()
    {
        var model = CreateSearchState(hasPreviousResults: false);
        Invoke(model, "MarkSearchResultsStale");
        Assert.False(model.AreSearchResultsStale);
        Assert.Contains("Enter search text", model.SearchResultStatusText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncomingLogs_KeepSearchFailureVisible(bool hasPreviousResults)
    {
        var model = CreateSearchState(hasPreviousResults);
        const string error = "Search failed: Invalid pattern '['.";
        SetField(model, "_lastSearchError", error);
        SetField(model, "_searchResultStatusText", error);

        // Live RX marks the current snapshot stale after every render batch.
        Invoke(model, "MarkSearchResultsStale");
        Invoke(model, "MarkSearchResultsStale");

        Assert.True(model.AreSearchResultsStale);
        Assert.Contains("Invalid pattern '['.", model.SearchResultStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("Search failed: ", model.SearchResultStatusText, StringComparison.Ordinal);
        Assert.Equal(hasPreviousResults ? 1 : 0, model.SearchResults.Count);
    }

    [Fact]
    public void EditingCriteria_DismissesPreviousFailureAndRequiresRefresh()
    {
        var model = CreateSearchState(hasPreviousResults: true);
        SetField(model, "_lastSearchError", "Search failed: Invalid pattern '['.");

        model.SearchText = "READY";

        Assert.Equal(string.Empty, model.LastSearchError);
        Assert.True(model.AreSearchResultsStale);
        Assert.Contains("Refresh", model.SearchResultStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("Search failed", model.SearchResultStatusText, StringComparison.Ordinal);
        Assert.Single(model.SearchResults);
    }

    private static MainViewModel CreateSearchState(bool hasPreviousResults)
    {
        // These status transitions need no WinUI dispatcher or serial services.
        // Supply only their UI-bound search state; execute the production methods.
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        SetField(model, "_searchText", "[");
        SetField(model, "_currentUiSettings", new UiSettings());
        SetField(model, "_suppressSettingsApplyRecording", true);
        SetField(model, "<SearchResults>k__BackingField", new BulkObservableCollection<VisibleSearchResult>());
        foreach (var name in new[]
        {
            "FindNextCommand", "FindPreviousCommand", "SearchVisibleLogCommand",
            "RefreshSearchResultsCommand", "PreviousSearchResultsPageCommand", "NextSearchResultsPageCommand"
        })
        {
            SetField(model, $"<{name}>k__BackingField", new AsyncRelayCommand(() => Task.CompletedTask));
        }
        if (hasPreviousResults)
            model.SearchResults.Add(VisibleSearchResultParser.Create(1, 0, 1, 0, 1, "READY"));
        return model;
    }

    private static void SetField(MainViewModel model, string name, object value) =>
        typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, value);

    private static void Invoke(MainViewModel model, string name) =>
        typeof(MainViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null);
}
