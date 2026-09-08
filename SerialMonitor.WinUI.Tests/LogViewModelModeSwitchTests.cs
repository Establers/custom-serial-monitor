using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogViewModelModeSwitchTests
{
    [Theory]
    [InlineData(RxDisplayMode.Terminal, RxDisplayMode.Hex)]
    [InlineData(RxDisplayMode.Hex, RxDisplayMode.Terminal)]
    public void ModeChangeWithClear_DiscardsHistoryAndPartialLineWithoutRebuild(
        RxDisplayMode original, RxDisplayMode next)
    {
        var viewModel = new LogViewModel(100);
        viewModel.SetRxDisplayMode(original);
        viewModel.AddRange([LogLine.Rx("OLD", "OLD"u8.ToArray(), isPartialRxSegment: true)]);
        var cleared = 0;
        var rebuilt = 0;
        viewModel.TextCleared += (_, _) => cleared++;
        viewModel.TextRebuilt += (_, _) => rebuilt++;

        viewModel.SetRxDisplayMode(next, clearExisting: true);

        Assert.Equal(1, cleared);
        Assert.Equal(0, rebuilt);
        Assert.Equal(0, viewModel.TotalRetainedLineCount);
        Assert.Equal(0, viewModel.PartialRxVisualLength);
        Assert.Empty(viewModel.GetVisibleSearchContentSnapshot());
        Assert.Empty(viewModel.GetXtermTextSnapshot());
        viewModel.AddRange([LogLine.Rx("NEW", "NEW"u8.ToArray())]);
        Assert.Contains(next == RxDisplayMode.Hex ? "4E 45 57" : "NEW", viewModel.GetVisibleTextSnapshot());
        Assert.Equal(1, viewModel.TotalRetainedLineCount);
    }

    [Fact]
    public void SameModeWithClear_DoesNotEraseCurrentLogs()
    {
        var viewModel = new LogViewModel(100);
        viewModel.AddRange([LogLine.Rx("KEEP")]);

        viewModel.SetRxDisplayMode(RxDisplayMode.Terminal, clearExisting: true);

        Assert.Equal(1, viewModel.TotalRetainedLineCount);
        Assert.Contains("KEEP", viewModel.GetVisibleTextSnapshot());
    }

    [Fact]
    public async Task ModeChangeWithClear_PreservesQueuedFileLogsAndContinuesSameWriter()
    {
        using var stream = new MemoryStream();
        await using var writer = new SerialMonitor.WinUI.Services.FileLogWriter((_, _) => stream);
        await writer.StartAsync(Path.GetTempPath(), CancellationToken.None);
        var originalPath = writer.CurrentLogFilePath;
        var viewModel = new LogViewModel(100);
        var oldLine = LogLine.Rx("BEFORE", "BEFORE"u8.ToArray());
        viewModel.AddRange([oldLine]);
        Assert.True(writer.TryEnqueue(oldLine));

        viewModel.SetRxDisplayMode(RxDisplayMode.Hex, clearExisting: true);

        Assert.True(writer.IsRunning);
        Assert.Equal(originalPath, writer.CurrentLogFilePath);
        var nextLine = LogLine.Rx("A", [0x41], displayText: "41", contentMode: LogRuleMatchMode.Hex);
        Assert.True(writer.TryEnqueue(nextLine));
        viewModel.AddRange([nextLine]);
        await writer.StopAsync(CancellationToken.None);
        var fileText = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("RX < BEFORE", fileText);
        Assert.Contains("RX < 41", fileText);
        Assert.DoesNotContain("BEFORE", viewModel.GetVisibleTextSnapshot());
        Assert.Equal(2, writer.DurableLineCount);
        Assert.Equal(1, writer.StartCount);
    }

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
