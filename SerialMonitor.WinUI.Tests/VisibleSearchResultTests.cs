using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class VisibleSearchResultTests
{
    [Fact]
    public void PrefixFreeRxResult_PreservesStructuredDirection()
    {
        var result = VisibleSearchResultParser.Create(
            1,
            0,
            42,
            0,
            1,
            "[16:30:55.091] INFO mock serial sample",
            LogDirection.Rx);

        Assert.Equal("16:30:55.091", result.TimeText);
        Assert.Equal("RX", result.DirectionText);
        Assert.Equal("INFO mock serial sample", result.MessagePreview);
    }

    [Theory]
    [InlineData(
        "[16:30:55.091] RX < INFO mock serial sample",
        "16:30:55.091",
        "RX",
        "INFO mock serial sample")]
    [InlineData(
        "[16:30:55] RX < INFO time seconds",
        "16:30:55",
        "RX",
        "INFO time seconds")]
    [InlineData(
        "[2026-07-26 16:30:55.091] TX > 41 42",
        "16:30:55.091",
        "TX",
        "41 42")]
    [InlineData(
        "[2026-07-26 16:30:55] TX > date seconds",
        "16:30:55",
        "TX",
        "date seconds")]
    [InlineData(
        "RX < INFO without timestamp",
        "",
        "RX",
        "INFO without timestamp")]
    [InlineData("MARK > checkpoint", "", "MARK", "checkpoint")]
    [InlineData("SYS connected", "", "SYS", "connected")]
    [InlineData(
        "[16:30:55.091 RX < missing bracket",
        "",
        "",
        "[16:30:55.091 RX < missing bracket")]
    [InlineData(
        "[garbage 16:30:55] RX < invalid date prefix",
        "",
        "",
        "[garbage 16:30:55] RX < invalid date prefix")]
    public void CreateVisibleSearchResult_SplitsSupportedLineFormats(
        string fullText,
        string expectedTime,
        string expectedDirection,
        string expectedMessage)
    {
        var result = VisibleSearchResultParser.Create(1, 2, 3, 0, 1, fullText);

        Assert.Equal(expectedTime, result.TimeText);
        Assert.Equal(expectedDirection, result.DirectionText);
        Assert.Equal(expectedMessage, result.MessagePreview);
        Assert.Equal(fullText, result.FullText);
    }

    [Fact]
    public void MultipleMatchesOnOneLine_CreateOneRowWithMatchCount()
    {
        var result = VisibleSearchResultParser.Create(
            11,
            4,
            42,
            7,
            3,
            "RX < READY READY READY",
            LogDirection.Rx);

        Assert.Equal(11, result.MatchIndex);
        Assert.Equal(7, result.PayloadOffset);
        Assert.Equal(3, result.MatchCountInLine);
    }

    [Theory]
    [InlineData("SYS status")]
    [InlineData("SYSTEM ready")]
    [InlineData("RX < payload")]
    [InlineData("[16:30:55.091] payload")]
    [InlineData("  READY")]
    [InlineData("\tREADY")]
    public void KnownPayloadBoundary_PreservesDeviceTextWithoutParsingItAsMetadata(string payload)
    {
        var result = VisibleSearchResultParser.Create(
            1, 0, 1, 0, 1, payload, LogDirection.Rx, payloadStart: 0);

        Assert.Equal(string.Empty, result.TimeText);
        Assert.Equal("RX", result.DirectionText);
        Assert.Equal(payload, result.MessagePreview);
    }

    [Theory]
    [InlineData("[16:30:55.091] ")]
    [InlineData("[16:30:55.091] RX < ")]
    public void KnownPayloadBoundary_PreservesLeadingSpacesAndMatchingCoordinates(string prefix)
    {
        const string payload = "  SYS READY READY";
        var result = VisibleSearchResultParser.Create(
            1, 0, 1, 2, 2, prefix + payload, LogDirection.Rx,
            searchText: "SYS", payloadStart: prefix.Length);

        Assert.Equal("16:30:55.091", result.TimeText);
        Assert.Equal("RX", result.DirectionText);
        Assert.Equal(payload, result.MessagePreview);
        Assert.Equal("SYS", Assert.Single(result.MessageSegments.Where(segment => segment.IsMatch)).Text);
        Assert.Equal(payload, string.Concat(result.MessageSegments.Select(segment => segment.Text)));
    }

    [Fact]
    public void LegacyParser_DoesNotMistakeSystemLikePayloadForDirection()
    {
        var result = VisibleSearchResultParser.Create(1, 0, 1, 0, 1, "SYSTEM ready");

        Assert.Equal(string.Empty, result.DirectionText);
        Assert.Equal("SYSTEM ready", result.MessagePreview);
    }
}
