using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class StreamingEventRuleMatcherTests
{
    [Fact]
    public void MultipleFragmentsAndInterleavedTx_MatchOnlyNewOccurrence()
    {
        var matcher = new StreamingEventRuleMatcher();
        var rule = Rule("FAULT");
        Assert.False(Match(matcher, rule, LogLine.Rx("F", isPartialRxSegment: true)));
        Assert.False(Match(matcher, rule, LogLine.Tx("status")));
        Assert.False(Match(matcher, rule, LogLine.Rx("AU", isPartialRxSegment: true)));
        Assert.True(Match(matcher, rule, LogLine.Rx("LT", isPartialRxSegment: true)));
        Assert.False(Match(matcher, rule, LogLine.Rx(" more data")));
    }

    [Fact]
    public void ShortRule_DoesNotRematchTailRetainedForLongRule()
    {
        var matcher = new StreamingEventRuleMatcher();
        var shortRule = Rule("FAULT");
        var longRule = Rule("prefix FAULT suffix");
        var rules = new[] { shortRule, longRule };
        var first = LogLine.Rx("prefix FAULT", isPartialRxSegment: true);
        matcher.BeginLine(first, rules, LogRuleMatchMode.Terminal);
        Assert.True(matcher.IsMatch(first, shortRule, LogRuleMatchMode.Terminal, out _, out _));
        Assert.False(matcher.IsMatch(first, longRule, LogRuleMatchMode.Terminal, out _, out _));
        var second = LogLine.Rx(" suffix");
        matcher.BeginLine(second, rules, LogRuleMatchMode.Terminal);
        Assert.False(matcher.IsMatch(second, shortRule, LogRuleMatchMode.Terminal, out _, out _));
        Assert.True(matcher.IsMatch(second, longRule, LogRuleMatchMode.Terminal, out var match, out _));
        Assert.Equal("prefix FAULT suffix", match.Text);
    }

    [Fact]
    public void CompletedLines_AreNotJoined()
    {
        var matcher = new StreamingEventRuleMatcher();
        var rule = Rule("FAULT");
        Assert.False(Match(matcher, rule, LogLine.Rx("FA")));
        Assert.False(Match(matcher, rule, LogLine.Rx("ULT")));
    }

    [Fact]
    public void MissingRxSequence_DoesNotJoinAcrossDroppedData()
    {
        var matcher = new StreamingEventRuleMatcher();
        var rule = Rule("FAULT");
        Assert.False(Match(matcher, rule, LogLine.Rx("FA", sequenceNumber: 1, isPartialRxSegment: true)));
        Assert.False(Match(matcher, rule, LogLine.Rx("ULT", sequenceNumber: 3)));
    }

    [Fact]
    public void ContentModeChange_DoesNotJoinTextFromDifferentModes()
    {
        var matcher = new StreamingEventRuleMatcher();
        var rule = Rule("FAULT");
        Assert.False(Match(matcher, rule, LogLine.Rx("FA", isPartialRxSegment: true)));
        Assert.False(Match(matcher, rule, LogLine.Rx("ULT", contentMode: LogRuleMatchMode.Hex)));
    }

    [Fact]
    public void ActiveModeChange_DiscardsPartialText()
    {
        var matcher = new StreamingEventRuleMatcher();
        var rule = Rule("FAULT");
        Assert.False(Match(matcher, rule, LogLine.Rx("FA", isPartialRxSegment: true)));
        matcher.BeginLine(LogLine.Tx("status"), rule, LogRuleMatchMode.Hex);
        Assert.False(Match(matcher, rule, LogLine.Rx("ULT")));
    }

    [Fact]
    public void RuleReplacement_DoesNotInheritOldPartialText()
    {
        var matcher = new StreamingEventRuleMatcher();
        Assert.False(Match(matcher, Rule("FAULT"), LogLine.Rx("FA", isPartialRxSegment: true)));
        Assert.False(Match(matcher, Rule("FAULT"), LogLine.Rx("ULT")));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void CaseSensitivity_IsPreserved(bool caseSensitive, bool expected)
    {
        var matcher = new StreamingEventRuleMatcher();
        var rule = LogRuleMatcher.Compile(new EventRule { Keyword = "FAULT", CaseSensitive = caseSensitive });
        Assert.False(Match(matcher, rule, LogLine.Rx("fa", isPartialRxSegment: true)));
        Assert.Equal(expected, Match(matcher, rule, LogLine.Rx("ult")));
    }

    [Fact]
    public void LongUnterminatedStream_StillFindsNewBoundaryMatch()
    {
        var matcher = new StreamingEventRuleMatcher();
        var rule = Rule("FAULT");
        var fragment = new string('x', 1024);
        for (var i = 0; i < 10_000; i++)
            Assert.False(Match(matcher, rule, LogLine.Rx(fragment, isPartialRxSegment: true)));
        Assert.False(Match(matcher, rule, LogLine.Rx("FA", isPartialRxSegment: true)));
        Assert.True(Match(matcher, rule, LogLine.Rx("ULT")));
    }

    [Fact]
    public async Task Detector_TerminatorSeparatesLinesAndDoesNotProduceEvents()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var detector = new EventDetector();
        await detector.StartAsync([new EventRule { Keyword = "FAULT", TriggerSequenceName = "Recover" }],
            new EventContextSettings(), timeout.Token);
        Assert.True(detector.TryEnqueue(LogLine.Rx("FA", isPartialRxSegment: true)));
        Assert.True(detector.TryEnqueue(LogLine.RxPartialTerminator()));
        Assert.True(detector.TryEnqueue(LogLine.Rx("ULT")));
        Assert.True(detector.TryEnqueue(LogLine.Rx("FA", isPartialRxSegment: true)));
        Assert.True(detector.TryEnqueue(LogLine.Rx("ULT", isPartialRxSegment: true)));
        Assert.True(detector.TryEnqueue(LogLine.RxPartialTerminator()));
        await detector.StopAsync(timeout.Token);
        Assert.Equal(1, detector.DetectedEventCount);
        Assert.True(detector.DetectedEvents.TryRead(out var found));
        Assert.Equal("FAULT", found.Message);
        Assert.True(detector.SequenceTriggerEvents.TryRead(out _));
        Assert.False(detector.SequenceTriggerEvents.TryRead(out _));
    }

    private static LogRuleMatcher.CompiledEventRule Rule(string keyword) =>
        LogRuleMatcher.Compile(new EventRule { Keyword = keyword });

    private static bool Match(StreamingEventRuleMatcher matcher, LogRuleMatcher.CompiledEventRule rule, LogLine line)
    {
        matcher.BeginLine(line, rule, LogRuleMatchMode.Terminal);
        var matched = matcher.IsMatch(line, rule, LogRuleMatchMode.Terminal, out _, out var error);
        Assert.Null(error);
        return matched;
    }
}
