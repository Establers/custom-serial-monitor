using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Services;
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SearchContent_MetadataLikePayloadIsNeverRemoved(bool showTimestamp, bool showDirection)
    {
        string[] payloads =
        [
            "SYS status", "SYSTEM ready", "RX < data", "TX > data", "MARK > data",
            "[16:30:55.091] data", "[device] data", "  READY", "\tREADY"
        ];
        var viewModel = new LogViewModel(100);
        viewModel.SetShowTimestampInLogView(showTimestamp);
        viewModel.SetShowRxTxDirectionPrefixInLogView(showDirection);
        viewModel.AddRange(payloads.Select(payload => LogLine.Rx(payload)).ToArray());

        var lines = viewModel.GetVisibleSearchContentSnapshot();
        for (var index = 0; index < payloads.Length; index++)
        {
            Assert.Equal(payloads[index], lines[index].PayloadText);
            Assert.Equal(lines[index].FullText.Length - payloads[index].Length, lines[index].PayloadStart);
            var snapshot = VisibleLogSearchEngine.Build(
                [lines[index]],
                "^" + System.Text.RegularExpressions.Regex.Escape(payloads[index]) + "$",
                new VisibleLogSearchOptions(UseRegularExpression: true));
            Assert.Equal(1, snapshot.TotalMatchCount);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SearchContent_PartialRxPreservesPayloadBoundaryAcrossRebuild(bool showTimestamp, bool showDirection)
    {
        var viewModel = new LogViewModel(100);
        viewModel.SetShowTimestampInLogView(showTimestamp);
        viewModel.SetShowRxTxDirectionPrefixInLogView(showDirection);
        viewModel.AddRange(
        [
            LogLine.Rx("SYS", isPartialRxSegment: true),
            LogLine.Rx(" READY", isPartialRxSegment: true)
        ]);

        var before = Assert.Single(viewModel.GetVisibleSearchContentSnapshot());
        Assert.Equal("SYS READY", before.PayloadText);
        viewModel.SetShowRxTxDirectionPrefixInLogView(!showDirection);
        var after = Assert.Single(viewModel.GetVisibleSearchContentSnapshot());
        Assert.Equal(before.LineId, after.LineId);
        Assert.Equal("SYS READY", after.PayloadText);
    }

    [Fact]
    public void TryGetSearchLine_UsesSourceIdentityAndRejectsFilteredOrExpiredLogs()
    {
        var visible = LogLine.Rx("KEEP first");
        var hidden = LogLine.Rx("hidden");
        var viewModel = new LogViewModel(2);
        viewModel.SetViewFilter(new HighlightRule { Keyword = "KEEP" });
        viewModel.AddRange([visible, hidden]);

        var expected = Assert.Single(viewModel.GetVisibleSearchContentSnapshot());
        Assert.True(viewModel.TryGetSearchLine(visible, out var found));
        Assert.Equal(expected, found);
        Assert.False(viewModel.TryGetSearchLine(LogLine.Rx("KEEP first"), out _));
        Assert.False(viewModel.TryGetSearchLine(hidden, out _));

        viewModel.AddRange([LogLine.Rx("KEEP newest")]);
        Assert.False(viewModel.TryGetSearchLine(visible, out _));
    }

    [Fact]
    public void TryGetSearchLine_ResolvesMergedPartialSourceAndRejectsFilteredSegments()
    {
        var first = LogLine.Rx("KEEP first", isPartialRxSegment: true);
        var hidden = LogLine.Rx("hidden", isPartialRxSegment: true);
        var second = LogLine.Rx(" KEEP second", isPartialRxSegment: true);
        var viewModel = new LogViewModel(100);
        viewModel.SetViewFilter(new HighlightRule { Keyword = "KEEP" });
        viewModel.AddRange([first, hidden, second]);

        Assert.True(viewModel.TryGetSearchLine(second, out var found));
        Assert.True(viewModel.TryGetSearchLine(first, out var firstLine));
        Assert.Equal(firstLine.LineId, found.LineId);
        Assert.Equal("KEEP first KEEP second", found.PayloadText);
        Assert.False(viewModel.TryGetSearchLine(hidden, out _));
        viewModel.SetCapacity(1);
        Assert.False(viewModel.TryGetSearchLine(first, out _));
        Assert.False(viewModel.TryGetSearchLine(second, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryGetSearchLine_ResolvesActualStreamingEventSource(bool secondIsPartial)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var first = new LogLine(timestamp, LogDirection.Rx, "FA", sequenceNumber: 1, isPartialRxSegment: true);
        var second = new LogLine(timestamp.AddMilliseconds(1), LogDirection.Rx, "ULT",
            sequenceNumber: 2, isPartialRxSegment: secondIsPartial);
        var viewModel = new LogViewModel(100);
        viewModel.AddRange([first, second]);
        var detectedEvent = await DetectStreamingEventAsync(first, second);
        var source = Assert.IsType<LogLine>(detectedEvent.SourceLogLine);
        Assert.NotSame(second, source);
        Assert.Equal("FAULT", source.Text);

        Assert.True(viewModel.TryGetSearchLine(second, out var expected));
        Assert.True(viewModel.TryGetSearchLine(source, out var found));
        Assert.Equal(expected, found);
        Assert.Equal("FAULT", found.PayloadText);
    }

    [Fact]
    public async Task TryGetSearchLine_StreamingSourceCannotBypassOriginalFragmentFilter()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var first = new LogLine(timestamp, LogDirection.Rx, "FA", sequenceNumber: 1, isPartialRxSegment: true);
        var hidden = new LogLine(timestamp.AddMilliseconds(1), LogDirection.Rx, "ULT",
            sequenceNumber: 2, isPartialRxSegment: true);
        var viewModel = new LogViewModel(100);
        viewModel.SetViewFilter(new HighlightRule { Keyword = "FA" });
        viewModel.AddRange([first, hidden]);
        var detectedEvent = await DetectStreamingEventAsync(first, hidden);

        Assert.True(viewModel.TryGetSearchLine(first, out _));
        Assert.False(viewModel.TryGetSearchLine(detectedEvent.SourceLogLine!, out _));
    }

    [Fact]
    public void TryGetSearchLine_SequenceFallbackRequiresEveryIdentityFieldAndNeverGuessesByText()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var original = new LogLine(timestamp, LogDirection.Rx, "same text", sequenceNumber: 42);
        var viewModel = new LogViewModel(100);
        viewModel.AddRange([original]);

        Assert.True(viewModel.TryGetSearchLine(
            new LogLine(timestamp, LogDirection.Rx, "tail plus same text", sequenceNumber: 42), out _));
        Assert.False(viewModel.TryGetSearchLine(
            new LogLine(timestamp, LogDirection.Rx, "same text"), out _));
        Assert.False(viewModel.TryGetSearchLine(
            new LogLine(timestamp, LogDirection.Rx, "same text", sequenceNumber: 43), out _));
        Assert.False(viewModel.TryGetSearchLine(
            new LogLine(timestamp.AddTicks(1), LogDirection.Rx, "same text", sequenceNumber: 42), out _));
        Assert.False(viewModel.TryGetSearchLine(
            new LogLine(timestamp, LogDirection.Tx, "same text", sequenceNumber: 42), out _));
        Assert.False(viewModel.TryGetSearchLine(
            new LogLine(timestamp, LogDirection.Rx, "same text", sequenceNumber: 42,
                contentMode: LogRuleMatchMode.Hex), out _));
    }

    private static async Task<DetectedEvent> DetectStreamingEventAsync(params LogLine[] lines)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var detector = new EventDetector();
        await detector.StartAsync([new EventRule { Keyword = "FAULT" }], new EventContextSettings(), timeout.Token);
        foreach (var line in lines)
        {
            Assert.True(detector.TryEnqueue(line));
        }
        await detector.StopAsync(timeout.Token);
        Assert.Equal(1, detector.DetectedEventCount);
        Assert.True(detector.DetectedEvents.TryRead(out var detected));
        return Assert.IsType<DetectedEvent>(detected);
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
