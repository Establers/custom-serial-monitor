using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class RegexLogRuleTests
{
    private static HighlightRule Highlight(string pattern = @"^temp=\d+$") => new()
    { Name = "temperature", Keyword = pattern, UseRegex = true, ForegroundColor = "Red", UseAsViewFilter = true };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FragmentedLine_MatchesOnlyAtCompletion_ConsistentAcrossEventHighlightAndFilter(bool separateTerminator)
    {
        var lines = new List<LogLine> { LogLine.Rx("temp=", sequenceNumber: 1, isPartialRxSegment: true),
            LogLine.Rx("42", sequenceNumber: 2, isPartialRxSegment: separateTerminator) };
        if (separateTerminator) lines.Add(LogLine.RxPartialTerminator(3));
        await using var detector = new EventDetector();
        await detector.StartAsync([new EventRule { Keyword = @"^temp=\d+$", UseRegex = true }], new(), default);
        var view = new LogViewModel(100);
        view.SetShowRxTxDirectionPrefixInLogView(false);
        view.SetHighlightRules([Highlight()]);
        view.SetViewFilter(Highlight());
        view.AddRange([lines[0]]);
        Assert.Empty(view.GetVisibleSearchContentSnapshot());
        view.AddRange(lines.Skip(1).ToArray());
        foreach (var line in lines) Assert.True(detector.TryEnqueue(line));
        await detector.StopAsync(default);
        Assert.Equal(1, detector.DetectedEventCount);
        Assert.True(detector.DetectedEvents.TryRead(out var detected));
        Assert.Equal("temp=42", detected.Message);
        Assert.True(view.TryGetSearchLine(detected.SourceLine!, out var filteredSearchLine));
        Assert.Equal("temp=42", filteredSearchLine.PayloadText);
        Assert.Equal("temp=42", Assert.Single(view.GetVisibleSearchContentSnapshot()).PayloadText);
        Assert.Contains("\u001b[31m", view.GetVisibleTextSnapshot());
        var before = view.GetVisibleTextSnapshot();
        view.RefreshVisibleFormatting();
        Assert.Equal(before, view.GetVisibleTextSnapshot());
        await view.SetViewFiltersAsync([Highlight()]);
        Assert.Equal(before, view.GetVisibleTextSnapshot());
        view.SetViewFilters([]);
        Assert.Equal("temp=42", Assert.Single(view.GetVisibleSearchContentSnapshot()).PayloadText);
        Assert.True(view.TryGetSearchLine(detected.SourceLine!, out var unfilteredSearchLine));
        Assert.Equal("temp=42", unfilteredSearchLine.PayloadText);
    }

    [Fact]
    public void EndAnchor_DoesNotFireOnUnfinishedPrefix_AndHighlightRecolorsCompletedLine()
    {
        var view = new LogViewModel(100);
        view.SetHighlightRules([Highlight()]);
        view.AddRange([LogLine.Rx("temp=42", isPartialRxSegment: true)]);
        Assert.DoesNotContain("\u001b[31m", view.GetVisibleTextSnapshot());
        view.AddRange([LogLine.Rx("x"), LogLine.Rx("temp=1", isPartialRxSegment: true), LogLine.RxPartialTerminator()]);
        Assert.Contains("\u001b[31m", view.GetVisibleTextSnapshot());
        var before = view.GetVisibleTextSnapshot();
        view.RefreshVisibleFormatting();
        Assert.Equal(before, view.GetVisibleTextSnapshot());
    }

    [Fact]
    public void OverlongLine_IsSkippedUntilNewline_ThenRecovers()
    {
        var matcher = new StreamingEventRuleMatcher();
        var rule = LogRuleMatcher.Compile(new EventRule { Keyword = "ERROR", UseRegex = true });
        var rules = new[] { rule };
        foreach (var line in new[] { LogLine.Rx(new string('x', RuleRegex.MaxInputLength), isPartialRxSegment: true),
            LogLine.Rx("ERROR", isPartialRxSegment: true), LogLine.RxPartialTerminator() })
        {
            matcher.BeginLine(line, rules, LogRuleMatchMode.Terminal);
            Assert.False(matcher.IsMatch(line, rule, LogRuleMatchMode.Terminal, out _, out _));
        }
        var recovery = LogLine.Rx("ERROR");
        matcher.BeginLine(recovery, rules, LogRuleMatchMode.Terminal);
        Assert.True(matcher.IsMatch(recovery, rule, LogRuleMatchMode.Terminal, out _, out _));
    }

    [Theory]
    [InlineData("[")]
    [InlineData("(?=ERROR)ERROR")]
    [InlineData(@"(ERROR)\1")]
    public void UnsupportedPattern_IsRejected(string pattern)
    {
        Assert.False(LogRuleValidation.TryValidatePattern(new LogRule { Keyword = pattern, UseRegex = true }, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void EmptyMatches_AreIgnored_AndPatternsAreNotCaseFoldedForFilterIdentity()
    {
        var zero = LogRuleMatcher.Compile(Highlight("^"));
        Assert.False(LogRuleMatcher.IsMatch(LogLine.Rx("abc"), zero, LogRuleMatchMode.Terminal, out _));
        var digits = MainViewModel.BuildViewFilterKey(new LogRule { Name = "same", Keyword = @"\d", UseRegex = true });
        var nonDigits = MainViewModel.BuildViewFilterKey(new LogRule { Name = "same", Keyword = @"\D", UseRegex = true });
        var literal = MainViewModel.BuildViewFilterKey(new LogRule { Name = "same", Keyword = @"\d" });
        Assert.NotEqual(digits, nonDigits);
        Assert.NotEqual(digits, literal);
    }

    [Fact]
    public void RangeAndCaseSensitivity_AgreeWithDetection()
    {
        var rule = new EventRule { Keyword = @"error=\d+", UseRegex = true };
        var line = LogLine.Rx("ERROR=42 and error=7");
        Assert.True(LogRuleMatcher.IsMatch(line, rule, LogRuleMatchMode.Terminal, out _));
        Assert.Equal(2, EventMatchRangeResolver.Resolve(line, rule).Count);
        rule.CaseSensitive = true;
        Assert.Single(EventMatchRangeResolver.Resolve(line, rule));
    }

    [Fact]
    public async Task EvictedHiddenPrefix_RegexFilterMatchesFullLine_AndSurvivesRebuild()
    {
        var log = new LogViewModel(2);
        log.SetShowRxTxDirectionPrefixInLogView(false);
        log.SetHighlightRules([Highlight(@"^prefix temp=42$")]);
        log.SetViewFilter(Highlight(@"^prefix temp=42$"));
        log.AddRange([LogLine.Rx("prefix ", sequenceNumber: 1, isPartialRxSegment: true),
            LogLine.Rx("temp=", sequenceNumber: 2, isPartialRxSegment: true),
            LogLine.Rx("42", sequenceNumber: 3, isPartialRxSegment: true), LogLine.RxPartialTerminator(4)]);
        await log.SetViewFiltersAsync([Highlight(@"^prefix temp=42$")]);
        Assert.Equal("prefix temp=42", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        log.RefreshVisibleFormatting();
        Assert.Equal("prefix temp=42", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
    }

    [Fact]
    public void DroppedRxFragment_CannotBecomeAFalseRegexLineStart()
    {
        var matcher = new StreamingEventRuleMatcher();
        var rule = LogRuleMatcher.Compile(new EventRule { Keyword = "^ERROR$", UseRegex = true });
        var rules = new[] { rule };
        foreach (var line in new[] { LogLine.Rx("NOT ", sequenceNumber: 1, isPartialRxSegment: true), LogLine.Rx("ERROR", sequenceNumber: 3) })
        {
            matcher.BeginLine(line, rules, LogRuleMatchMode.Terminal);
            Assert.False(matcher.IsMatch(line, rule, LogRuleMatchMode.Terminal, out _, out _));
        }
        var next = LogLine.Rx("ERROR", sequenceNumber: 4);
        matcher.BeginLine(next, rules, LogRuleMatchMode.Terminal);
        Assert.True(matcher.IsMatch(next, rule, LogRuleMatchMode.Terminal, out _, out _));
    }

    [Fact]
    public async Task GapAfterCompleteLine_DoesNotTreatUnknownRemainderAsLineStart()
    {
        var lines = new[] { LogLine.Rx("OK", sequenceNumber: 1), LogLine.Rx("ERROR", sequenceNumber: 3) };
        await using var detector = new EventDetector();
        await detector.StartAsync([new EventRule { Keyword = "^ERROR$", UseRegex = true }], new(), default);
        foreach (var line in lines) detector.TryEnqueue(line);
        await detector.StopAsync(default);
        Assert.Equal(0, detector.DetectedEventCount);
        var matcher = new StreamingHighlightRuleMatcher();
        var rules = new[] { LogRuleMatcher.Compile(Highlight("^ERROR$")) };
        foreach (var line in lines) Assert.Null(matcher.Evaluate(line, rules, LogRuleMatchMode.Terminal).Rule);
        var log = new LogViewModel(100);
        log.SetHighlightRules([Highlight("^ERROR$")]);
        log.SetViewFilter(Highlight("^ERROR$"));
        log.AddRange(lines);
        Assert.Empty(log.GetVisibleSearchContentSnapshot());
    }

    [Fact]
    public void LiteralTail_IsNotUsedAsFullRegexContextAfterEviction()
    {
        var log = new LogViewModel(2);
        log.SetShowRxTxDirectionPrefixInLogView(false);
        log.SetHighlightRules([new HighlightRule { Keyword = "temp=42", ForegroundColor = "Red" }]);
        log.AddRange([LogLine.Rx("NOT temp=", sequenceNumber: 1, isPartialRxSegment: true),
            LogLine.Rx("4", sequenceNumber: 2, isPartialRxSegment: true),
            LogLine.Rx("2", sequenceNumber: 3, isPartialRxSegment: true), LogLine.RxPartialTerminator(4)]);
        log.SetViewFilter(Highlight(@"^temp=42$"));
        Assert.Empty(log.GetVisibleSearchContentSnapshot());
    }

    [Fact]
    public async Task ApplyingRegexFilterDuringPartialRx_PreservesPendingLine()
    {
        var log = new LogViewModel(100);
        log.SetShowRxTxDirectionPrefixInLogView(false);
        log.AddRange([LogLine.Rx("temp=", sequenceNumber: 1, isPartialRxSegment: true)]);
        await log.SetViewFiltersAsync([Highlight()]);
        log.AddRange([LogLine.Rx("42", sequenceNumber: 2, isPartialRxSegment: true), LogLine.RxPartialTerminator(3)]);
        Assert.Equal("temp=42", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
    }

    [Fact]
    public async Task CompletedLineEvictedFromHistory_IsNotResurrectedByRegexFilter()
    {
        var log = new LogViewModel(2);
        log.SetHighlightRules([Highlight()]);
        log.AddRange([LogLine.Rx("temp=42"), LogLine.Rx("OK"), LogLine.Rx("OK")]);
        await log.SetViewFiltersAsync([Highlight()]);
        Assert.Empty(log.GetVisibleSearchContentSnapshot());
    }

    [Fact]
    public void RegexRuleReplacement_DoesNotTreatRemainingFragmentAsNewLine()
    {
        var matcher = new StreamingEventRuleMatcher();
        var oldRule = LogRuleMatcher.Compile(new EventRule { Keyword = "^ERROR$", UseRegex = true });
        var first = LogLine.Rx("NOT ", sequenceNumber: 1, isPartialRxSegment: true);
        matcher.BeginLine(first, oldRule, LogRuleMatchMode.Terminal);
        var replacement = LogRuleMatcher.Compile(new EventRule { Keyword = "^ERROR$", UseRegex = true });
        var last = LogLine.Rx("ERROR", sequenceNumber: 2);
        matcher.BeginLine(last, replacement, LogRuleMatchMode.Terminal);
        Assert.False(matcher.IsMatch(last, replacement, LogRuleMatchMode.Terminal, out _, out _));
    }

    [Fact]
    public void MixedLiteralAndRegexFilters_DoNotDuplicateVisiblePartialText()
    {
        var log = new LogViewModel(100);
        log.SetShowRxTxDirectionPrefixInLogView(false);
        log.SetViewFilters([new HighlightRule { Keyword = "temp=", UseAsViewFilter = true }, Highlight()]);
        log.AddRange([LogLine.Rx("temp=", sequenceNumber: 1, isPartialRxSegment: true),
            LogLine.Rx("42", sequenceNumber: 2, isPartialRxSegment: true), LogLine.RxPartialTerminator(3)]);
        Assert.Equal("temp=42", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        log.RefreshVisibleFormatting();
        Assert.Equal("temp=42", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
    }

    [Fact]
    public void HighlightRuleChangeOnInterleavedTx_DoesNotTurnRxRemainderIntoNewLine()
    {
        var matcher = new StreamingHighlightRuleMatcher();
        var before = new[] { LogRuleMatcher.Compile(Highlight("^ERROR$")) };
        var after = new[] { LogRuleMatcher.Compile(Highlight("^ERROR$")) };
        matcher.Evaluate(LogLine.Rx("NOT ", sequenceNumber: 1, isPartialRxSegment: true), before, LogRuleMatchMode.Terminal);
        matcher.Evaluate(LogLine.Tx("query"), after, LogRuleMatchMode.Terminal);
        Assert.Null(matcher.Evaluate(LogLine.Rx("ERROR", sequenceNumber: 2), after, LogRuleMatchMode.Terminal).Rule);
        Assert.Same(after[0], matcher.Evaluate(LogLine.Rx("ERROR", sequenceNumber: 3), after, LogRuleMatchMode.Terminal).Rule);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FilterPublishAfterRejectedScreenRecord_DoesNotLoseRegexLineStart(bool highlightKeepsRegex)
    {
        var rules = new[] { LogRuleMatcher.Compile(Highlight("^ERROR$")), LogRuleMatcher.Compile(Highlight("^NOT ERROR$")) };
        var live = new StreamingHighlightRuleMatcher();
        var highlight = new StreamingHighlightRuleMatcher();
        var first = LogLine.Rx("NOT ", sequenceNumber: 1, isPartialRxSegment: true);
        live.Evaluate(first, [], LogRuleMatchMode.Terminal, position: 1);
        highlight.Evaluate(first, highlightKeepsRegex ? rules : [], LogRuleMatchMode.Terminal, position: 1);
        // The screen-only rejected record was consumed live but is absent from
        // the rebuilt snapshot when a newly selected filter is published.
        var published = new StreamingHighlightRuleMatcher().WithLatestContext(live, highlight, rules, LogRuleMatchMode.Terminal);
        var completed = published.Evaluate(LogLine.Rx("ERROR", sequenceNumber: 2), rules, LogRuleMatchMode.Terminal,
            position: 2, previousRxPosition: 1, continuesRxLine: true);
        Assert.NotSame(rules[0], completed.Rule);
        if (highlightKeepsRegex) Assert.Same(rules[1], completed.Rule);
        else Assert.Null(completed.Rule);
    }

    [Fact]
    public void NestedQuantifiers_AndUnterminatedInputRemainBounded()
    {
        var compiled = LogRuleMatcher.Compile(Highlight("(a+)+$"));
        Assert.False(LogRuleMatcher.IsMatch(LogLine.Rx(new string('a', 8000) + "!"), compiled, LogRuleMatchMode.Terminal, out _));
        var matcher = new StreamingHighlightRuleMatcher();
        var rules = new[] { compiled };
        for (var i = 0; i < 1000; i++)
        {
            Assert.Null(matcher.Evaluate(LogLine.Rx(new string('a', 1000), isPartialRxSegment: true), rules, LogRuleMatchMode.Terminal).Rule);
            Assert.InRange(matcher.RetainedCharacterCount, 0, RuleRegex.MaxInputLength);
        }
        Assert.Null(matcher.Evaluate(LogLine.RxPartialTerminator(), rules, LogRuleMatchMode.Terminal).Rule);
    }

    [Fact]
    public async Task TransportBoundary_DoesNotFireRegexOrAutomaticSequence()
    {
        await using var detector = new EventDetector();
        await detector.StartAsync([new EventRule { Keyword = "^ERROR$", UseRegex = true, TriggerSequenceName = "Reset" }], new(), default);
        var log = new LogViewModel(100);
        log.SetHighlightRules([Highlight("^ERROR$")]);
        log.SetViewFilter(Highlight("^ERROR$"));
        var lines = new[] { LogLine.Rx("ERROR", sequenceNumber: 1, isPartialRxSegment: true), LogLine.RxStreamBoundary(2) };
        log.AddRange(lines);
        foreach (var line in lines) detector.TryEnqueue(line);
        await detector.StopAsync(default);
        Assert.Empty(log.GetVisibleSearchContentSnapshot());
        Assert.Equal(0, detector.DetectedEventCount);
        Assert.False(detector.SequenceTriggerEvents.TryRead(out _));
        log.AddRange([LogLine.Rx("ERROR", sequenceNumber: 3)]);
        Assert.Empty(log.GetVisibleSearchContentSnapshot());
        log.AddRange([LogLine.Rx("ERROR", sequenceNumber: 4)]);
        Assert.Single(log.GetVisibleSearchContentSnapshot());
    }

    [Fact]
    public async Task RejectedDetectorInput_CannotFinalizeRegexOrTriggerFromRemainingFragment()
    {
        await using var detector = new EventDetector();
        await detector.StartAsync([new EventRule { Keyword = "^ERROR$", UseRegex = true, TriggerSequenceName = "Reset" }], new(), default);
        typeof(EventDetector).GetField("_inputMemory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(detector, new ChannelMemoryBudget<LogLine>(4096, LogViewMemoryPolicy.EstimateRetainedBytes));
        Assert.True(detector.TryEnqueue(LogLine.Rx("ERROR", isPartialRxSegment: true)));
        Assert.False(detector.TryEnqueue(LogLine.Rx("X" + new string('x', 8192), isPartialRxSegment: true)));
        Assert.True(detector.TryEnqueue(LogLine.Rx("ERROR")));
        Assert.True(detector.TryEnqueue(LogLine.Rx("ERROR")));
        await detector.StopAsync(default);
        Assert.Equal(1, detector.DetectedEventCount);
        Assert.True(detector.SequenceTriggerEvents.TryRead(out _));
        Assert.False(detector.SequenceTriggerEvents.TryRead(out _));
    }
}
