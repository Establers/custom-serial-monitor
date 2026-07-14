using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class TimestampDisplayFormatTests
{
    private static readonly DateTime LocalDateTime =
        new(2026, 7, 14, 23, 8, 41, 390, DateTimeKind.Unspecified);

    private static readonly DateTimeOffset Timestamp =
        new(LocalDateTime, TimeZoneInfo.Local.GetUtcOffset(LocalDateTime));

    [Theory]
    [InlineData(TimestampDisplayFormat.DateTimeMilliseconds, "[2026-07-14 23:08:41.390] RX < payload")]
    [InlineData(TimestampDisplayFormat.DateTimeSeconds, "[2026-07-14 23:08:41] RX < payload")]
    [InlineData(TimestampDisplayFormat.TimeMilliseconds, "[23:08:41.390] RX < payload")]
    [InlineData(TimestampDisplayFormat.TimeSeconds, "[23:08:41] RX < payload")]
    public void LogLine_Format_UsesSelectedTimestampFormat(
        TimestampDisplayFormat format,
        string expected)
    {
        var line = new LogLine(Timestamp, LogDirection.Rx, "payload");

        Assert.Equal(expected, line.Format(format));
        if (format == TimestampDisplayFormat.DateTimeMilliseconds)
        {
            Assert.Equal(expected, line.Formatted);
        }
    }

    [Fact]
    public void ChangingFormat_RebuildsRetainedVisibleLines()
    {
        var viewModel = new LogViewModel(capacity: 100);
        viewModel.AddRange(new[] { new LogLine(Timestamp, LogDirection.Rx, "payload") });
        var rebuiltCount = 0;
        viewModel.TextRebuilt += (_, _) => rebuiltCount++;

        viewModel.SetTimestampDisplayFormat(TimestampDisplayFormat.TimeSeconds);

        Assert.Contains("[23:08:41] RX < payload", viewModel.GetVisibleTextSnapshot(), StringComparison.Ordinal);
        Assert.DoesNotContain("2026-07-14", viewModel.GetVisibleTextSnapshot(), StringComparison.Ordinal);
        Assert.Equal(1, rebuiltCount);
    }

    [Fact]
    public void UiSettingsClone_PreservesTimestampDisplayFormat()
    {
        var settings = new UiSettings
        {
            TimestampDisplayFormat = TimestampDisplayFormat.TimeMilliseconds
        };

        Assert.Equal(TimestampDisplayFormat.TimeMilliseconds, settings.Clone().TimestampDisplayFormat);
    }
}
