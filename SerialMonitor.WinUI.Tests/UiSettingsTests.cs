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
}
