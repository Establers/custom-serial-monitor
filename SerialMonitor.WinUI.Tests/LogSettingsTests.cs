using System.Text.Json;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogSettingsTests
{
    [Fact]
    public void FileLoggingEnabled_IsRuntimeOnly_AndNeverRestoredFromProfileJson()
    {
        var settings = new LogSettings { FileLoggingEnabled = true };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<LogSettings>("""
            { "FileLoggingEnabled": true }
            """);

        Assert.DoesNotContain("FileLoggingEnabled", json, StringComparison.Ordinal);
        Assert.NotNull(restored);
        Assert.False(restored.FileLoggingEnabled);
    }

    [Fact]
    public void Default_SizeRotationThreshold_IsTenMegabytes()
    {
        Assert.Equal(1024L * 1024, LogSettings.BytesPerMegabyte);
        Assert.Equal(10, LogSettings.DefaultSizeRotationMegabytes);
        Assert.Equal(10L * 1024 * 1024, new LogSettings().SizeRotationBytes);
    }
}
