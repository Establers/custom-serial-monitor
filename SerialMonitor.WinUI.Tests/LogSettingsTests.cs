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
}
