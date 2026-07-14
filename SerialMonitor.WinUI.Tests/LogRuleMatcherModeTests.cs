using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogRuleMatcherModeTests
{
    private static readonly byte[] ErrorBytes = "ERROR"u8.ToArray();

    [Fact]
    public void HexContent_AcceptsOnlyHexEventAndHighlightRules()
    {
        var line = LogLine.Rx(
            "ERROR",
            ErrorBytes,
            displayText: "45 52 52 4F 52",
            contentMode: LogRuleMatchMode.Hex);

        Assert.False(LogRuleMatcher.IsMatch(line, TextEventRule(), out var eventError));
        Assert.Null(eventError);
        Assert.True(LogRuleMatcher.IsMatch(line, HexEventRule(), out eventError));
        Assert.Null(eventError);

        Assert.False(LogRuleMatcher.IsMatch(line, TextHighlightRule(), out var highlightError));
        Assert.Null(highlightError);
        Assert.True(LogRuleMatcher.IsMatch(line, HexHighlightRule(), out highlightError));
        Assert.Null(highlightError);
    }

    [Fact]
    public void TextContent_AcceptsOnlyTextEventAndHighlightRules()
    {
        var line = LogLine.Rx("ERROR", ErrorBytes, contentMode: LogRuleMatchMode.Text);

        Assert.True(LogRuleMatcher.IsMatch(line, TextEventRule(), out var eventError));
        Assert.Null(eventError);
        Assert.False(LogRuleMatcher.IsMatch(line, HexEventRule(), out eventError));
        Assert.Null(eventError);

        Assert.True(LogRuleMatcher.IsMatch(line, TextHighlightRule(), out var highlightError));
        Assert.Null(highlightError);
        Assert.False(LogRuleMatcher.IsMatch(line, HexHighlightRule(), out highlightError));
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
        var textLine = LogLine.Rx("ERROR", ErrorBytes, contentMode: LogRuleMatchMode.Text);
        var textEvent = LogRuleMatcher.Compile(TextEventRule());
        var hexEvent = LogRuleMatcher.Compile(HexEventRule());
        var textHighlight = LogRuleMatcher.Compile(TextHighlightRule());
        var hexHighlight = LogRuleMatcher.Compile(HexHighlightRule());

        Assert.False(LogRuleMatcher.IsMatch(hexLine, textEvent, out _));
        Assert.True(LogRuleMatcher.IsMatch(hexLine, hexEvent, out _));
        Assert.False(LogRuleMatcher.IsMatch(hexLine, textHighlight, out _));
        Assert.True(LogRuleMatcher.IsMatch(hexLine, hexHighlight, out _));

        Assert.True(LogRuleMatcher.IsMatch(textLine, textEvent, out _));
        Assert.False(LogRuleMatcher.IsMatch(textLine, hexEvent, out _));
        Assert.True(LogRuleMatcher.IsMatch(textLine, textHighlight, out _));
        Assert.False(LogRuleMatcher.IsMatch(textLine, hexHighlight, out _));
    }

    private static EventRule TextEventRule() => new()
    {
        Enabled = true,
        Keyword = "ERROR",
        MatchMode = LogRuleMatchMode.Text
    };

    private static EventRule HexEventRule() => new()
    {
        Enabled = true,
        Keyword = "45 52 52 4F 52",
        MatchMode = LogRuleMatchMode.Hex
    };

    private static HighlightRule TextHighlightRule() => new()
    {
        Enabled = true,
        Keyword = "ERROR",
        MatchMode = LogRuleMatchMode.Text
    };

    private static HighlightRule HexHighlightRule() => new()
    {
        Enabled = true,
        Keyword = "45 52 52 4F 52",
        MatchMode = LogRuleMatchMode.Hex
    };
}
