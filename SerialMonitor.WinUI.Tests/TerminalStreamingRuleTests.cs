using System.Text;
using System.Threading.Channels;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class TerminalStreamingRuleTests
{
    private static HighlightRule Rule(bool regex = false, string? pattern = null) => new()
    {
        Keyword = pattern ?? (regex ? @"^prefix temp=\d+ suffix$" : "temp=42"),
        UseRegex = regex, ForegroundColor = "Red", UseAsViewFilter = true
    };
    private static LogLine Rx(string text, long sequence, bool partial = true) =>
        LogLine.Rx(text, sequenceNumber: sequence, isPartialRxSegment: partial);
    private static LogViewModel View(int capacity = 100, LogViewMemoryPolicy? policy = null)
    {
        var log = policy is null ? new LogViewModel(capacity) : new LogViewModel(capacity, policy);
        log.SetShowTimestampInLogView(false);
        log.SetShowRxTxDirectionPrefixInLogView(false);
        return log;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SplitLine_AgreesAcrossEventHighlightAndWholeLineFilter(bool regex, bool separateTerminator)
    {
        var rule = Rule(regex);
        var lines = new List<LogLine> { Rx("prefix ", 1), Rx("te", 2), Rx("mp=4", 3), Rx("2", 4),
            Rx(" suffix", 5, separateTerminator) };
        if (separateTerminator) lines.Add(LogLine.RxPartialTerminator(6));
        var filtered = View();
        filtered.SetHighlightRules([rule]);
        filtered.SetViewFilter(rule);
        var unfiltered = View();
        unfiltered.SetHighlightRules([rule]);
        await using var detector = new EventDetector();
        await detector.StartAsync([new EventRule { Keyword = rule.Keyword, UseRegex = regex, TriggerSequenceName = "Recover" }], new(), default);
        for (var i = 0; i < lines.Count; i++)
        {
            filtered.AddRange([lines[i]]);
            unfiltered.AddRange([lines[i]]);
            Assert.True(detector.TryEnqueue(lines[i]));
            if (i < (regex ? lines.Count - 1 : 3))
            {
                Assert.Empty(filtered.GetVisibleSearchContentSnapshot());
                Assert.DoesNotContain("\u001b[31m", unfiltered.GetVisibleTextSnapshot());
            }
            else Assert.Contains("\u001b[31m", unfiltered.GetVisibleTextSnapshot());
        }
        await detector.StopAsync(default);
        Assert.Equal(1, detector.DetectedEventCount);
        Assert.True(detector.SequenceTriggerEvents.TryRead(out _));
        Assert.False(detector.SequenceTriggerEvents.TryRead(out _));
        Assert.True(detector.DetectedEvents.TryRead(out var detected));
        const string expected = "prefix temp=42 suffix";
        Assert.Equal(expected, Assert.Single(filtered.GetVisibleSearchContentSnapshot()).PayloadText);
        Assert.True(filtered.TryGetSearchLine(detected.SourceLine!, out var found));
        Assert.Equal(expected, found.PayloadText);
        Assert.Equal(expected, Assert.Single(unfiltered.GetVisibleSearchContentSnapshot()).PayloadText);
        Assert.Contains("\u001b[31m", filtered.GetVisibleTextSnapshot());
        var before = filtered.GetVisibleTextSnapshot();
        filtered.RefreshVisibleFormatting();
        Assert.Equal(before, filtered.GetVisibleTextSnapshot());
        await filtered.SetViewFiltersAsync([Rule(regex)]);
        Assert.Equal(before, filtered.GetVisibleTextSnapshot());
        await unfiltered.SetViewFiltersAsync([Rule(regex)]);
        Assert.Equal(before, unfiltered.GetVisibleTextSnapshot());
    }

    [Fact]
    public async Task MixedFilters_ShowEveryFragmentOnce_AndResetAtNextLine()
    {
        var log = View();
        log.SetViewFilters([Rule(), Rule(true)]);
        log.AddRange([Rx("prefix ", 1), Rx("temp=42", 2)]);
        Assert.Equal("prefix temp=42", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        log.AddRange([Rx(" suffix", 3), LogLine.RxPartialTerminator(4), Rx("hidden", 5, false)]);
        Assert.Equal("prefix temp=42 suffix", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        var before = log.GetVisibleTextSnapshot();
        await log.SetViewFiltersAsync([Rule(), Rule(true)]);
        Assert.Equal(before, log.GetVisibleTextSnapshot());
    }

    [Fact]
    public async Task MatchingLine_RemainsFilteredAfterEarlyFragmentsExpire_WithoutLeakingToOtherFilters()
    {
        var log = View(4, new LogViewMemoryPolicy(ContinuousLineCharacters: 10));
        log.SetViewFilter(Rule());
        log.AddRange([Rx("temp=", 1), Rx("42", 2), Rx("aaaaa", 3), Rx("bbbbb", 4), Rx("ccccc", 5), Rx("ddddd", 6)]);
        Assert.DoesNotContain(log.GetRetainedLinesSnapshot(), line => line.SequenceNumber <= 2);
        var before = log.GetVisibleTextSnapshot();
        Assert.NotEmpty(before);
        log.RefreshVisibleFormatting();
        Assert.Equal(before, log.GetVisibleTextSnapshot());
        await log.SetViewFiltersAsync([Rule()]);
        Assert.Equal(before, log.GetVisibleTextSnapshot());
        await log.SetViewFiltersAsync([Rule(pattern: "unrelated")]);
        Assert.Empty(log.GetVisibleSearchContentSnapshot());
    }

    [Fact]
    public void HiddenPrefixBuffer_ObeysScreenLimits()
    {
        var context = new TerminalFilterLineContext();
        var rules = new object();
        for (var i = 1; i <= 1000; i++)
        {
            Assert.False(context.Evaluate(Rx(new string('x', 32), i), i, i - 1, 1, rules, i == 1, false, "", 2, 50).Visible);
            Assert.InRange(context.RetainedCharacterCount, 0, 50);
        }
        var shown = context.Evaluate(Rx("match", 1001), 1001, 1000, 1, rules, false, true, "", 2, 50);
        Assert.True(shown.Visible);
        Assert.InRange(shown.Prefix.Length, 0, 50);
        Assert.Equal(0, context.RetainedCharacterCount);
    }

    [Fact]
    public void CachedVisibility_DoesNotLeakAcrossDisplayModes()
    {
        var log = View();
        log.SetViewFilters([Rule(), new HighlightRule { Mode = LogRuleMatchMode.Hex, Keyword = "FF", UseAsViewFilter = true }]);
        log.AddRange([LogLine.Rx("temp=42", "temp=42"u8.ToArray(), sequenceNumber: 1)]);
        Assert.NotEmpty(log.GetVisibleSearchContentSnapshot());
        log.SetRxDisplayMode(RxDisplayMode.Hex);
        Assert.Empty(log.GetVisibleSearchContentSnapshot());
        log.SetRxDisplayMode(RxDisplayMode.Terminal);
        Assert.Equal("temp=42", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
    }

    [Theory]
    [InlineData(RxDisplayMode.Terminal)]
    [InlineData(RxDisplayMode.Hex)]
    public void RejectedScreenFragment_OnlyRestoresTheSmallMatchingSuffix(RxDisplayMode mode)
    {
        var log = View(100, new LogViewMemoryPolicy(RetainedBytes: 2048, ContinuousLineCharacters: 128));
        log.SetRxDisplayMode(mode);
        var rule = Rule(pattern: mode == RxDisplayMode.Hex ? "46 41 55 4C 54" : "FAULT");
        rule.Mode = mode == RxDisplayMode.Hex ? LogRuleMatchMode.Hex : LogRuleMatchMode.Terminal;
        log.SetViewFilter(rule);
        foreach (var (text, sequence) in new[] { ("AAAA", 1), (new string('x', 2000) + "FA", 2), ("ULT", 3) })
            log.AddRange([LogLine.Rx(text, Encoding.UTF8.GetBytes(text), sequence, isPartialRxSegment: true, contentMode: rule.Mode)]);
        Assert.Equal(rule.Keyword, Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        log.RefreshVisibleFormatting();
        Assert.Equal(rule.Keyword, Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualPipelinePartialFlush_MatchesSplitKoreanPattern(bool regex)
    {
        const string keyword = "온도=42";
        var pattern = regex ? @"온도=\d+" : keyword;
        var expected = new string('x', 128) + keyword + " 정상";
        var source = Channel.CreateUnbounded<ReceivedByteChunk>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipeline.StartAsync(source.Reader, new SerialSettings { Encoding = RxEncodingMode.Utf8 }, timeout.Token);
        LogLine first, last;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(new string('x', 128) + "온");
            await source.Writer.WriteAsync(ReceivedByteChunk.Capture(bytes), timeout.Token);
            first = await pipeline.Logs.ReadAsync(timeout.Token);
            Assert.True(first.IsPartialRxSegment);
            await source.Writer.WriteAsync(ReceivedByteChunk.Capture(Encoding.UTF8.GetBytes("도=42 정상\r\n")), timeout.Token);
            last = await pipeline.Logs.ReadAsync(timeout.Token);
            Assert.False(last.IsPartialRxSegment);
        }
        finally { source.Writer.TryComplete(); await pipeline.DrainAsync(CancellationToken.None); }
        var log = View();
        log.SetHighlightRules([Rule(regex, pattern)]);
        log.SetViewFilter(Rule(regex, pattern));
        log.AddRange([first, last]);
        await using var detector = new EventDetector();
        await detector.StartAsync([new EventRule { Keyword = pattern, UseRegex = regex }], new(), default);
        Assert.True(detector.TryEnqueue(first));
        Assert.True(detector.TryEnqueue(last));
        await detector.StopAsync(default);
        Assert.Equal(1, detector.DetectedEventCount);
        Assert.Equal(expected, Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        Assert.Contains("\u001b[31m", log.GetVisibleTextSnapshot());
    }
}
