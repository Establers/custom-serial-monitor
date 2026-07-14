using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogRuleMatcherModeTests
{
    [Fact]
    public void HexPattern_Over256Bytes_IsAccepted()
    {
        var keyword = string.Join(' ', Enumerable.Repeat("AA", 257));

        Assert.True(LogRuleMatcher.TryParseHexPattern(keyword, out var bytes, out var error));
        Assert.Equal(257, bytes.Length);
        Assert.Empty(error);
    }

    private static readonly byte[] ErrorBytes = "ERROR"u8.ToArray();

    [Fact]
    public void HexMode_AcceptsOnlyHexEventAndHighlightRules()
    {
        var line = LogLine.Rx(
            "ERROR",
            ErrorBytes,
            displayText: "45 52 52 4F 52",
            contentMode: LogRuleMatchMode.Hex);

        Assert.False(LogRuleMatcher.IsMatch(line, TerminalEventRule(), LogRuleMatchMode.Hex, out var eventError));
        Assert.Null(eventError);
        Assert.True(LogRuleMatcher.IsMatch(line, HexEventRule(), LogRuleMatchMode.Hex, out eventError));
        Assert.Null(eventError);

        Assert.False(LogRuleMatcher.IsMatch(line, TerminalHighlightRule(), LogRuleMatchMode.Hex, out var highlightError));
        Assert.Null(highlightError);
        Assert.True(LogRuleMatcher.IsMatch(line, HexHighlightRule(), LogRuleMatchMode.Hex, out highlightError));
        Assert.Null(highlightError);
    }

    [Fact]
    public void TerminalMode_AcceptsOnlyTerminalEventAndHighlightRules()
    {
        var line = LogLine.Rx("ERROR", ErrorBytes, contentMode: LogRuleMatchMode.Terminal);

        Assert.True(LogRuleMatcher.IsMatch(line, TerminalEventRule(), LogRuleMatchMode.Terminal, out var eventError));
        Assert.Null(eventError);
        Assert.False(LogRuleMatcher.IsMatch(line, HexEventRule(), LogRuleMatchMode.Terminal, out eventError));
        Assert.Null(eventError);

        Assert.True(LogRuleMatcher.IsMatch(line, TerminalHighlightRule(), LogRuleMatchMode.Terminal, out var highlightError));
        Assert.Null(highlightError);
        Assert.False(LogRuleMatcher.IsMatch(line, HexHighlightRule(), LogRuleMatchMode.Terminal, out highlightError));
        Assert.Null(highlightError);
    }

    [Fact]
    public void CompiledRules_UseTheSameModeExclusivity()
    {
        var hexLine = LogLine.Rx(
            "ERROR",
            ErrorBytes,
            displayText: "45 52 52 4F 52",
            contentMode: LogRuleMatchMode.Hex);
        var terminalLine = LogLine.Rx("ERROR", ErrorBytes, contentMode: LogRuleMatchMode.Terminal);
        var terminalEvent = LogRuleMatcher.Compile(TerminalEventRule());
        var hexEvent = LogRuleMatcher.Compile(HexEventRule());
        var terminalHighlight = LogRuleMatcher.Compile(TerminalHighlightRule());
        var hexHighlight = LogRuleMatcher.Compile(HexHighlightRule());

        Assert.False(LogRuleMatcher.IsMatch(hexLine, terminalEvent, LogRuleMatchMode.Hex, out _));
        Assert.True(LogRuleMatcher.IsMatch(hexLine, hexEvent, LogRuleMatchMode.Hex, out _));
        Assert.False(LogRuleMatcher.IsMatch(hexLine, terminalHighlight, LogRuleMatchMode.Hex, out _));
        Assert.True(LogRuleMatcher.IsMatch(hexLine, hexHighlight, LogRuleMatchMode.Hex, out _));

        Assert.True(LogRuleMatcher.IsMatch(terminalLine, terminalEvent, LogRuleMatchMode.Terminal, out _));
        Assert.False(LogRuleMatcher.IsMatch(terminalLine, hexEvent, LogRuleMatchMode.Terminal, out _));
        Assert.True(LogRuleMatcher.IsMatch(terminalLine, terminalHighlight, LogRuleMatchMode.Terminal, out _));
        Assert.False(LogRuleMatcher.IsMatch(terminalLine, hexHighlight, LogRuleMatchMode.Terminal, out _));
    }

    [Fact]
    public void CompiledHighlightRule_CanUseCurrentRxViewModeForRetainedLines()
    {
        var retainedTerminalLine = LogLine.Rx(
            "ERROR",
            ErrorBytes,
            contentMode: LogRuleMatchMode.Terminal);
        var hexHighlight = LogRuleMatcher.Compile(HexHighlightRule());

        Assert.False(LogRuleMatcher.IsMatch(
            retainedTerminalLine,
            hexHighlight,
            LogRuleMatchMode.Terminal,
            out _));
        Assert.True(LogRuleMatcher.IsMatch(
            retainedTerminalLine,
            hexHighlight,
            LogRuleMatchMode.Hex,
            out var error));
        Assert.Null(error);
    }

    [Fact]
    public void CompiledEventRule_UsesCurrentModeInsteadOfLineArrivalMode()
    {
        var lineCreatedInTerminalMode = LogLine.Rx(
            "ERROR",
            ErrorBytes,
            contentMode: LogRuleMatchMode.Terminal);
        var terminalEvent = LogRuleMatcher.Compile(TerminalEventRule());
        var hexEvent = LogRuleMatcher.Compile(HexEventRule());

        Assert.False(LogRuleMatcher.IsMatch(
            lineCreatedInTerminalMode,
            terminalEvent,
            LogRuleMatchMode.Hex,
            out _));
        Assert.True(LogRuleMatcher.IsMatch(
            lineCreatedInTerminalMode,
            hexEvent,
            LogRuleMatchMode.Hex,
            out var error));
        Assert.Null(error);
    }

    private static EventRule TerminalEventRule() => new()
    {
        Enabled = true,
        Keyword = "ERROR",
        Mode = LogRuleMatchMode.Terminal
    };

    private static EventRule HexEventRule() => new()
    {
        Enabled = true,
        Keyword = "45 52 52 4F 52",
        Mode = LogRuleMatchMode.Hex
    };

    private static HighlightRule TerminalHighlightRule() => new()
    {
        Enabled = true,
        Keyword = "ERROR",
        Mode = LogRuleMatchMode.Terminal
    };

    private static HighlightRule HexHighlightRule() => new()
    {
        Enabled = true,
        Keyword = "45 52 52 4F 52",
        Mode = LogRuleMatchMode.Hex
    };
}
