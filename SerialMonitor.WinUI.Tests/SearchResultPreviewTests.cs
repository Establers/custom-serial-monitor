using System.Text;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class SearchResultPreviewTests
{
    [Fact]
    public void MatchBeyondNarrowRowWidth_IsMovedNearStartEvenForModerateLengthMessages()
    {
        var message = new string('x', 100) + " ERROR";
        var preview = SearchResultPreview.Create(message,
            new VisibleLogSearchMatcher("ERROR", new VisibleLogSearchOptions()), default);
        Assert.StartsWith("…", preview.Text);
        Assert.InRange(preview.Text.IndexOf("ERROR", StringComparison.Ordinal), 0, 13);
        Assert.Equal("ERROR", Assert.Single(preview.Segments.Where(segment => segment.IsMatch)).Text);
    }

    [Theory]
    [InlineData("ERROR", false)]
    [InlineData("(?<=prefix )ERROR", true)]
    [InlineData("ERROR$", true)]
    public void LateMatch_IsVisibleAndHighlightedWithoutChangingOriginalCoordinates(string query, bool regex)
    {
        var payload = new string('x', 4_000) + " prefix ERROR";
        var row = VisibleSearchResultParser.Create(1, 0, 42, 4_008, 1, "RX < " + payload,
            LogDirection.Rx, searchText: query, searchOptions: new VisibleLogSearchOptions(UseRegularExpression: regex),
            payloadStart: 5);
        Assert.StartsWith("…", row.MessagePreview);
        Assert.Contains("ERROR", row.MessagePreview);
        Assert.Equal("ERROR", Assert.Single(row.MessageSegments.Where(segment => segment.IsMatch)).Text);
        Assert.InRange(row.MessagePreview.Length, 1, 162);
        Assert.Equal(payload, row.MessageText);
        Assert.Equal("RX < " + payload, row.FullText);
        Assert.Equal(4_008, row.PayloadOffset);
        Assert.Equal(row.MessagePreview, string.Concat(row.MessageSegments.Select(segment => segment.Text)));
    }

    [Fact]
    public void AnchoredLongMatch_IsClippedAndHighlightedWithinBoundedPreview()
    {
        var message = "ERROR " + new string('x', 5_000);
        var preview = SearchResultPreview.Create(message,
            new VisibleLogSearchMatcher("^ERROR.*$", new VisibleLogSearchOptions(UseRegularExpression: true)), default);
        Assert.EndsWith("…", preview.Text);
        Assert.Equal(160, Assert.Single(preview.Segments.Where(segment => segment.IsMatch)).Text.Length);
        Assert.InRange(preview.Text.Length, 1, 162);
    }

    [Theory]
    [InlineData(199, 11)]
    [InlineData(100, 158)]
    public void CroppingEmoji_DoesNotSplitSurrogatePairs(int prefixLength, int padding)
    {
        var message = new string('x', prefixLength) + "😀" + new string('y', padding) + "ERROR" + new string('z', 200);
        var preview = SearchResultPreview.Create(message,
            new VisibleLogSearchMatcher("ERROR", new VisibleLogSearchOptions()), default);
        var encoding = new UnicodeEncoding(false, false, true);
        Assert.Equal(preview.Text, encoding.GetString(encoding.GetBytes(preview.Text)));
        Assert.Contains("ERROR", preview.Text);
    }

    [Fact]
    public void CanceledPreview_StopsBeforeMatching()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SearchResultPreview.Create("ERROR",
            new VisibleLogSearchMatcher("ERROR", new VisibleLogSearchOptions()), cancellation.Token));
    }
}
