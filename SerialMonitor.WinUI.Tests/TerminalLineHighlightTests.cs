using System.Text;
using System.Text.RegularExpressions;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class TerminalLineHighlightTests
{
    private const string Red = "31";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryReceiveSplit_ColorsTheWholeStatusLine(bool separateBatches)
    {
        const string text = "prefix STATUS commands=118 injected_drop=0 injected_dup=0 injected_crc=0";
        for (var split = 1; split < text.Length; split++)
        {
            var log = Log();
            log.SetHighlightRules([Rule("STATUS")]);
            var lines = new[] { Rx(text[..split], 1), LogLine.Rx(text[split..], sequenceNumber: 2) };
            if (separateBatches) foreach (var line in lines) log.AddRange([line]);
            else log.AddRange(lines);
            Assert.Equal(text, Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
            AssertColor(log.GetVisibleTextSnapshot(), Red);
            var before = log.GetVisibleTextSnapshot();
            log.RefreshVisibleFormatting();
            Assert.Equal(before, log.GetVisibleTextSnapshot());
        }
    }

    [Fact]
    public void LateMatch_RecolorsTimestampBracketsAndEarlierTextWithATargetedBatch()
    {
        var log = new LogViewModel(100);
        log.SetHighlightRules([Rule("FAULT")]);
        var batches = new List<LogTextBatch>();
        var rebuilds = 0;
        log.TextBatchAppended += (_, batch) => batches.Add(batch);
        log.TextRebuilt += (_, _) => rebuilds++;
        log.AddRange([Rx("[INFO] prefix FA", 1)]);
        log.AddRange([Rx("ULT tail", 2), LogLine.RxPartialTerminator(3)]);

        Assert.Equal(0, rebuilds);
        Assert.Contains("\u001b]778;1;31\u0007", batches[1].AppendedText);
        AssertColor(log.GetVisibleTextSnapshot(), Red);
        Assert.DoesNotContain("38;2;207;232;255", log.GetVisibleTextSnapshot());
        Assert.DoesNotContain("\u001b]778;", log.GetXtermTextSnapshot());
        Assert.Contains("RX < [INFO] prefix FAULT tail", Plain(log.GetVisibleTextSnapshot()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateWinner_UsesPriorityThenRuleOrderAcrossTheEntireLogicalLine(bool equalPriority)
    {
        var log = Log();
        var yellow = Rule("WARN", "Yellow", priority: equalPriority ? 9 : -1);
        log.SetHighlightRules([Rule("ERROR", priority: 9), yellow]);
        log.AddRange([Rx("WARN first ", 1)]);
        log.AddRange([Rx("unmatched middle ", 2), Rx("ERROR last ", 3), LogLine.Rx("tail", sequenceNumber: 4)]);
        AssertColor(log.GetVisibleTextSnapshot(), Red);
        Assert.DoesNotContain("\u001b[33m", log.GetVisibleTextSnapshot());
        var before = log.GetVisibleTextSnapshot();
        log.RefreshVisibleFormatting();
        Assert.Equal(before, log.GetVisibleTextSnapshot());
    }

    [Fact]
    public void DefaultColorWinner_RemovesAnEarlierRuleWithoutChangingText()
    {
        var log = Log();
        log.SetHighlightRules([Rule("WARN"), Rule("IGNORE", "Default", 10)]);
        log.AddRange([Rx("WARN ", 1), Rx("IGNORE ", 2), LogLine.Rx("tail", sequenceNumber: 3)]);
        AssertColor(log.GetVisibleTextSnapshot(), "0");
        Assert.Equal("WARN IGNORE tail" + Environment.NewLine, log.GetVisibleTextSnapshot());
    }

    [Fact]
    public void InterleavedTx_IsNotRecoloredAndDoesNotBreakRxKeywordMatching()
    {
        var log = Log();
        log.SetHighlightRules([Rule("FAULT")]);
        log.AddRange([Rx("prefix FA", 1), LogLine.Tx("STATUS?"), Rx("ULT tail", 2), LogLine.RxPartialTerminator(3)]);
        var lines = log.GetVisibleTextSnapshot().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        AssertColor(lines[0], Red);
        AssertColor(lines[1], "36");
        AssertColor(lines[2], Red);
        var before = log.GetVisibleTextSnapshot();
        log.RefreshVisibleFormatting();
        Assert.Equal(before, log.GetVisibleTextSnapshot());
    }

    [Theory]
    [InlineData("newline")]
    [InlineData("terminator")]
    [InlineData("gap")]
    [InlineData("contentMode")]
    [InlineData("clear")]
    [InlineData("rules")]
    public void ColorDoesNotCarryAcrossRealBoundaries(string boundary)
    {
        var log = Log();
        log.SetHighlightRules([Rule("FAULT")]);
        log.AddRange([LogLine.Rx("FAULT", sequenceNumber: 1, isPartialRxSegment: boundary != "newline")]);
        if (boundary == "terminator") log.AddRange([LogLine.RxPartialTerminator(2)]);
        if (boundary == "clear") log.Clear();
        if (boundary == "rules") log.SetHighlightRules([Rule("OTHER", "Yellow")]);
        log.AddRange([LogLine.Rx("unmatched", sequenceNumber: boundary == "gap" ? 10 : boundary == "terminator" ? 3 : 2,
            isPartialRxSegment: true, contentMode: boundary == "contentMode" ? LogRuleMatchMode.Hex : LogRuleMatchMode.Terminal)]);
        var last = log.GetVisibleTextSnapshot().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[^1];
        AssertColor(last, "0");
        Assert.EndsWith("unmatched", last);
        var before = log.GetVisibleTextSnapshot();
        log.RefreshVisibleFormatting();
        Assert.Equal(before, log.GetVisibleTextSnapshot());
    }

    [Fact]
    public void FilterStillSelectsFragmentsAndHiddenMatchesCanRecolorVisibleText()
    {
        var log = Log();
        log.SetHighlightRules([Rule("ERROR")]);
        log.SetViewFilter(Rule("KEEP"));
        log.AddRange([Rx("KEEP first ", 1), Rx("hidden middle ", 2), Rx("KEEP last ", 3)]);
        LogTextBatch? update = null;
        log.TextBatchAppended += (_, batch) => update = batch;
        log.AddRange([Rx("ERROR hidden match", 4)]);
        Assert.Equal("KEEP first KEEP last ", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        AssertColor(log.GetVisibleTextSnapshot(), Red);
        Assert.Contains("\u001b]778;1;31\u0007", Assert.IsType<LogTextBatch>(update).AppendedText);
        Assert.Equal(1, update!.LineCount);
        var before = log.GetVisibleTextSnapshot();
        log.RefreshVisibleFormatting();
        Assert.Equal(before, log.GetVisibleTextSnapshot());
    }

    [Fact]
    public async Task ActiveLineAfterAsyncFilter_PreservesTheWinnerAndCanUpgradeIt()
    {
        var log = Log();
        log.SetHighlightRules([Rule("WARN", "Yellow"), Rule("ERROR", priority: 10)]);
        log.AddRange([Rx("WARN first ", 1)]);
        Assert.True(await log.SetViewFiltersAsync([]));
        log.AddRange([Rx("middle ", 2), Rx("ERROR last", 3)]);
        AssertColor(log.GetVisibleTextSnapshot(), Red);
        Assert.True(await log.SetViewFiltersAsync([]));
        log.AddRange([LogLine.Rx(" tail", sequenceNumber: 4)]);
        AssertColor(log.GetVisibleTextSnapshot(), Red);
        Assert.Equal("WARN first middle ERROR last tail", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncFilterPublish_PreservesRejectedRecordColorsAndBoundaries(bool receiveGap)
    {
        var log = new LogViewModel(100, new LogViewMemoryPolicy(RetainedBytes: 2048));
        log.SetShowTimestampInLogView(false);
        log.SetShowRxTxDirectionPrefixInLogView(false);
        log.SetHighlightRules([Rule("WARN", "Yellow"), Rule("ERROR", priority: 10)]);
        log.AddRange([Rx("WARN prefix ", 1)]);
        var pending = log.SetViewFiltersAsync([]);
        log.AddRange([Rx("ERROR" + new string('x', 3000), receiveGap ? 10 : 2)]);
        Assert.True(await pending);
        Assert.DoesNotContain("ERROR", Plain(log.GetVisibleTextSnapshot()));
        AssertColor(log.GetVisibleTextSnapshot(), receiveGap ? "33" : Red);
        log.AddRange([Rx("tail", receiveGap ? 11 : 3)]);
        var lines = log.GetVisibleTextSnapshot().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        AssertColor(lines[^1], Red);
        if (receiveGap) AssertColor(lines[0], "33");
    }

    [Fact]
    public void HexHighlightingRemainsRecordLocal()
    {
        var log = Log();
        log.SetRxDisplayMode(RxDisplayMode.Hex);
        var rule = Rule("AA");
        rule.Mode = LogRuleMatchMode.Hex;
        log.SetHighlightRules([rule]);
        log.AddRange([LogLine.Rx("", [0xAA], 1, isPartialRxSegment: true),
            LogLine.Rx("", [0xBB], 2, isPartialRxSegment: true), LogLine.RxPartialTerminator(3)]);
        Assert.Equal("\u001b[31mAA\u001b[0m BB" + Environment.NewLine, log.GetVisibleTextSnapshot());
    }

    [Theory]
    [InlineData(8192, 128)]
    [InlineData(1024, 1024)]
    public void ContinuousLineAndMemoryLimits_DoNotLoseAnEvictedWinningRule(int byteLimit, int lineLimit)
    {
        var policy = new LogViewMemoryPolicy(RetainedBytes: byteLimit, VisibleCharacters: 8192, ContinuousLineCharacters: lineLimit);
        var log = new LogViewModel(100, policy);
        log.SetShowTimestampInLogView(false);
        log.SetShowRxTxDirectionPrefixInLogView(false);
        log.SetHighlightRules([Rule("FAULT")]);
        log.AddRange([Rx("FAULT" + new string('x', 35), 1)]);
        for (var sequence = 2; sequence <= 500; sequence++)
        {
            log.AddRange([Rx(new string('x', 40), sequence)]);
            Assert.InRange(log.RetainedMemoryBytes, 0, policy.RetainedBytes);
            Assert.InRange(log.VisibleCharacterCount, 0, policy.VisibleCharacters);
            AssertColor(log.GetVisibleTextSnapshot(), Red);
        }
        Assert.DoesNotContain("FAULT", Plain(log.GetVisibleTextSnapshot()));
        var before = log.GetVisibleTextSnapshot();
        log.RefreshVisibleFormatting();
        Assert.Equal(Plain(before).Replace(Environment.NewLine, ""), Plain(log.GetVisibleTextSnapshot()).Replace(Environment.NewLine, ""));
        AssertColor(log.GetVisibleTextSnapshot(), Red);
    }

    [Fact]
    public void RawAnsiAndControlBytesRemainEscapedWhenTheWholeLineIsRecolored()
    {
        var log = Log();
        log.SetHighlightRules([Rule("FAULT")]);
        log.AddRange([Rx("[INFO] \u001b[32m\0\t", 1), LogLine.Rx("FAULT", sequenceNumber: 2)]);
        AssertColor(log.GetVisibleTextSnapshot(), Red);
        Assert.Equal("[INFO] \\x1B[32m\\x00\tFAULT" + Environment.NewLine, Plain(log.GetVisibleTextSnapshot()));
    }

    private static LogViewModel Log()
    {
        var log = new LogViewModel(100);
        log.SetShowTimestampInLogView(false);
        log.SetShowRxTxDirectionPrefixInLogView(false);
        return log;
    }

    private static LogLine Rx(string text, long sequence) => LogLine.Rx(text, Encoding.UTF8.GetBytes(text), sequence, isPartialRxSegment: true);
    private static HighlightRule Rule(string keyword, string color = "Red", int priority = 0) => new()
        { Keyword = keyword, ForegroundColor = color, Priority = priority };

    private static string Plain(string text) => Regex.Replace(text, "\u001b\\[[0-9;]*m", "");

    private static void AssertColor(string text, string expected)
    {
        var color = "0";
        var offset = 0;
        foreach (Match match in Regex.Matches(text, "\u001b\\[([0-9;]*)m"))
        {
            if (text[offset..match.Index].Trim('\r', '\n').Length > 0) Assert.Equal(expected, color);
            color = match.Groups[1].Value;
            offset = match.Index + match.Length;
        }
        if (text[offset..].Trim('\r', '\n').Length > 0) Assert.Equal(expected, color);
    }
}
