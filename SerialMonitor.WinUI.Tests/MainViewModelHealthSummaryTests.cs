using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class MainViewModelHealthSummaryTests
{
    [Fact]
    public void CreateHealthReasonSummary_NoReasons_ReturnsNoIssuesText()
    {
        var summary = MainViewModel.CreateHealthReasonSummary(
            [],
            maxVisibleReasons: 5,
            "No issues",
            "+{0} more");

        Assert.Equal("No issues", summary);
    }

    [Fact]
    public void CreateHealthReasonSummary_WithinLimit_ReturnsEveryReason()
    {
        string[] reasons = ["one", "two", "three"];

        var summary = MainViewModel.CreateHealthReasonSummary(
            reasons,
            maxVisibleReasons: 5,
            "No issues",
            "+{0} more");

        Assert.Equal(string.Join(Environment.NewLine, reasons), summary);
    }

    [Fact]
    public void CreateHealthReasonSummary_OverLimit_TruncatesAndReportsHiddenCount()
    {
        string[] reasons = ["one", "two", "three", "four", "five", "six", "seven"];

        var summary = MainViewModel.CreateHealthReasonSummary(
            reasons,
            maxVisibleReasons: 5,
            "No issues",
            "+{0} more");

        Assert.Equal(
            string.Join(Environment.NewLine, reasons.Take(5).Append("+2 more")),
            summary);
        Assert.DoesNotContain("six", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("seven", summary, StringComparison.Ordinal);
    }
}
