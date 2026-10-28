using System.Globalization;
using System.Text.RegularExpressions;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogNavigationTimestampVisibilityTests
{
    [Theory]
    [InlineData(RxDisplayMode.Terminal)]
    [InlineData(RxDisplayMode.Hex)]
    public void HiddenTimestampKeepsExactNavigationTimesInLiveAndRestoredLogs(RxDisplayMode mode)
    {
        var first = new DateTimeOffset(2026, 10, 28, 14, 30, 10, 123, TimeSpan.FromHours(9));
        var second = first.AddMilliseconds(1234);
        var model = new LogViewModel(100);
        model.SetRxDisplayMode(mode);
        model.SetShowTimestampInLogView(false);
        var live = string.Empty;
        model.TextBatchAppended += (_, batch) => live += batch.AppendedText;

        model.AddRange([
            new LogLine(first, LogDirection.Rx, "sample-one", "sample-one"u8.ToArray()),
            new LogLine(second, LogDirection.Rx, "sample-two", "sample-two"u8.ToArray())
        ]);

        Assert.DoesNotContain("2026-10-28", model.GetVisibleTextSnapshot());
        var expected = new[] { first.ToUnixTimeMilliseconds(), second.ToUnixTimeMilliseconds() };
        Assert.Equal(expected, ReadMetadataTimes(live));
        Assert.Equal(expected, ReadMetadataTimes(model.GetXtermTextSnapshot()));

        // Changing the visible format rebuilds rows but must keep the original
        // reception times, even when no timestamp characters are drawn.
        model.SetTimestampDisplayFormat(TimestampDisplayFormat.TimeSeconds);
        Assert.DoesNotContain("14:30:", model.GetVisibleTextSnapshot());
        Assert.Equal(expected, ReadMetadataTimes(model.GetXtermTextSnapshot()));
    }

    [Fact]
    public void MixedTimestampVisibilityDoesNotDiscardNavigationMetadata()
    {
        var start = new DateTimeOffset(2026, 10, 28, 14, 30, 10, TimeSpan.FromHours(9));
        var model = new LogViewModel(100);
        model.AddRange([new LogLine(start, LogDirection.Rx, "visible")]);
        model.SetShowTimestampInLogView(false);
        model.AddRange([new LogLine(start.AddMinutes(1), LogDirection.Rx, "hidden")]);

        Assert.Equal(new[] { start.ToUnixTimeMilliseconds(), start.AddMinutes(1).ToUnixTimeMilliseconds() },
            ReadMetadataTimes(model.GetXtermTextSnapshot()));
    }

    private static long[] ReadMetadataTimes(string text) =>
        Regex.Matches(text, "\u001b\\]777;[0-9]+,(-?[0-9]+)\u0007")
            .Select(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
}
