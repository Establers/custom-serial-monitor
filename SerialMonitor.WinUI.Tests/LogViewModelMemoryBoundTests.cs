using System.Text;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

[Collection(StabilityIsolationCollection.Name)]
public sealed class LogViewModelMemoryBoundTests
{
    [Fact]
    public void Repeated64KiBSegments_StayWithinRetainedBudgetAndPartialVisualCap()
    {
        const long retainedBudget = 4L * 1024 * 1024;
        var viewModel = new LogViewModel(
            capacity: 100_000,
            retainedMemoryBudgetBytes: retainedBudget,
            maximumPartialRxVisualCharacters: LogViewModel.DefaultMaximumPartialRxVisualCharacters);
        viewModel.SetRxDisplayMode(RxDisplayMode.Hex);
        var rawBytes = Enumerable.Range(0, 64 * 1024)
            .Select(index => (byte)index)
            .ToArray();
        var decoded = Encoding.Latin1.GetString(rawBytes);

        for (var index = 0; index < 96; index++)
        {
            viewModel.AddRange(
            [
                new LogLine(
                    DateTimeOffset.UnixEpoch.AddMilliseconds(index),
                    LogDirection.Rx,
                    decoded,
                    rawBytes,
                    sequenceNumber: index,
                    isPartialRxSegment: true,
                    contentMode: LogRuleMatchMode.Hex)
            ]);

            Assert.InRange(viewModel.RetainedMemoryCostBytes, 0, retainedBudget);
            Assert.InRange(
                viewModel.PartialRxVisualLength,
                0,
                viewModel.MaximumPartialRxVisualCharacters);
        }

        Assert.True(viewModel.ForcedPartialVisualBoundaryCount > 0);
        Assert.True(viewModel.DroppedVisibleLineCount > 0);
        Assert.True(viewModel.TotalRetainedLineCount < 96);
        Assert.All(
            viewModel.GetVisibleSearchLinesSnapshot(),
            line => Assert.InRange(
                line.Length,
                0,
                viewModel.MaximumPartialRxVisualCharacters));
        Assert.InRange(viewModel.RetainedMemoryCostBytes, 0, retainedBudget);
    }

    [Fact]
    public void SourceCost_CountsSharedPayloadOnceAndDistinctDisplayTextSeparately()
    {
        const string shared = "shared payload";
        var distinctDisplay = new string(shared.ToCharArray());
        var rawBytes = new byte[] { 1, 2, 3, 4 };
        var viewModel = new LogViewModel(capacity: 10, retainedMemoryBudgetBytes: 1024 * 1024);

        viewModel.AddRange(
        [
            new LogLine(DateTimeOffset.UnixEpoch, LogDirection.Rx, shared, rawBytes),
            new LogLine(
                DateTimeOffset.UnixEpoch,
                LogDirection.Rx,
                shared,
                rawBytes,
                displayText: distinctDisplay)
        ]);

        var fixedAndRawCost = (2 * 256L) + (2 * rawBytes.Length);
        var expectedTextCost = (3L * shared.Length * sizeof(char));
        Assert.Equal(fixedAndRawCost + expectedTextCost, viewModel.RetainedSourceCostBytes);
        Assert.True(viewModel.RetainedVisualCostBytes > 0);
        Assert.Equal(
            viewModel.RetainedSourceCostBytes + viewModel.RetainedVisualCostBytes,
            viewModel.RetainedMemoryCostBytes);
    }

    [Fact]
    public void RebuildCapacityAndClear_KeepCostAccountingExactAndNonNegative()
    {
        var viewModel = new LogViewModel(
            capacity: 20,
            retainedMemoryBudgetBytes: 512 * 1024,
            maximumPartialRxVisualCharacters: 256 * 1024);
        viewModel.AddRange(
        [
            LogLine.System("system"),
            new LogLine(
                DateTimeOffset.UnixEpoch,
                LogDirection.Rx,
                "partial",
                "partial"u8.ToArray(),
                isPartialRxSegment: true),
            LogLine.RxPartialTerminator(),
            LogLine.Tx("tx")
        ]);

        AssertAccounting(viewModel);
        viewModel.SetShowTimestampInLogView(false);
        AssertAccounting(viewModel);
        viewModel.SetShowRxTxDirectionPrefixInLogView(false);
        AssertAccounting(viewModel);
        viewModel.SetTimestampDisplayFormat(TimestampDisplayFormat.TimeSeconds);
        AssertAccounting(viewModel);
        viewModel.SetRxDisplayMode(RxDisplayMode.Hex);
        AssertAccounting(viewModel);
        viewModel.SetViewFilters(
        [
            new HighlightRule
            {
                Enabled = true,
                Keyword = "partial",
                Mode = LogRuleMatchMode.Terminal
            }
        ]);
        AssertAccounting(viewModel);

        viewModel.SetCapacity(1);
        Assert.InRange(viewModel.TotalRetainedLineCount, 0, 1);
        AssertAccounting(viewModel);

        viewModel.Clear();
        Assert.Equal(0, viewModel.RetainedSourceCostBytes);
        Assert.Equal(0, viewModel.RetainedVisualCostBytes);
        Assert.Equal(0, viewModel.RetainedMemoryCostBytes);
        Assert.Equal(0, viewModel.PartialRxVisualLength);
    }

    [Fact]
    public void TinyInjectedPartialLimit_AlsoCapsBoundaryMarker()
    {
        var viewModel = new LogViewModel(
            capacity: 10,
            retainedMemoryBudgetBytes: 1024 * 1024,
            maximumPartialRxVisualCharacters: 8);

        viewModel.AddRange(
        [
            new LogLine(
                DateTimeOffset.UnixEpoch,
                LogDirection.Rx,
                new string('x', 64),
                isPartialRxSegment: true)
        ]);

        Assert.InRange(viewModel.PartialRxVisualLength, 0, 8);
        Assert.All(viewModel.GetVisibleSearchLinesSnapshot(), line => Assert.InRange(line.Length, 0, 8));
        Assert.InRange(viewModel.RetainedMemoryCostBytes, 0, viewModel.RetainedMemoryBudgetBytes);
    }

    [Fact]
    public void HiddenLineEvictsVisiblePrefix_EmitsTrimOnlyVisualMutation()
    {
        var viewModel = new LogViewModel(
            capacity: 1,
            retainedMemoryBudgetBytes: 1024 * 1024);
        viewModel.AddRange([LogLine.System("visible before filter")]);
        viewModel.SetViewFilters(
        [
            new HighlightRule
            {
                Enabled = true,
                Keyword = "KEEP",
                Mode = LogRuleMatchMode.Terminal,
                UseAsViewFilter = true
            }
        ], rebuildExisting: false);

        LogTextBatch? mutation = null;
        viewModel.TextBatchAppended += (_, batch) => mutation = batch;
        viewModel.AddRange([LogLine.System("hidden and evicts visible")]);

        Assert.NotNull(mutation);
        Assert.Equal(string.Empty, mutation.AppendedText);
        Assert.Equal(0, mutation.LineCount);
        Assert.True(mutation.TrimCharacterCount > 0);
        Assert.Equal(viewModel.DisplayedLineCount, mutation.EndDisplayedLineCount);
        Assert.Equal(0, viewModel.CurrentVisibleLineCount);
        Assert.Equal(string.Empty, viewModel.GetXtermTextSnapshot());
        AssertAccounting(viewModel);
    }

    private static void AssertAccounting(LogViewModel viewModel)
    {
        Assert.InRange(
            viewModel.RetainedMemoryCostBytes,
            0,
            viewModel.RetainedMemoryBudgetBytes);
        Assert.InRange(viewModel.RetainedSourceCostBytes, 0, viewModel.RetainedMemoryCostBytes);
        Assert.InRange(viewModel.RetainedVisualCostBytes, 0, viewModel.RetainedMemoryCostBytes);
        Assert.Equal(
            viewModel.RetainedSourceCostBytes + viewModel.RetainedVisualCostBytes,
            viewModel.RetainedMemoryCostBytes);
        Assert.InRange(
            viewModel.PartialRxVisualLength,
            0,
            viewModel.MaximumPartialRxVisualCharacters);
    }
}
