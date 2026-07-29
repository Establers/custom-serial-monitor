using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Tests;

public sealed class EventMatchRangeResolverTests
{
    [Fact]
    public void TerminalRule_ReturnsEveryNonOverlappingMatchWithConfiguredCaseComparison()
    {
        var line = LogLine.Rx("Error ERROR error");
        var insensitiveRule = new EventRule
        {
            Keyword = "ERROR",
            Mode = LogRuleMatchMode.Terminal,
            CaseSensitive = false
        };
        var sensitiveRule = new EventRule
        {
            Keyword = "ERROR",
            Mode = LogRuleMatchMode.Terminal,
            CaseSensitive = true
        };

        Assert.Equal(
            [new TextMatchRange(0, 5), new TextMatchRange(6, 5), new TextMatchRange(12, 5)],
            EventMatchRangeResolver.Resolve(line, insensitiveRule));
        Assert.Equal(
            [new TextMatchRange(6, 5)],
            EventMatchRangeResolver.Resolve(line, sensitiveRule));
    }

    [Fact]
    public void HexRule_MapsMatchedBytesToSpacedHexDisplayOffsets()
    {
        var line = LogLine.Rx(
            "ignored decoded text",
            [0x01, 0xAA, 0xBB, 0x02, 0xAA, 0xBB],
            displayText: "01 AA BB 02 AA BB",
            contentMode: LogRuleMatchMode.Hex);
        var rule = new EventRule
        {
            Keyword = "AA BB",
            Mode = LogRuleMatchMode.Hex
        };

        Assert.Equal(
            [new TextMatchRange(3, 5), new TextMatchRange(12, 5)],
            EventMatchRangeResolver.Resolve(line, rule));
    }

    [Fact]
    public void DetectedEvent_BuildsOnlyMatchedSegmentsWithRuleColors()
    {
        var detectedEvent = new DetectedEvent(
            DateTimeOffset.Now,
            "fault",
            "FAULT",
            LogDirection.Rx,
            "before FAULT after",
            matchRanges: [new TextMatchRange(7, 5)],
            matchForegroundColor: "Red",
            matchBackgroundColor: "Yellow");

        Assert.Collection(
            detectedEvent.MessageSegments,
            segment =>
            {
                Assert.Equal("before ", segment.Text);
                Assert.False(segment.IsMatch);
            },
            segment =>
            {
                Assert.Equal("FAULT", segment.Text);
                Assert.True(segment.IsMatch);
                Assert.Equal("Red", segment.ForegroundColor);
                Assert.Equal("Yellow", segment.BackgroundColor);
            },
            segment =>
            {
                Assert.Equal(" after", segment.Text);
                Assert.False(segment.IsMatch);
            });
    }
}
