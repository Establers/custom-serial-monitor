using System.Globalization;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Infrastructure;

internal static class LogHistoryExportFileName
{
    public static string Create(DateTimeOffset firstRecordAt, DateTimeOffset capturedAt, string? windowName = null)
    {
        const string format = "yyyy-MM-dd_HHmmss.fff";
        var start = firstRecordAt.LocalDateTime.ToString(format, CultureInfo.InvariantCulture);
        var end = capturedAt.LocalDateTime.ToString(format, CultureInfo.InvariantCulture);
        return LogFileNamePolicy.AddDefaultPrefix($"{start}_to_{end}_logs.log", windowName);
    }
}
