using System.Globalization;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogHistoryExportFileNameTests
{
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
