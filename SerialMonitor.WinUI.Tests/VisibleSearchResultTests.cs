using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class VisibleSearchResultTests
{
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
        var result = VisibleSearchResultParser.Create(1, 2, 3, fullText);

        Assert.Equal(expectedTime, result.TimeText);
        Assert.Equal(expectedDirection, result.DirectionText);
        Assert.Equal(expectedMessage, result.MessagePreview);
        Assert.Equal(fullText, result.FullText);
    }
}
