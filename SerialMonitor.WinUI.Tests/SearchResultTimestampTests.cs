using System.Globalization;
using System.Reflection;
using System.Text;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class SearchResultTimestampTests
{
    [Theory]
    [InlineData(RxDisplayMode.Terminal, false, false)]
    [InlineData(RxDisplayMode.Terminal, false, true)]
    [InlineData(RxDisplayMode.Terminal, true, false)]
    [InlineData(RxDisplayMode.Terminal, true, true)]
    [InlineData(RxDisplayMode.Hex, false, false)]
    [InlineData(RxDisplayMode.Hex, false, true)]
    [InlineData(RxDisplayMode.Hex, true, false)]
    [InlineData(RxDisplayMode.Hex, true, true)]
    public void SearchRows_KeepReceptionTimeIndependentOfDisplayMetadata(
        RxDisplayMode mode, bool showTimestamp, bool showDirection)
    {
        var timestamp = new DateTimeOffset(2026, 10, 28, 14, 32, 10, 123, TimeSpan.FromHours(9));
        const string payload = "[01:02:03.004] READY READY";
        var source = new LogLine(timestamp, LogDirection.Rx, payload, Encoding.UTF8.GetBytes(payload));
        var searchText = mode == RxDisplayMode.Hex ? "52 45 41 44 59" : "READY";

        foreach (var format in Enum.GetValues<TimestampDisplayFormat>())
        {
            var model = new LogViewModel(10);
            model.SetRxDisplayMode(mode);
            model.SetShowTimestampInLogView(showTimestamp);
            model.SetShowRxTxDirectionPrefixInLogView(showDirection);
            model.SetTimestampDisplayFormat(format);
            model.AddRange([source]);

            var line = Assert.Single(model.GetVisibleSearchContentSnapshot());
            Assert.Equal(timestamp.ToUnixTimeMilliseconds(), line.TimestampUnixMilliseconds);
            Assert.True(model.TryGetSearchLine(source, out var resolved));
            Assert.Equal(line, resolved);
            var row = Assert.Single(BuildRows(model, searchText));

            Assert.Equal(LocalTime(timestamp), row.TimeText);
            Assert.Equal("RX", row.DirectionText);
            Assert.Equal(line.PayloadText, row.MessagePreview);
            Assert.Equal(2, row.MatchCountInLine);
            Assert.Equal(2, row.MessageSegments.Count(segment => segment.IsMatch));
            Assert.Equal(line.PayloadText, string.Concat(row.MessageSegments.Select(segment => segment.Text)));
            if (mode == RxDisplayMode.Terminal)
            {
                Assert.Equal(payload, row.MessagePreview);
                Assert.Equal(payload.IndexOf("READY", StringComparison.Ordinal), row.PayloadOffset);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MergedPartialSearchRow_KeepsFirstReceptionTimeAcrossRebuild(bool showDirection)
    {
        var firstTime = new DateTimeOffset(2026, 10, 28, 14, 32, 10, 123, TimeSpan.Zero);
        var first = new LogLine(firstTime, LogDirection.Rx, "REA", isPartialRxSegment: true);
        var tail = new LogLine(firstTime.AddMilliseconds(50), LogDirection.Rx, "DY READY", isPartialRxSegment: true);
        var model = new LogViewModel(10);
        model.SetShowTimestampInLogView(false);
        model.SetShowRxTxDirectionPrefixInLogView(showDirection);
        model.AddRange([first, tail]);

        var before = Assert.Single(BuildRows(model, "READY"));
        Assert.Equal(LocalTime(firstTime), before.TimeText);
        Assert.Equal("READY READY", before.MessagePreview);
        Assert.True(model.TryGetSearchLine(tail, out var resolvedTail));
        Assert.Equal(firstTime.ToUnixTimeMilliseconds(), resolvedTail.TimestampUnixMilliseconds);

        model.SetTimestampDisplayFormat(TimestampDisplayFormat.TimeSeconds);
        model.SetShowRxTxDirectionPrefixInLogView(!showDirection);

        var after = Assert.Single(BuildRows(model, "READY"));
        Assert.Equal(before.LineId, after.LineId);
        Assert.Equal(before.TimeText, after.TimeText);
        Assert.Equal(before.MessagePreview, after.MessagePreview);
        Assert.True(model.TryGetSearchLine(tail, out resolvedTail));
        Assert.Equal(firstTime.ToUnixTimeMilliseconds(), resolvedTail.TimestampUnixMilliseconds);
    }

    [Theory]
    [InlineData(RxDisplayMode.Terminal)]
    [InlineData(RxDisplayMode.Hex)]
    public void CapacityTrim_KeepsSearchTimesAlignedWithSurvivingRows(RxDisplayMode mode)
    {
        // Epoch zero is a valid reception time, distinct from absent metadata.
        var timestamp = DateTimeOffset.UnixEpoch;
        var sources = Enumerable.Range(0, 3)
            .Select(index => new LogLine(timestamp.AddMilliseconds(index * 123), LogDirection.Rx,
                "READY", "READY"u8.ToArray()))
            .ToArray();
        var model = new LogViewModel(3);
        model.SetRxDisplayMode(mode);
        model.SetShowTimestampInLogView(false);
        model.AddRange(sources);
        var searchText = mode == RxDisplayMode.Hex ? "52 45 41 44 59" : "READY";
        Assert.Equal(LocalTime(timestamp), BuildRows(model, searchText)[0].TimeText);

        model.SetCapacity(2);
        model.SetTimestampDisplayFormat(TimestampDisplayFormat.TimeSeconds);

        var rows = BuildRows(model, searchText);
        Assert.Equal(2, rows.Count);
        Assert.Equal(sources.Skip(1).Select(source => LocalTime(source.Timestamp)), rows.Select(row => row.TimeText));
        Assert.False(model.TryGetSearchLine(sources[0], out _));
        for (var index = 1; index < sources.Length; index++)
        {
            Assert.True(model.TryGetSearchLine(sources[index], out var resolved));
            Assert.Equal(sources[index].Timestamp.ToUnixTimeMilliseconds(), resolved.TimestampUnixMilliseconds);
            Assert.Equal(resolved.LineId, rows[index - 1].LineId);
        }
    }

    private static List<VisibleSearchResult> BuildRows(LogViewModel model, string searchText)
    {
        var snapshot = VisibleLogSearchEngine.Build(model.GetVisibleSearchContentSnapshot(),
            searchText, StringComparison.Ordinal);
        var page = snapshot.GetMatchedLinePage(0, 100);
        // Exercise the production page builder so dropping metadata at any
        // boundary between retained logs and the bound rows fails these tests.
        var build = typeof(MainViewModel).GetMethod("BuildVisibleSearchResults",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(build);
        return Assert.IsType<List<VisibleSearchResult>>(build.Invoke(null, [snapshot, page, CancellationToken.None]));
    }

    private static string LocalTime(DateTimeOffset timestamp) =>
        timestamp.LocalDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
}
