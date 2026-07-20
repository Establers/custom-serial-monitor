using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogViewModelSearchContentTests
{
    [Fact]
    public void XtermSearchRequest_PreservesTargetLineId()
    {
        var request = new XtermSearchRequest(1, "READY", false, "next", 0, 42);

        Assert.Equal(42, request.TargetLineId);
    }

    [Fact]
    public void SearchContent_ExcludesTimestampAndDirectionMetadata()
    {
        var viewModel = new LogViewModel(100);
        viewModel.AddRange(
        [
            new LogLine(
                new DateTimeOffset(2026, 7, 20, 14, 35, 12, 345, TimeSpan.Zero),
                LogDirection.Rx,
                "device RX < payload"),
            new LogLine(
                new DateTimeOffset(2026, 7, 20, 14, 35, 13, 456, TimeSpan.Zero),
                LogDirection.Tx,
                "status")
        ]);

        var lines = viewModel.GetVisibleSearchContentSnapshot();

        Assert.Equal(2, lines.Count);
        Assert.Equal("device RX < payload", lines[0].PayloadText);
        Assert.Equal("status", lines[1].PayloadText);
        Assert.DoesNotContain("2026-07-20", lines[0].PayloadText, StringComparison.Ordinal);
        Assert.False(lines[0].PayloadText.StartsWith("RX <", StringComparison.Ordinal));
        Assert.Contains("RX <", lines[0].PayloadText, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchContent_ExcludesDirectionMetadata_WhenTimestampIsHidden()
    {
        var viewModel = new LogViewModel(100);
        viewModel.SetShowTimestampInLogView(false);
        viewModel.AddRange([LogLine.Rx("READY")]);

        var line = Assert.Single(viewModel.GetVisibleSearchContentSnapshot());

        Assert.Equal("RX < READY", line.FullText);
        Assert.Equal("READY", line.PayloadText);
    }

    [Fact]
    public void SearchContent_PreservesDirectionLikeTextInsidePayload()
    {
        var viewModel = new LogViewModel(100);
        viewModel.AddRange([LogLine.Rx("RX < is part of the device data")]);

        var line = Assert.Single(viewModel.GetVisibleSearchContentSnapshot());

        Assert.Equal("RX < is part of the device data", line.PayloadText);
    }

    [Fact]
    public void SearchContent_LineIdsRemainStableAcrossFormattingRebuild()
    {
        var viewModel = new LogViewModel(100);
        viewModel.AddRange([LogLine.Rx("first"), LogLine.Tx("second")]);
        var before = viewModel.GetVisibleSearchContentSnapshot();

        viewModel.SetTimestampDisplayFormat(TimestampDisplayFormat.TimeSeconds);

        var after = viewModel.GetVisibleSearchContentSnapshot();
        Assert.Equal(before.Select(line => line.LineId), after.Select(line => line.LineId));
        Assert.Equal(2, after.Select(line => line.LineId).Distinct().Count());
        Assert.DoesNotContain("\u001b]777;", viewModel.GetVisibleTextSnapshot(), StringComparison.Ordinal);
        Assert.Contains(
            $"\u001b]777;{after[0].LineId}\u0007",
            viewModel.GetXtermTextSnapshot(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SearchContent_PartialRxKeepsFirstSegmentLineIdAcrossRebuild()
    {
        var viewModel = new LogViewModel(100);
        viewModel.AddRange(
        [
            LogLine.Rx("first", isPartialRxSegment: true),
            LogLine.Rx(" second", isPartialRxSegment: true),
            LogLine.RxPartialTerminator()
        ]);
        var before = Assert.Single(viewModel.GetVisibleSearchContentSnapshot());

        viewModel.SetTimestampDisplayFormat(TimestampDisplayFormat.TimeSeconds);

        var after = Assert.Single(viewModel.GetVisibleSearchContentSnapshot());
        Assert.Equal(before.LineId, after.LineId);
    }
}
