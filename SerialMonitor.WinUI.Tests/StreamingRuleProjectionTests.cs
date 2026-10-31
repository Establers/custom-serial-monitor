using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class StreamingRuleProjectionTests
{
    private const string Red = "\u001b[31m";

    [Theory]
    [InlineData(RxDisplayMode.Terminal)]
    [InlineData(RxDisplayMode.Hex)]
    public void LaterPartialFragment_UsesHighlightPriorityAndCountsMatches(RxDisplayMode mode)
    {
        var log = Log(mode);
        var lowPriority = Rule("E", mode, priority: -1);
        lowPriority.ForegroundColor = "Yellow";
        log.SetHighlightRules([lowPriority, Rule("ERROR", mode, priority: int.MaxValue)]);
        log.AddRange([Rx("start ", 1), Rx("ERROR", 2), LogLine.RxPartialTerminator(3)]);
        Assert.Contains(Red, log.GetVisibleTextSnapshot());
        Assert.DoesNotContain("\u001b[33m", log.GetVisibleTextSnapshot());
        Assert.Equal(1, log.HighlightedLineCount);
        var before = log.GetVisibleTextSnapshot();
        log.RefreshVisibleFormatting();
        Assert.Equal(before, log.GetVisibleTextSnapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerminalSplitKeyword_IsVisibleHighlightedAndNavigable(bool finalWithNewline)
    {
        var log = Log(RxDisplayMode.Terminal);
        log.SetHighlightRules([Rule("FAULT")]);
        log.SetViewFilter(Rule("FAULT"));
        var final = LogLine.Rx("ULT", "ULT"u8.ToArray(), 2, isPartialRxSegment: !finalWithNewline);
        log.AddRange([Rx("FA", 1)]);
        log.AddRange([final, LogLine.RxPartialTerminator(3)]);
        Assert.Equal("FAULT", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        Assert.Contains(Red, log.GetVisibleTextSnapshot());
        Assert.True(log.TryGetSearchLine(final, out var found));
        Assert.Equal("FAULT", found.PayloadText);
        var before = log.GetVisibleTextSnapshot();
        log.RefreshVisibleFormatting();
        Assert.Equal(before, log.GetVisibleTextSnapshot());
        log.SetViewFilters([]);
        Assert.Equal("FAULT", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
    }

    [Fact]
    public void TerminalMultipleFilters_DoNotRepeatAlreadyVisiblePrefixes()
    {
        var log = Log(RxDisplayMode.Terminal);
        log.SetViewFilters([Rule("FA"), Rule("FAULT")]);
        log.AddRange([Rx("FA", 1), Rx("U", 2), Rx("LT", 3), LogLine.RxPartialTerminator(4)]);
        Assert.Equal("FAULT", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        log.RefreshVisibleFormatting();
        Assert.Equal("FAULT", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
    }

    [Theory]
    [InlineData("terminator")]
    [InlineData("gap")]
    [InlineData("contentMode")]
    [InlineData("rules")]
    [InlineData("clear")]
    public void TerminalBoundary_DoesNotCreateFalseCrossBoundaryMatch(string boundary)
    {
        var log = Log(RxDisplayMode.Terminal);
        log.SetHighlightRules([Rule("FAULT")]);
        log.SetViewFilter(Rule("FAULT"));
        log.AddRange([Rx("FA", 1)]);
        if (boundary == "terminator") log.AddRange([LogLine.RxPartialTerminator(2)]);
        if (boundary == "rules") { log.SetViewFilter(Rule("FAULT")); log.SetHighlightRules([Rule("FAULT")]); }
        if (boundary == "clear") log.Clear();
        log.AddRange([LogLine.Rx("ULT", "ULT"u8.ToArray(), boundary == "gap" ? 4 : 2,
            isPartialRxSegment: true, contentMode: boundary == "contentMode" ? LogRuleMatchMode.Hex : LogRuleMatchMode.Terminal)]);
        // Reapplying a filter intentionally evaluates all retained history.
        if (boundary != "rules") Assert.Empty(log.GetVisibleSearchContentSnapshot());
        Assert.DoesNotContain(Red, log.GetVisibleTextSnapshot());
    }

    [Fact]
    public void HexSplitPattern_RemainsRecordLocalWhileLaterWholePatternMatches()
    {
        var log = Log(RxDisplayMode.Hex);
        log.SetHighlightRules([Rule("FAULT", RxDisplayMode.Hex)]);
        log.SetViewFilter(Rule("FAULT", RxDisplayMode.Hex));
        log.AddRange([Rx("FA", 1), Rx("ULT", 2)]);
        Assert.Empty(log.GetVisibleSearchContentSnapshot());
        log.AddRange([Rx("FAULT", 3), LogLine.RxPartialTerminator(4)]);
        Assert.Equal("46 41 55 4C 54", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        Assert.Contains(Red, log.GetVisibleTextSnapshot());
    }

    [Fact]
    public void FinalNewlineFragment_StaysOnItsExistingLogicalLine()
    {
        var log = Log(RxDisplayMode.Terminal);
        log.SetHighlightRules([Rule("FAULT")]);
        log.AddRange([Rx("FA", 1), LogLine.Rx("ULT", "ULT"u8.ToArray(), 2)]);
        Assert.Equal("FAULT", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        Assert.False(log.PartialRxVisualLineActive);
        Assert.Contains(Red, log.GetVisibleTextSnapshot());
    }

    [Fact]
    public void HiddenCompletedLine_EndsThePreviousVisiblePartialLine()
    {
        var log = Log(RxDisplayMode.Terminal);
        log.SetViewFilter(Rule("KEEP"));
        log.AddRange([Rx("KEEP-A", 1), LogLine.Rx("hidden", sequenceNumber: 2), Rx("KEEP-B", 3)]);
        Assert.Equal(new[] { "KEEP-A", "KEEP-B" }, log.GetVisibleSearchContentSnapshot().Select(line => line.PayloadText));
    }

    [Fact]
    public void VeryLongUnterminatedStream_RetainsOnlyKeywordSuffixes()
    {
        var matcher = new StreamingHighlightRuleMatcher();
        var rules = new[] { LogRuleMatcher.Compile(Rule("FAULT")), LogRuleMatcher.Compile(Rule("ERROR")) };
        for (var i = 1; i <= 10_000; i++)
        {
            Assert.Null(matcher.Evaluate(Rx(new string('x', 1000), i), rules, LogRuleMatchMode.Terminal).Rule);
            Assert.InRange(matcher.RetainedCharacterCount, 0, 8);
        }
        Assert.NotNull(matcher.Evaluate(Rx("FAULT", 10_001), rules, LogRuleMatchMode.Terminal).Rule);
    }

    [Fact]
    public void EvictedHiddenPrefix_KeepsMatchUntilItsCompletingRecordExpires()
    {
        var log = new LogViewModel(2);
        log.SetViewFilter(Rule("FAULT"));
        log.AddRange([Rx("FA", 1), Rx("ULT", 2)]);
        Assert.Equal("FAULT", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        var rebuilds = 0;
        var appends = 0;
        log.TextRebuilt += (_, _) => rebuilds++;
        log.TextBatchAppended += (_, _) => appends++;
        log.AddRange([LogLine.Rx("next", sequenceNumber: 3)]);
        Assert.Equal("FAULT", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        Assert.Equal(1, rebuilds);
        Assert.Equal(0, appends);
        log.RefreshVisibleFormatting();
        Assert.Equal("FAULT", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        log.AddRange([LogLine.Rx("next again", sequenceNumber: 4)]);
        Assert.Empty(log.GetVisibleSearchContentSnapshot());
    }

    private static LogViewModel Log(RxDisplayMode mode)
    {
        var log = new LogViewModel(100);
        log.SetShowTimestampInLogView(false);
        log.SetShowRxTxDirectionPrefixInLogView(false);
        log.SetRxDisplayMode(mode);
        return log;
    }

    private static LogLine Rx(string text, long sequence) =>
        LogLine.Rx(text, System.Text.Encoding.UTF8.GetBytes(text), sequence, isPartialRxSegment: true);

    private static HighlightRule Rule(string text, RxDisplayMode mode = RxDisplayMode.Terminal, int priority = 0) => new()
    {
        Keyword = mode == RxDisplayMode.Hex ? Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(text)) : text,
        ForegroundColor = "Red", UseAsViewFilter = true, Priority = priority,
        Mode = mode == RxDisplayMode.Hex ? LogRuleMatchMode.Hex : LogRuleMatchMode.Terminal
    };
}
