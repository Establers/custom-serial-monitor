using System.Diagnostics;
using System.Threading.Channels;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class HexStreamingRuleTests
{
    private const string Pattern = "DE AD BE EF";
    private static HighlightRule Rule(string pattern = Pattern, int priority = 0, string color = "Red") => new()
    { Keyword = pattern, Mode = LogRuleMatchMode.Hex, ForegroundColor = color, Priority = priority, UseAsViewFilter = true };
    private static LogLine Rx(byte[] bytes, long sequence, bool partial = true) =>
        LogLine.Rx("decoded", bytes, sequence, partial, displayText: HexFormatter.Format(bytes), contentMode: LogRuleMatchMode.Hex);
    private static LogViewModel View(int capacity = 100, LogViewMemoryPolicy? policy = null)
    {
        var log = policy is null ? new LogViewModel(capacity) : new LogViewModel(capacity, policy);
        log.SetRxDisplayMode(RxDisplayMode.Hex);
        log.SetShowTimestampInLogView(false);
        log.SetShowRxTxDirectionPrefixInLogView(false);
        return log;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SplitPattern_AgreesAcrossEventHighlightAndWholeLineFilter(bool finalTerminator)
    {
        var lines = new[] { Rx([0x10, 0xDE], 1), Rx([0xAD, 0xBE], 2), Rx([0xEF, 0x20], 3),
            Rx([0x30], 4, finalTerminator) };
        var log = View();
        log.SetHighlightRules([Rule()]);
        log.SetViewFilter(Rule());
        await using var detector = new EventDetector();
        await detector.StartAsync([new EventRule { Keyword = Pattern, Mode = LogRuleMatchMode.Hex, TriggerSequenceName = "Recover" }], new(), default);
        detector.UpdateRuleMode(LogRuleMatchMode.Hex);
        for (var i = 0; i < lines.Length; i++)
        {
            log.AddRange([lines[i]]);
            Assert.True(detector.TryEnqueue(lines[i]));
            if (i < 2) Assert.Empty(log.GetVisibleSearchContentSnapshot());
        }
        if (finalTerminator)
        {
            log.AddRange([LogLine.RxStreamBoundary(5)]);
            Assert.True(detector.TryEnqueue(LogLine.RxStreamBoundary(5)));
        }
        await detector.StopAsync(default);
        Assert.Equal(1, detector.DetectedEventCount);
        Assert.True(detector.DetectedEvents.TryRead(out var detected));
        Assert.Contains(Pattern, detected.Message);
        Assert.True(detector.SequenceTriggerEvents.TryRead(out _));
        Assert.False(detector.SequenceTriggerEvents.TryRead(out _));
        const string expected = "10 DE AD BE EF 20 30";
        Assert.Equal(expected, Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        Assert.True(log.TryGetSearchLine(detected.SourceLine!, out var found));
        Assert.Equal(expected, found.PayloadText);
        var before = log.GetVisibleTextSnapshot();
        Assert.Contains("\u001b[31m", before);
        log.RefreshVisibleFormatting();
        Assert.Equal(before, log.GetVisibleTextSnapshot());
        await log.SetViewFiltersAsync([Rule()]);
        Assert.Equal(before, log.GetVisibleTextSnapshot());
        log.SetViewFilters([]);
        Assert.Equal(expected, Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
    }

    [Fact]
    public void LaterMatch_RecolorsTheWholeHexLine_AndKeepsHighestPriority()
    {
        var log = View();
        var appended = "";
        log.TextBatchAppended += (_, batch) => appended = batch.AppendedText;
        log.SetHighlightRules([Rule("10", -1, "Yellow"), Rule()]);
        log.AddRange([Rx([0x10, 0xDE, 0xAD], 1)]);
        Assert.Contains("\u001b[33m", log.GetVisibleTextSnapshot());
        log.AddRange([Rx([0xBE, 0xEF], 2), Rx([0x20], 3), LogLine.RxStreamBoundary(4)]);
        var text = log.GetVisibleTextSnapshot();
        Assert.Contains("\u001b[31m", text);
        Assert.DoesNotContain("\u001b[33m", text);
        Assert.Contains("\u001b]778;", appended);
        log.RefreshVisibleFormatting();
        Assert.Equal(text, log.GetVisibleTextSnapshot());
    }

    [Fact]
    public async Task WholeLineFilter_ResetsAtNextGroup_AndMatchesRetainedHistoryWhenAppliedLater()
    {
        var lines = new[] { Rx([0x10, 0xDE, 0xAD], 1), Rx([0xBE, 0xEF], 2), Rx([0x20], 3), LogLine.RxStreamBoundary(4),
            Rx([0x30, 0x40], 5), LogLine.RxStreamBoundary(6) };
        var live = View();
        live.SetViewFilter(Rule());
        live.AddRange(lines);
        var replay = View();
        replay.AddRange(lines);
        await replay.SetViewFiltersAsync([Rule()]);
        Assert.Equal("10 DE AD BE EF 20", Assert.Single(live.GetVisibleSearchContentSnapshot()).PayloadText);
        Assert.Equal(live.GetVisibleTextSnapshot(), replay.GetVisibleTextSnapshot());
        live.Clear();
        live.AddRange([Rx([0xBE, 0xEF], 7)]);
        Assert.Empty(live.GetVisibleSearchContentSnapshot());
    }

    [Fact]
    public void HiddenHexPrefixBuffer_ObeysScreenLimits()
    {
        var context = new HexFilterLineContext();
        var rules = new object();
        for (var i = 1; i <= 1000; i++)
        {
            Assert.False(context.Evaluate(Rx(new byte[32], i), i, i - 1, 1, rules, i == 1, false, null, 2, 50).Visible);
            Assert.InRange(context.RetainedByteCount, 0, 50);
        }
        var shown = context.Evaluate(Rx([1], 1001), 1001, 1000, 1, rules, false, true, null, 2, 50);
        Assert.True(shown.Visible);
        Assert.InRange(shown.Prefix.Length, 0, 50);
        Assert.Equal(0, context.RetainedByteCount);
    }

    [Fact]
    public async Task MatchingLine_RemainsFilteredAfterItsEarlyFragmentsExpire_WithoutLeakingToOtherFilters()
    {
        var log = View(4, new LogViewMemoryPolicy(ContinuousLineCharacters: 10));
        log.SetViewFilter(Rule());
        log.AddRange([Rx([0xDE, 0xAD], 1), Rx([0xBE, 0xEF], 2), Rx([0x10], 3), Rx([0x20], 4),
            Rx([0x30], 5), Rx([0x40], 6)]);
        Assert.DoesNotContain(log.GetRetainedLinesSnapshot(), line => line.SequenceNumber <= 2);
        var before = log.GetVisibleTextSnapshot();
        Assert.NotEmpty(before);
        log.RefreshVisibleFormatting();
        Assert.Equal(before, log.GetVisibleTextSnapshot());
        await log.SetViewFiltersAsync([Rule()]);
        Assert.Equal(before, log.GetVisibleTextSnapshot());
        await log.SetViewFiltersAsync([Rule("99 99")]);
        Assert.Empty(log.GetVisibleSearchContentSnapshot());
    }

    [Theory]
    [InlineData("newline")]
    [InlineData("boundary")]
    [InlineData("gap")]
    [InlineData("mode")]
    [InlineData("rules")]
    public void RealBoundaries_DoNotJoinSeparateHexLines(string boundary)
    {
        var matcher = new StreamingEventRuleMatcher();
        var rule = LogRuleMatcher.Compile(new EventRule { Keyword = Pattern, Mode = LogRuleMatchMode.Hex });
        var highlight = new StreamingHighlightRuleMatcher();
        var rules = new[] { LogRuleMatcher.Compile(Rule()) };
        var first = Rx([0xDE, 0xAD], 1, boundary != "newline");
        matcher.BeginLine(first, rule, LogRuleMatchMode.Hex);
        Assert.False(matcher.IsMatch(first, rule, LogRuleMatchMode.Hex, out _, out _));
        Assert.Null(highlight.Evaluate(first, rules, LogRuleMatchMode.Hex).Rule);
        if (boundary == "boundary")
        {
            matcher.BeginLine(LogLine.RxStreamBoundary(2), rule, LogRuleMatchMode.Hex);
            highlight.Evaluate(LogLine.RxStreamBoundary(2), rules, LogRuleMatchMode.Hex);
        }
        if (boundary == "rules")
        {
            rule = LogRuleMatcher.Compile(new EventRule { Keyword = Pattern, Mode = LogRuleMatchMode.Hex });
            rules = [LogRuleMatcher.Compile(Rule())];
        }
        var last = boundary == "mode" ? LogLine.Rx("", [0xBE, 0xEF], 2, true) : Rx([0xBE, 0xEF], boundary is "gap" or "boundary" ? 3 : 2);
        matcher.BeginLine(last, rule, LogRuleMatchMode.Hex);
        Assert.False(matcher.IsMatch(last, rule, LogRuleMatchMode.Hex, out _, out _));
        Assert.Null(highlight.Evaluate(last, rules, LogRuleMatchMode.Hex).Rule);
    }

    [Fact]
    public async Task FilterAppliedDuringPartialGroup_AndEvictedPrefix_KeepCrossFragmentMatch()
    {
        var log = View(2);
        log.SetHighlightRules([Rule()]);
        log.AddRange([Rx([0xDE], 1), Rx([0xAD], 2)]);
        await log.SetViewFiltersAsync([Rule()]);
        log.AddRange([Rx([0xBE], 3), Rx([0xEF], 4)]);
        Assert.Equal(Pattern, Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        log.RefreshVisibleFormatting();
        Assert.Equal(Pattern, Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        await log.SetViewFiltersAsync([Rule()]);
        Assert.Equal(Pattern, Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
    }

    [Fact]
    public void ByteMatching_DoesNotUseDisplayedTextOrMatchHalfBytes()
    {
        var matcher = new StreamingHighlightRuleMatcher();
        var rules = new[] { LogRuleMatcher.Compile(Rule("AD BE")) };
        Assert.Null(matcher.Evaluate(Rx([0x1A, 0xDB], 1), rules, LogRuleMatchMode.Hex).Rule);
        Assert.Null(matcher.Evaluate(LogLine.Rx("AD BE", [0xE0], 2, true, displayText: "AD BE", contentMode: LogRuleMatchMode.Hex), rules, LogRuleMatchMode.Hex).Rule);
    }

    [Fact]
    public void LongUnterminatedGroup_KeepsOnlyBoundedSuffixes_AndDoesNotRepeatOldMatch()
    {
        var matcher = new StreamingEventRuleMatcher();
        var rule = LogRuleMatcher.Compile(new EventRule { Keyword = Pattern, Mode = LogRuleMatchMode.Hex });
        var highlight = new StreamingHighlightRuleMatcher();
        var rules = new[] { LogRuleMatcher.Compile(Rule()) };
        for (var i = 1; i <= 1000; i++)
        {
            var line = Rx(new byte[1024], i);
            matcher.BeginLine(line, rule, LogRuleMatchMode.Hex);
            Assert.False(matcher.IsMatch(line, rule, LogRuleMatchMode.Hex, out _, out _));
            Assert.Null(highlight.Evaluate(line, rules, LogRuleMatchMode.Hex).Rule);
            Assert.InRange(matcher.RetainedByteCount, 0, 3);
            Assert.InRange(highlight.RetainedByteCount, 0, 3);
        }
        var match = Rx([0xDE, 0xAD, 0xBE, 0xEF], 1001);
        matcher.BeginLine(match, rule, LogRuleMatchMode.Hex);
        Assert.True(matcher.IsMatch(match, rule, LogRuleMatchMode.Hex, out _, out _));
        var next = Rx([0x00], 1002);
        matcher.BeginLine(next, rule, LogRuleMatchMode.Hex);
        Assert.False(matcher.IsMatch(next, rule, LogRuleMatchMode.Hex, out _, out _));
    }

    [Fact]
    public async Task ActualPipeline64KSplit_MatchesAcrossInternalSegmentBoundary()
    {
        var input = Channel.CreateUnbounded<ReceivedByteChunk>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, 1000);
        await pipeline.StartAsync(input.Reader, new SerialSettings { Encoding = RxEncodingMode.Hex }, default);
        var bytes = new byte[65_538];
        new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }.CopyTo(bytes, 65_534);
        var receivedAt = Stopwatch.GetTimestamp();
        await input.Writer.WriteAsync(new ReceivedByteChunk(bytes[..65_536], receivedAt));
        await input.Writer.WriteAsync(new ReceivedByteChunk(bytes[65_536..], receivedAt + Stopwatch.Frequency / 1000, endsAtNativeIdleBoundary: true));
        input.Writer.Complete();
        await pipeline.DrainAsync(default);
        var lines = new List<LogLine>();
        while (pipeline.Logs.TryRead(out var line)) lines.Add(line);
        Assert.Equal(2, lines.Count(l => l.RawBytes is { Length: > 0 }));
        await using var detector = new EventDetector();
        await detector.StartAsync([new EventRule { Keyword = Pattern, Mode = LogRuleMatchMode.Hex }], new(), default);
        detector.UpdateRuleMode(LogRuleMatchMode.Hex);
        var log = View();
        log.SetHighlightRules([Rule()]);
        log.SetViewFilter(Rule());
        log.AddRange(lines);
        foreach (var line in lines) Assert.True(detector.TryEnqueue(line));
        await detector.StopAsync(default);
        Assert.Equal(1, detector.DetectedEventCount);
        Assert.EndsWith(Pattern, Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        Assert.Equal(HexFormatter.Format(bytes), Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
    }
}
