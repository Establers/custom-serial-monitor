using System.Text.Json;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class CommandHistoryTests
{
    [Fact]
    public void RepeatedCommandBecomesNewestAndUpdatesTimestamp()
    {
        var model = new CommandViewModel();
        var timestamp = DateTimeOffset.Now.AddMinutes(-1);
        model.AddToHistory("status", timestamp);
        var resentAt = timestamp.AddMinutes(1);
        model.AddToHistory("reset", timestamp.AddSeconds(30));
        model.AddToHistory(" status ", resentAt);

        Assert.Equal(new[] { "status", "reset" }, model.CommandHistory.Select(entry => entry.CommandText));
        Assert.Equal(resentAt, model.CommandHistory[0].LastSentTime);
        Assert.Equal("status", model.FilteredCommandHistory[0].CommandText);

        var restored = new CommandViewModel();
        restored.LoadHistory(model.GetHistorySnapshot());
        Assert.True(restored.NavigateHistory(-1));
        Assert.Equal("status", restored.CurrentCommandText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResendRecallsLatestCommandFirstAndDownRestoresDraft(bool selectFromHistory)
    {
        var model = new CommandViewModel();
        model.AddToHistory("status");
        model.AddToHistory("reset");
        Assert.True(model.NavigateHistory(-1));
        if (selectFromHistory)
        {
            model.HistorySearchText = "stat";
            model.SelectHistoryEntry(Assert.Single(model.FilteredCommandHistory));
        }
        else
        {
            model.CurrentCommandText = "status";
        }

        model.AddToHistory(model.CurrentCommandText);
        model.CurrentCommandText = string.Empty;

        Assert.True(model.NavigateHistory(-1));
        Assert.Equal("status", model.CurrentCommandText);
        Assert.True(model.NavigateHistory(-1));
        Assert.Equal("reset", model.CurrentCommandText);
        Assert.True(model.NavigateHistory(1));
        Assert.Equal("status", model.CurrentCommandText);
        Assert.True(model.NavigateHistory(1));
        Assert.Equal(string.Empty, model.CurrentCommandText);
    }

    [Fact]
    public void RepeatedNewestCommandUpdatesTimeWithoutGrowingHistory()
    {
        var model = new CommandViewModel();
        var timestamp = DateTimeOffset.Now.AddMinutes(-1);
        model.AddToHistory("status", timestamp);
        model.AddToHistory("status", timestamp.AddMinutes(1));

        Assert.Equal(timestamp.AddMinutes(1), Assert.Single(model.CommandHistory).LastSentTime);
        Assert.Equal(timestamp.AddMinutes(1), Assert.Single(model.FilteredCommandHistory).LastSentTime);
    }

    [Fact]
    public void ResentOldestCommandSurvivesHistoryCapacityEviction()
    {
        var model = new CommandViewModel();
        for (var i = 0; i < CommandViewModel.DefaultMaxHistoryCount; i++)
        {
            model.AddToHistory($"command {i}");
        }

        model.AddToHistory("command 0");
        model.AddToHistory("new command");

        Assert.Equal(CommandViewModel.DefaultMaxHistoryCount, model.CommandHistoryCount);
        Assert.Equal("command 0", model.CommandHistory[1].CommandText);
        Assert.DoesNotContain(model.CommandHistory, entry => entry.CommandText == "command 1");
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
