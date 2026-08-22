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
    public void XtermSearchRequest_PreservesPayloadRelativeTarget()
    {
        var request = new XtermSearchRequest(
            1,
            "rx_mq",
            false,
            "next",
            targetLineId: 42,
            targetPayloadOffset: 17,
            matchLength: 5,
            expectedText: "RX_MQ",
            occurrenceInLine: 3,
            tabsBeforeMatch: 2,
            tabsBeforeMatchEnd: 3);

        Assert.Equal(42, request.TargetLineId);
        Assert.Equal(17, request.TargetPayloadOffset);
        Assert.Equal(5, request.MatchLength);
        Assert.Equal("RX_MQ", request.ExpectedText);
        Assert.Equal(3, request.OccurrenceInLine);
        Assert.Equal(2, request.TabsBeforeMatch);
        Assert.Equal(3, request.TabsBeforeMatchEnd);
    }

    [Fact]
    public void XtermSearchRequest_PreservesPayloadStartForPrefixFreeDisplay()
    {
        var request = new XtermSearchRequest(
            1,
            "READY",
            false,
            "next",
            targetLineId: 42,
            targetPayloadOffset: 0,
            targetPayloadStart: 23,
            matchLength: 5,
            expectedText: "READY");

        Assert.Equal(23, request.TargetPayloadStart);
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
    public void DirectionPrefixSetting_RebuildsRetainedRxAndTxWithoutChangingPayloadOrDirection()
    {
        var localTime = new DateTime(2026, 7, 28, 20, 30, 0, DateTimeKind.Unspecified);
        var timestamp = new DateTimeOffset(localTime, TimeZoneInfo.Local.GetUtcOffset(localTime));
        var viewModel = new LogViewModel(100);
        viewModel.AddRange(
        [
            new LogLine(timestamp, LogDirection.Rx, "READY"),
            new LogLine(timestamp, LogDirection.Tx, "PING"),
            new LogLine(timestamp, LogDirection.Mark, "checkpoint"),
            new LogLine(timestamp, LogDirection.System, "connected")
        ]);

        viewModel.SetShowRxTxDirectionPrefixInLogView(false);

        var lines = viewModel.GetVisibleSearchContentSnapshot();
        Assert.Equal("[2026-07-28 20:30:00.000] READY", lines[0].FullText);
        Assert.Equal("[2026-07-28 20:30:00.000] PING", lines[1].FullText);
        Assert.Equal("[2026-07-28 20:30:00.000] MARK > checkpoint", lines[2].FullText);
        Assert.Equal("[2026-07-28 20:30:00.000] SYS connected", lines[3].FullText);
        Assert.Equal("READY", lines[0].PayloadText);
        Assert.Equal("PING", lines[1].PayloadText);
        Assert.Equal(LogDirection.Rx, lines[0].Direction);
        Assert.Equal(LogDirection.Tx, lines[1].Direction);
        Assert.DoesNotContain("RX <", viewModel.GetVisibleTextSnapshot(), StringComparison.Ordinal);
        Assert.DoesNotContain("TX >", viewModel.GetVisibleTextSnapshot(), StringComparison.Ordinal);
    }

    [Fact]
    public void DirectionPrefixSetting_WithTimestampHidden_LeavesOnlyRxAndTxPayload()
    {
        var viewModel = new LogViewModel(100);
        viewModel.SetShowTimestampInLogView(false);
        viewModel.SetShowRxTxDirectionPrefixInLogView(false);
        viewModel.AddRange([LogLine.Rx("READY"), LogLine.Tx("PING")]);

        var lines = viewModel.GetVisibleSearchContentSnapshot();
        Assert.Equal("READY", lines[0].FullText);
        Assert.Equal("PING", lines[1].FullText);
        Assert.Equal(0, lines[0].PayloadStart);
        Assert.Equal(0, lines[1].PayloadStart);
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
        var firstTimestamp = new DateTimeOffset(2026, 8, 2, 12, 34, 56, 789, TimeSpan.Zero);
        viewModel.AddRange(
        [
            new LogLine(firstTimestamp, LogDirection.Rx, "first"),
            new LogLine(firstTimestamp.AddSeconds(1.423), LogDirection.Tx, "second")
        ]);
        var before = viewModel.GetVisibleSearchContentSnapshot();

        viewModel.SetTimestampDisplayFormat(TimestampDisplayFormat.TimeSeconds);

        var after = viewModel.GetVisibleSearchContentSnapshot();
        Assert.Equal(before.Select(line => line.LineId), after.Select(line => line.LineId));
        Assert.Equal(2, after.Select(line => line.LineId).Distinct().Count());
        Assert.DoesNotContain("\u001b]777;", viewModel.GetVisibleTextSnapshot(), StringComparison.Ordinal);
        Assert.Contains(
            $"\u001b]777;{after[0].LineId},{firstTimestamp.ToUnixTimeMilliseconds()}\u0007",
            viewModel.GetXtermTextSnapshot(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void XtermTimestampMetadata_RemainsAvailableWhenVisibleTimestampIsHidden()
    {
        var timestamp = new DateTimeOffset(2026, 8, 2, 12, 34, 56, 789, TimeSpan.Zero);
        var viewModel = new LogViewModel(100);
        viewModel.SetShowTimestampInLogView(false);
        viewModel.AddRange([new LogLine(timestamp, LogDirection.Rx, "READY")]);

        var line = Assert.Single(viewModel.GetVisibleSearchContentSnapshot());
        var xtermText = viewModel.GetXtermTextSnapshot();

        Assert.DoesNotContain("2026-08-02", viewModel.GetVisibleTextSnapshot(), StringComparison.Ordinal);
        Assert.Contains(
            $"\u001b]777;{line.LineId},{timestamp.ToUnixTimeMilliseconds()}\u0007",
            xtermText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void XtermTimestampMetadata_StaysAlignedAfterCapacityTrim()
    {
        var firstTimestamp = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var viewModel = new LogViewModel(2);
        viewModel.AddRange(
        [
            new LogLine(firstTimestamp, LogDirection.Rx, "first"),
            new LogLine(firstTimestamp.AddSeconds(1), LogDirection.Rx, "second"),
            new LogLine(firstTimestamp.AddSeconds(2), LogDirection.Rx, "third")
        ]);

        var lines = viewModel.GetVisibleSearchContentSnapshot();
        var xtermText = viewModel.GetXtermTextSnapshot();

        Assert.Equal(2, lines.Count);
        Assert.DoesNotContain(
            $"\u001b]777;1,{firstTimestamp.ToUnixTimeMilliseconds()}\u0007",
            xtermText,
            StringComparison.Ordinal);
        Assert.Contains(
            $"\u001b]777;{lines[0].LineId},{firstTimestamp.AddSeconds(1).ToUnixTimeMilliseconds()}\u0007",
            xtermText,
            StringComparison.Ordinal);
        Assert.Contains(
            $"\u001b]777;{lines[1].LineId},{firstTimestamp.AddSeconds(2).ToUnixTimeMilliseconds()}\u0007",
            xtermText,
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
