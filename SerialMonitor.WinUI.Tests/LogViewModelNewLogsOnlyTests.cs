using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogViewModelNewLogsOnlyTests
{
    [Fact]
    public void ViewFilterChange_RemainsNewLogsOnly_AfterFormattingRebuild()
    {
        var viewModel = new LogViewModel(capacity: 100);
        viewModel.AddRange(new[] { LogLine.Rx("OLD NONMATCH") });

        viewModel.SetViewFilter(CreateErrorRule(), rebuildExisting: false);
        viewModel.AddRange(new[]
        {
            LogLine.Rx("NEW NONMATCH"),
            LogLine.Rx("NEW ERROR")
        });

        viewModel.SetTimestampDisplayFormat(TimestampDisplayFormat.TimeSeconds);
        var snapshot = viewModel.GetVisibleTextSnapshot();

        Assert.Contains("OLD NONMATCH", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("NEW NONMATCH", snapshot, StringComparison.Ordinal);
        Assert.Contains("NEW ERROR", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void HighlightRuleChange_RemainsNewLogsOnly_AfterFormattingRebuild()
    {
        var viewModel = new LogViewModel(capacity: 100);
        viewModel.AddRange(new[] { LogLine.Rx("OLD ERROR") });

        viewModel.SetHighlightRules(new[] { CreateErrorRule() });
        viewModel.AddRange(new[] { LogLine.Rx("NEW ERROR") });

        viewModel.SetTimestampDisplayFormat(TimestampDisplayFormat.TimeSeconds);
        var snapshot = viewModel.GetVisibleTextSnapshot();

        Assert.Equal(1, CountOccurrences(snapshot, "\u001b[31m"));
        Assert.Contains("OLD ERROR", snapshot, StringComparison.Ordinal);
        Assert.Contains("NEW ERROR", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void HighlightRuleChange_RemainsNewLogsOnly_AfterRxModeRebuild()
    {
        var viewModel = new LogViewModel(capacity: 100);
        var line = LogLine.Rx("ERROR", "ERROR"u8.ToArray());
        viewModel.AddRange(new[] { line });
        viewModel.SetHighlightRules(new[]
        {
            new HighlightRule
            {
                Name = "HEX errors",
                Keyword = "45 52 52 4F 52",
                Enabled = true,
                Mode = LogRuleMatchMode.Hex,
                MatchDirection = HighlightMatchDirection.Both,
                ForegroundColor = "Red"
            }
        });
        viewModel.AddRange(new[] { line });

        viewModel.SetRxDisplayMode(RxDisplayMode.Hex);
        var snapshot = viewModel.GetVisibleTextSnapshot();

        Assert.Equal(1, CountOccurrences(snapshot, "\u001b[31m"));
        Assert.Equal(2, CountOccurrences(snapshot, "45 52 52 4F 52"));
    }

    [Fact]
    public void HighlightRuleChange_RemainsNewLogsOnly_AfterPartialTrimRebuild()
    {
        var viewModel = new LogViewModel(capacity: 3);
        viewModel.AddRange(new[]
        {
            LogLine.Rx("partial", isPartialRxSegment: true),
            LogLine.Rx("OLD ERROR")
        });
        viewModel.SetHighlightRules(new[] { CreateErrorRule() });
        viewModel.AddRange(new[] { LogLine.Rx("NEW ERROR") });

        viewModel.SetCapacity(2);
        var snapshot = viewModel.GetVisibleTextSnapshot();

        Assert.Equal(1, CountOccurrences(snapshot, "\u001b[31m"));
        Assert.Contains("OLD ERROR", snapshot, StringComparison.Ordinal);
        Assert.Contains("NEW ERROR", snapshot, StringComparison.Ordinal);
    }

    private static HighlightRule CreateErrorRule() => new()
    {
        Name = "Errors",
        Keyword = "ERROR",
        Enabled = true,
        Mode = LogRuleMatchMode.Terminal,
        MatchDirection = HighlightMatchDirection.Both,
        ForegroundColor = "Red",
        UseAsViewFilter = true
    };

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
