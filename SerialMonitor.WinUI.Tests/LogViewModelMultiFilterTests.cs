using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogViewModelMultiFilterTests
{
    [Fact]
    public void RxOnlyFilter_CanLeaveViewEmptyDespiteTxAndRxUntilFilterIsCleared()
    {
        var viewModel = new LogViewModel(capacity: 100);
        var rule = CreateTerminalRule("ERROR");
        rule.MatchDirection = HighlightMatchDirection.RxOnly;
        viewModel.SetViewFilters([rule]);
        viewModel.AddRange([LogLine.Rx("ERROR before reconnect")]);
        viewModel.Clear();

        viewModel.AddRange([LogLine.Tx("ERROR command"), LogLine.Rx("OK")]);

        Assert.Equal(2, viewModel.TotalRetainedLineCount);
        Assert.Equal(0, viewModel.CurrentVisibleLineCount);
        Assert.Empty(viewModel.GetVisibleTextSnapshot());

        viewModel.SetViewFilters([], rebuildExisting: false);
        viewModel.AddRange([LogLine.Tx("new command"), LogLine.Rx("new reply")]);
        var visible = viewModel.GetVisibleTextSnapshot();
        Assert.Contains("new command", visible, StringComparison.Ordinal);
        Assert.Contains("new reply", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleViewFilters_ShowLinesMatchingAnySelectedRule()
    {
        var viewModel = new LogViewModel(capacity: 100);
        viewModel.SetViewFilters([CreateTerminalRule("ERROR"), CreateTerminalRule("WARN")]);

        viewModel.AddRange(
        [
            LogLine.Rx("ERROR first"),
            LogLine.Rx("WARN second"),
            LogLine.Rx("INFO hidden")
        ]);

        var snapshot = viewModel.GetVisibleTextSnapshot();
        Assert.Equal(2, viewModel.CurrentVisibleLineCount);
        Assert.Contains("ERROR first", snapshot, StringComparison.Ordinal);
        Assert.Contains("WARN second", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("INFO hidden", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyViewFilterSelection_ShowsAllLines()
    {
        var viewModel = new LogViewModel(capacity: 100);
        viewModel.SetViewFilters([]);

        viewModel.AddRange([LogLine.Rx("ERROR"), LogLine.Rx("INFO")]);

        Assert.Equal(2, viewModel.CurrentVisibleLineCount);
    }

    [Fact]
    public void ViewFilterChanges_RemainNewLogsOnly_AfterFormattingRebuild()
    {
        var viewModel = new LogViewModel(capacity: 100);
        viewModel.SetViewFilters([CreateTerminalRule("ERROR")], rebuildExisting: false);
        viewModel.AddRange([LogLine.Rx("OLD ERROR"), LogLine.Rx("OLD WARN")]);

        viewModel.SetViewFilters([CreateTerminalRule("WARN")], rebuildExisting: false);
        viewModel.AddRange([LogLine.Rx("NEW ERROR"), LogLine.Rx("NEW WARN")]);
        viewModel.SetTimestampDisplayFormat(TimestampDisplayFormat.TimeSeconds);

        var snapshot = viewModel.GetVisibleTextSnapshot();
        Assert.Contains("OLD ERROR", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("OLD WARN", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("NEW ERROR", snapshot, StringComparison.Ordinal);
        Assert.Contains("NEW WARN", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidFilter_DoesNotPreventAnotherSelectedFilterFromMatching()
    {
        var viewModel = new LogViewModel(capacity: 100);
        viewModel.SetRxDisplayMode(RxDisplayMode.Hex);
        viewModel.SetViewFilters(
        [
            CreateHexRule("GG"),
            CreateHexRule("DE AD")
        ]);

        viewModel.AddRange(
        [
            LogLine.Rx(
                "DE AD",
                new byte[] { 0xDE, 0xAD },
                contentMode: LogRuleMatchMode.Hex)
        ]);

        Assert.Equal(1, viewModel.CurrentVisibleLineCount);
    }

    private static HighlightRule CreateTerminalRule(string keyword) => new()
    {
        Name = keyword,
        Keyword = keyword,
        Enabled = true,
        Mode = LogRuleMatchMode.Terminal,
        MatchDirection = HighlightMatchDirection.Both,
        UseAsViewFilter = true
    };

    private static HighlightRule CreateHexRule(string keyword) => new()
    {
        Name = keyword,
        Keyword = keyword,
        Enabled = true,
        Mode = LogRuleMatchMode.Hex,
        MatchDirection = HighlightMatchDirection.Both,
        UseAsViewFilter = true
    };
}
