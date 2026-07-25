using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogViewModelBackgroundContrastTests
{
    [Fact]
    public void BackgroundOnlyRule_UsesThemeContrastForegroundAndExtendedBackground()
    {
        var viewModel = CreateViewModel(new HighlightRule
        {
            Name = "Magenta background",
            Keyword = "MATCH",
            BackgroundColor = "Magenta"
        });

        viewModel.AddRange(new[] { LogLine.Rx("MATCH") });

        Assert.Contains(
            "\u001b[38;5;16;48;5;22m",
            viewModel.GetVisibleTextSnapshot(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BackgroundRule_WithExplicitForeground_PreservesForegroundAndUsesExtendedBackground()
    {
        var viewModel = CreateViewModel(new HighlightRule
        {
            Name = "Red on magenta",
            Keyword = "MATCH",
            ForegroundColor = "Red",
            BackgroundColor = "Magenta"
        });

        viewModel.AddRange(new[] { LogLine.Rx("MATCH") });
        var snapshot = viewModel.GetVisibleTextSnapshot();

        Assert.Contains("\u001b[31;48;5;22m", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("38;5;16", snapshot, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("None")]
    [InlineData(" ")]
    [InlineData(null)]
    public void NoEffectiveBackground_PreservesExplicitForeground(string? backgroundColor)
    {
        var viewModel = CreateViewModel(new HighlightRule
        {
            Name = "Red foreground",
            Keyword = "MATCH",
            ForegroundColor = "Red",
            BackgroundColor = backgroundColor
        });

        viewModel.AddRange(new[] { LogLine.Rx("MATCH") });
        var snapshot = viewModel.GetVisibleTextSnapshot();

        Assert.Contains("\u001b[31m", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("48;5;", snapshot, StringComparison.Ordinal);
    }

    private static LogViewModel CreateViewModel(HighlightRule rule)
    {
        var viewModel = new LogViewModel(capacity: 100);
        viewModel.SetHighlightRules(new[] { rule });
        return viewModel;
    }
}
