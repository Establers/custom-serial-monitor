using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class SearchResultMatchSegmentResolverTests
{
    [Fact]
    public void Resolve_HighlightsEverySearchTextMatch()
    {
        var segments = SearchResultMatchSegmentResolver.Resolve(
            "READY then READY",
            "READY",
            StringComparison.Ordinal);

        Assert.Equal(
            ["READY", "READY"],
            segments.Where(segment => segment.IsMatch).Select(segment => segment.Text));
        Assert.Equal("READY then READY", string.Concat(segments.Select(segment => segment.Text)));
    }

    [Fact]
    public void Resolve_UsesSearchCaseSensitivity()
    {
        var sensitive = SearchResultMatchSegmentResolver.Resolve(
            "error ERROR Error",
            "ERROR",
            StringComparison.Ordinal);
        var insensitive = SearchResultMatchSegmentResolver.Resolve(
            "error ERROR Error",
            "ERROR",
            StringComparison.OrdinalIgnoreCase);

        Assert.Single(sensitive.Where(segment => segment.IsMatch));
        Assert.Equal(3, insensitive.Count(segment => segment.IsMatch));
    }

    [Fact]
    public void Resolve_HighlightsSearchTextInHexDisplayWithoutRuleParsing()
    {
        var segments = SearchResultMatchSegmentResolver.Resolve(
            "AA BB 0A AA BB",
            "AA BB",
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            ["AA BB", "AA BB"],
            segments.Where(segment => segment.IsMatch).Select(segment => segment.Text));
    }

    [Fact]
    public void Resolve_LeavesColorsUnsetForDefaultYellowFallback()
    {
        var segments = SearchResultMatchSegmentResolver.Resolve(
            "READY",
            "READY",
            StringComparison.Ordinal);

        var match = Assert.Single(segments.Where(segment => segment.IsMatch));
        Assert.Null(match.ForegroundColor);
        Assert.Null(match.BackgroundColor);
    }

    [Theory]
    [InlineData("000012 INFO mock serial sample", "INFO")]
    [InlineData("000012 INFO mock serial sample", "s")]
    public void Resolve_SegmentsReconstructOriginalSpacing(
        string message,
        string searchText)
    {
        var segments = SearchResultMatchSegmentResolver.Resolve(
            message,
            searchText,
            StringComparison.OrdinalIgnoreCase);

        var renderedText = string.Concat(segments.Select(segment => segment.Text));
        Assert.Equal(message, renderedText);
        Assert.Equal(
            message.Count(character => character == ' '),
            renderedText.Count(character => character == ' '));
    }
}
