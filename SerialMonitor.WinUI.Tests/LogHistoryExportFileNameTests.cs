using System.Globalization;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogHistoryExportFileNameTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("메인 보드 🙂", "메인 보드 🙂_")]
    [InlineData(" Board A ", "Board A_")]
    [InlineData("Board:A/B\\C?*\"<>|", "Board_A_B_C_______")]
    [InlineData("CON", "CON_")]
    [InlineData("CON.board", "_CON.board_")]
    public void Create_PutsTheSavedWindowNameFirst_UsingAValidFileName(string? windowName, string expectedPrefix)
    {
        var timestamp = new DateTimeOffset(new DateTime(2026, 11, 7, 12, 34, 56, DateTimeKind.Local));

        var name = LogHistoryExportFileName.Create(timestamp, timestamp, windowName);

        Assert.Equal($"{expectedPrefix}2026-11-07_123456.000_to_2026-11-07_123456.000_logs.log", name);
        Assert.True(SerialMonitor.WinUI.Services.LogFileNamePolicy.TryValidate(name, out _, out var error), error);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ko-KR")]
    [InlineData("ar-SA")]
    public void Create_UsesLocalDatesAcrossMidnight_AndWindowsSafeInvariantFormat(string culture)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var start = new DateTimeOffset(new DateTime(2026, 10, 27, 23, 59, 58, 123, DateTimeKind.Local));
            var click = new DateTimeOffset(new DateTime(2026, 10, 28, 0, 1, 2, 456, DateTimeKind.Local));

            // Incoming records can use UTC, while the UI and filename use local time.
            var name = LogHistoryExportFileName.Create(start.ToUniversalTime(), click.ToUniversalTime());

            Assert.Equal("2026-10-27_235958.123_to_2026-10-28_000102.456_logs.log", name);
            Assert.DoesNotContain(name, c => Path.GetInvalidFileNameChars().Contains(c));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
