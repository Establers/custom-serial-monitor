using SerialMonitor.WinUI.Models;
using System.Text.Json;

namespace SerialMonitor.WinUI.Tests;

public sealed class UiSettingsTests
{
    [Fact]
    public void Clone_PreservesFileLoggingWhileViewPaused()
    {
        var settings = new UiSettings
        {
            FileLoggingWhileViewPaused = false
        };

        var clone = settings.Clone();

        Assert.False(clone.FileLoggingWhileViewPaused);
    }

    [Fact]
    public void Default_KeepsFileLoggingEnabledWhileViewPaused()
    {
        Assert.True(new UiSettings().FileLoggingWhileViewPaused);
    }

    [Fact]
    public void LegacyJsonWithoutPauseSetting_DefaultsToKeepingFileLoggingEnabled()
    {
        var settings = JsonSerializer.Deserialize<UiSettings>("{}");

        Assert.NotNull(settings);
        Assert.True(settings.FileLoggingWhileViewPaused);
    }

    [Fact]
    public void AutoReconnect_DefaultsEnabledAndClonePreservesChoice()
    {
        var defaults = JsonSerializer.Deserialize<UiSettings>("{}");
        var disabled = new UiSettings { AutoReconnectEnabled = false };

        Assert.NotNull(defaults);
        Assert.True(defaults.AutoReconnectEnabled);
        Assert.False(disabled.Clone().AutoReconnectEnabled);
    }

    [Fact]
    public void Clone_PreservesXtermFontSettings()
    {
        var settings = new UiSettings
        {
            XtermFontFamily = XtermFontFamily.D2Coding,
            XtermFontSize = 15
        };

        var clone = settings.Clone();

        Assert.Equal(XtermFontFamily.D2Coding, clone.XtermFontFamily);
        Assert.Equal(15, clone.XtermFontSize);
    }

    [Fact]
    public void LegacyJsonWithoutXtermFontSettings_UsesCurrentDefaults()
    {
        var settings = JsonSerializer.Deserialize<UiSettings>("{}");

        Assert.NotNull(settings);
        Assert.Equal(XtermFontFamily.Consolas, settings.XtermFontFamily);
        Assert.Equal(UiSettings.DefaultXtermFontSize, settings.XtermFontSize);
    }

    [Fact]
    public void SearchOptions_DefaultOffAndClonePreservesEnabledChoices()
    {
        var defaults = JsonSerializer.Deserialize<UiSettings>("{}");
        var enabled = new UiSettings
        {
            SearchCaseSensitive = true,
            SearchWholeWord = true,
            SearchUseRegularExpression = true
        }.Clone();

        Assert.NotNull(defaults);
        Assert.False(defaults.SearchCaseSensitive);
        Assert.False(defaults.SearchWholeWord);
        Assert.False(defaults.SearchUseRegularExpression);
        Assert.True(enabled.SearchCaseSensitive);
        Assert.True(enabled.SearchWholeWord);
        Assert.True(enabled.SearchUseRegularExpression);
    }
}
