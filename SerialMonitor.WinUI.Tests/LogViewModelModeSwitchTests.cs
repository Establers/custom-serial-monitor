using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogViewModelModeSwitchTests
{
    [Fact]
    public void SystemLine_IsAlwaysRenderedInGray()
    {
        var viewModel = new LogViewModel(capacity: 100);
        viewModel.AddRange(new[] { LogLine.System("VIEW RESUMED - PS 12") });

        var snapshot = viewModel.GetVisibleTextSnapshot();

        Assert.Contains("\u001b[90m", snapshot, StringComparison.Ordinal);
        Assert.Contains("VIEW RESUMED - PS 12", snapshot, StringComparison.Ordinal);
        Assert.Contains("\u001b[0m", snapshot, StringComparison.Ordinal);
    }

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

    [Fact]
    public void CompletedHexGroupTrim_DoesNotReformatRetainedBuffer()
    {
        var viewModel = new LogViewModel(capacity: 2);
        viewModel.SetRxDisplayMode(RxDisplayMode.Hex);
        viewModel.SetHighlightRules(new[]
        {
            new HighlightRule
            {
                Enabled = true,
                Keyword = "AA",
                Mode = LogRuleMatchMode.Hex,
                ForegroundColor = "invalid"
            }
        });
        var group = new[]
        {
            LogLine.Rx("", new byte[] { 0xAA }, isPartialRxSegment: true),
            LogLine.RxPartialTerminator()
        };

        viewModel.AddRange(group);
        viewModel.AddRange(group);

        Assert.Equal(2, viewModel.XtermFormattingErrorCount);
        Assert.Equal(1, viewModel.CurrentVisibleLineCount);
    }

    [Fact]
    public void PartialHexGroupTrim_DropsThroughTerminatorWithoutReformattingRetainedBuffer()
    {
        var viewModel = new LogViewModel(capacity: 3);
        viewModel.SetRxDisplayMode(RxDisplayMode.Hex);
        viewModel.SetHighlightRules(new[]
        {
            new HighlightRule
            {
                Enabled = true,
                Keyword = "AA",
                Mode = LogRuleMatchMode.Hex,
                ForegroundColor = "invalid"
            }
        });

        viewModel.AddRange(new[]
        {
            LogLine.Rx("", new byte[] { 0xAA }, isPartialRxSegment: true),
            LogLine.Rx("", new byte[] { 0xBB }, isPartialRxSegment: true),
            LogLine.RxPartialTerminator()
        });
        viewModel.AddRange(new[]
        {
            LogLine.Rx("", new byte[] { 0xAA }, isPartialRxSegment: true),
            LogLine.RxPartialTerminator()
        });

        Assert.Equal(2, viewModel.XtermFormattingErrorCount);
        Assert.Equal(2, viewModel.TotalRetainedLineCount);
        Assert.Equal(1, viewModel.CurrentVisibleLineCount);
    }

    [Fact]
    public void PartialTrim_AcrossHiddenNormalLine_KeepsRetainedContinuationVisible()
    {
        var viewModel = new LogViewModel(capacity: 2);
        viewModel.SetViewFilter(new HighlightRule
        {
            Enabled = true,
            Keyword = "KEEP",
            Mode = LogRuleMatchMode.Terminal,
            UseAsViewFilter = true
        });

        viewModel.AddRange(new[]
        {
            LogLine.Rx("KEEP-A", isPartialRxSegment: true),
            LogLine.Rx("HIDDEN"),
            LogLine.RxPartialTerminator(),
            LogLine.Rx("KEEP-B", isPartialRxSegment: true),
            LogLine.RxPartialTerminator()
        });

        var snapshot = viewModel.GetVisibleTextSnapshot();
        Assert.DoesNotContain("KEEP-A", snapshot, StringComparison.Ordinal);
        Assert.Contains("KEEP-B", snapshot, StringComparison.Ordinal);
    }
}
