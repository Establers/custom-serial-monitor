using System.Reflection;
using System.Runtime.CompilerServices;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class SearchResultStatusTests
{
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
        Assert.Contains(error, model.SearchResultStatusText, StringComparison.Ordinal);
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
