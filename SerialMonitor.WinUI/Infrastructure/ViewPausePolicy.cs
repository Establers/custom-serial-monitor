using System.Globalization;

namespace SerialMonitor.WinUI.Infrastructure;

internal static class ViewPausePolicy
{
    public static bool ShouldEnqueueFileLog(
        bool fileLoggingEnabled,
        bool viewPaused,
        bool fileLoggingWhileViewPaused)
    {
        return fileLoggingEnabled && (!viewPaused || fileLoggingWhileViewPaused);
    }

    public static string CreateResumeSummary(long omitted, long fileSkipped, TimeSpan duration)
    {
        var normalizedDuration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        var durationText = string.Create(
            CultureInfo.InvariantCulture,
            $"{(long)normalizedDuration.TotalHours:00}:{normalizedDuration.Minutes:00}:{normalizedDuration.Seconds:00}");
        var omittedText = omitted.ToString("N0", CultureInfo.InvariantCulture);
        var fileText = fileSkipped > 0
            ? $"; {fileSkipped.ToString("N0", CultureInfo.InvariantCulture)} records also omitted from file by pause option"
            : "; no records skipped by the pause file option";
        return $"VIEW RESUMED - PS {omittedText} during {durationText}{fileText}";
    }
}
