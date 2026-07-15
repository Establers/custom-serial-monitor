using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class TerminalBracketHighlightTests
{
    private const string AliceBlue = "\u001b[38;2;240;248;255m";
    private const string Cyan = "\u001b[36m";
    private const string Reset = "\u001b[0m";

    [Fact]
    public void TerminalMode_ColorsCompleteBracketGroupsInPayloadOnly()
    {
        var viewModel = new LogViewModel(capacity: 100);
        viewModel.AddRange(new[] { LogLine.Rx("plain [INFO] message") });

        var snapshot = viewModel.GetVisibleTextSnapshot();

        Assert.StartsWith("[", snapshot, StringComparison.Ordinal);
        Assert.Contains($"RX < plain {AliceBlue}[INFO]{Reset} message", snapshot, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(snapshot, AliceBlue));
    }

    [Fact]
    public void TerminalMode_DoesNotColorGeneratedTimestampOrIncompleteGroup()
    {
        var viewModel = new LogViewModel(capacity: 100);
        viewModel.AddRange(new[] { LogLine.Rx("plain [INFO message") });

        Assert.DoesNotContain(AliceBlue, viewModel.GetVisibleTextSnapshot(), StringComparison.Ordinal);
    }

    [Fact]
    public void HexMode_DoesNotApplyAutomaticBracketColorToTxText()
    {
        var viewModel = new LogViewModel(capacity: 100);
        viewModel.SetRxDisplayMode(RxDisplayMode.Hex);
        viewModel.AddRange(new[] { LogLine.Tx("[INFO]") });

        Assert.DoesNotContain(AliceBlue, viewModel.GetVisibleTextSnapshot(), StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalTx_RestoresCyanAfterBracketGroup()
    {
        var viewModel = new LogViewModel(capacity: 100);
        viewModel.SetShowTimestampInLogView(false);
        viewModel.AddRange(new[] { LogLine.Tx("before [TAG] after") });

        Assert.Equal(
            $"{Cyan}TX > before {AliceBlue}[TAG]{Reset}{Cyan} after{Reset}{Environment.NewLine}",
            viewModel.GetVisibleTextSnapshot());
    }

    [Fact]
    public void ExplicitHighlightRule_TakesPriorityOverAutomaticBracketColor()
    {
        var viewModel = new LogViewModel(capacity: 100);
        viewModel.SetShowTimestampInLogView(false);
        viewModel.SetHighlightRules(new[]
        {
            new HighlightRule
            {
                Keyword = "ERROR",
                Mode = LogRuleMatchMode.Terminal,
                ForegroundColor = "Red"
            }
        });
        viewModel.AddRange(new[] { LogLine.Rx("[TAG] ERROR") });

        var snapshot = viewModel.GetVisibleTextSnapshot();
        Assert.Contains($"\u001b[31mRX < [TAG] ERROR{Reset}", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain(AliceBlue, snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialRx_ColorsGroupsWithinEachSegmentWithoutRebuildingHistory()
    {
        var viewModel = new LogViewModel(capacity: 100);
        viewModel.SetShowTimestampInLogView(false);
        viewModel.AddRange(new[]
        {
            LogLine.Rx("[A]", isPartialRxSegment: true),
            LogLine.Rx(" [B]", isPartialRxSegment: true),
            LogLine.RxPartialTerminator()
        });

        var snapshot = viewModel.GetVisibleTextSnapshot();
        Assert.Contains($"{AliceBlue}[A]{Reset} {AliceBlue}[B]{Reset}", snapshot, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(snapshot, AliceBlue));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
