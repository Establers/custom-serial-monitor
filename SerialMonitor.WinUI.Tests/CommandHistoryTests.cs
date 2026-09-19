using System.Text.Json;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class CommandHistoryTests
{
    [Fact]
    public void RepeatedCommandKeepsOriginalEntryAndPosition()
    {
        var model = new CommandViewModel();
        var timestamp = DateTimeOffset.Now.AddMinutes(-1);
        model.AddToHistory("status", timestamp);
        var original = model.CommandHistory[0];
        model.AddToHistory("reset");
        model.AddToHistory("status");

        Assert.Equal(new[] { "reset", "status" }, model.CommandHistory.Select(entry => entry.CommandText));
        Assert.Same(original, model.CommandHistory[1]);
        Assert.Equal(timestamp, original.LastSentTime);
    }

    [Fact]
    public void SearchFiltersImmediatelyAndDoesNotChangeStoredHistory()
    {
        var model = new CommandViewModel();
        model.AddToHistory("get STATUS");
        model.AddToHistory("reset");
        model.HistorySearchText = "stat";
        Assert.Equal("get STATUS", Assert.Single(model.FilteredCommandHistory).CommandText);
        model.AddToHistory("status all");
        Assert.Equal(2, model.FilteredCommandHistory.Count);
        model.HistorySearchText = "missing";
        Assert.Empty(model.FilteredCommandHistory);
        Assert.Equal(3, model.GetHistorySnapshot().Count);
        model.HistorySearchText = "";
        Assert.Equal(3, model.FilteredCommandHistory.Count);
        model.ClearHistory();
        Assert.Empty(model.FilteredCommandHistory);
    }

    [Fact]
    public void LegacyCountsAreIgnoredAndLoadedCommandsAreUnique()
    {
        var history = JsonSerializer.Deserialize<CommandHistoryEntry[]>("""
            [{"CommandText":"status","Count":6},
             {"CommandText":"status","Count":3},
             {"CommandText":"STATUS","Count":1}]
            """);
        var model = new CommandViewModel();
        model.LoadHistory(history);
        Assert.Equal(2, model.CommandHistoryCount);
        Assert.DoesNotContain("Count", JsonSerializer.Serialize(model.GetHistorySnapshot()));
    }
}
