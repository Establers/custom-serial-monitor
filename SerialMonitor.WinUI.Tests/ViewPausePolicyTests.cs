using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class ViewPausePolicyTests
{
    [Theory]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    public void ShouldEnqueueFileLog_SeparatesLogAndPausePolicies(
        bool fileLoggingEnabled,
        bool viewPaused,
        bool fileLoggingWhileViewPaused,
        bool expected)
    {
        Assert.Equal(
            expected,
            ViewPausePolicy.ShouldEnqueueFileLog(
                fileLoggingEnabled,
                viewPaused,
                fileLoggingWhileViewPaused));
    }

    [Fact]
    public void CreateResumeSummary_ReportsDisplayAndFileOmissions()
    {
        var summary = ViewPausePolicy.CreateResumeSummary(
            omitted: 12_345,
            fileSkipped: 234,
            duration: TimeSpan.FromSeconds(125));

        Assert.Contains("PS 12,345", summary);
        Assert.Contains("00:02:05", summary);
        Assert.Contains("234 records also omitted from file", summary);
    }

    [Fact]
    public void CreateResumeSummary_DoesNotWrapDurationsAfterOneDay()
    {
        var summary = ViewPausePolicy.CreateResumeSummary(
            omitted: 1,
            fileSkipped: 0,
            duration: TimeSpan.FromHours(49) + TimeSpan.FromMinutes(2));

        Assert.Contains("49:02:00", summary);
    }
}
