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
                Mode = LogRuleMatchMode.Hex,
                ForegroundColor = "Red"
            }
        });
        viewModel.AddRange(new[]
        {
            LogLine.Rx(
                "ERROR",
                "ERROR"u8.ToArray(),
                contentMode: LogRuleMatchMode.Terminal)
        });

        Assert.DoesNotContain("\u001b[31m", viewModel.GetVisibleTextSnapshot(), StringComparison.Ordinal);

        viewModel.SetRxDisplayMode(RxDisplayMode.Hex);

        var hexSnapshot = viewModel.GetVisibleTextSnapshot();
        Assert.Contains("45 52 52 4F 52", hexSnapshot, StringComparison.Ordinal);
        Assert.Contains("\u001b[31m", hexSnapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void TxLine_AlsoUsesCurrentAppModeForHighlightRules()
    {
        var viewModel = new LogViewModel(capacity: 100);
        viewModel.SetHighlightRules(new[]
        {
            new HighlightRule
            {
                Enabled = true,
                Keyword = "ERROR",
                Mode = LogRuleMatchMode.Terminal,
                ForegroundColor = "Red"
            }
        });
        viewModel.AddRange(new[]
        {
            LogLine.Tx("ERROR", "ERROR"u8.ToArray(), contentMode: LogRuleMatchMode.Terminal)
        });

        Assert.Contains("\u001b[31m", viewModel.GetVisibleTextSnapshot(), StringComparison.Ordinal);

        viewModel.SetRxDisplayMode(RxDisplayMode.Hex);

        Assert.DoesNotContain("\u001b[31m", viewModel.GetVisibleTextSnapshot(), StringComparison.Ordinal);
    }
}
