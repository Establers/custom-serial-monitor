using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogViewModelModeSwitchTests
{
    [Fact]
    public void RetainedRxLine_UsesCurrentViewModeForHighlightRules()
    {
        var viewModel = new LogViewModel(capacity: 100);
        viewModel.SetHighlightRules(new[]
        {
            new HighlightRule
            {
                Enabled = true,
                Keyword = "45 52 52 4F 52",
                MatchMode = LogRuleMatchMode.Hex,
                ForegroundColor = "Red"
            }
        });
        viewModel.AddRange(new[]
        {
            LogLine.Rx(
                "ERROR",
                "ERROR"u8.ToArray(),
                contentMode: LogRuleMatchMode.Text)
        });

        Assert.DoesNotContain("\u001b[31m", viewModel.GetVisibleTextSnapshot(), StringComparison.Ordinal);

        viewModel.SetRxDisplayMode(RxDisplayMode.Hex);

        var hexSnapshot = viewModel.GetVisibleTextSnapshot();
        Assert.Contains("45 52 52 4F 52", hexSnapshot, StringComparison.Ordinal);
        Assert.Contains("\u001b[31m", hexSnapshot, StringComparison.Ordinal);
    }
}
