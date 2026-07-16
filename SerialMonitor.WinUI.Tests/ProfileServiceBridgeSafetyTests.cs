using System.Text.Json;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class ProfileServiceBridgeSafetyTests
{
    [Fact]
    public async Task SaveAndLoad_ForcesFixedEventRetentionValues()
    {
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.UiSettings.MaxVisibleEventCount = 5_000;
        profile.EventContextSettings.BeforeContextLines = 25;
        profile.EventContextSettings.AfterContextLines = 30;
        var path = CreateTemporaryProfilePath();

        try
        {
            await service.SaveAsync(path, profile, CancellationToken.None);
            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.Equal(UiSettings.FixedMaxVisibleEventCount, loaded.UiSettings.MaxVisibleEventCount);
            Assert.Equal(EventContextSettings.FixedLineCount, loaded.EventContextSettings.BeforeContextLines);
            Assert.Equal(EventContextSettings.FixedLineCount, loaded.EventContextSettings.AfterContextLines);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    [Fact]
    public async Task SaveAndLoad_PreservesVisualHexMockPattern()
    {
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.UiSettings.MockGeneratorPattern = MockGeneratorPattern.VisualHexPackets;
        var path = CreateTemporaryProfilePath();

        try
        {
            await service.SaveAsync(path, profile, CancellationToken.None);
            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.Equal(MockGeneratorPattern.VisualHexPackets, loaded.UiSettings.MockGeneratorPattern);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    [Fact]
    public async Task SaveAsync_NeverPersistsBridgeAsEnabled()
    {
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.BridgeSettings.Enabled = true;
        profile.BridgeSettings.VirtualPortName = "COM5";
        var path = CreateTemporaryProfilePath();

        try
        {
            await service.SaveAsync(path, profile, CancellationToken.None);

            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream);
            var enabled = document.RootElement
                .GetProperty(nameof(AppProfile.BridgeSettings))
                .GetProperty(nameof(BridgeSettings.Enabled))
                .GetBoolean();

            Assert.False(enabled);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    [Fact]
    public async Task LoadAsync_LegacyEnabledBridge_IsForcedOff()
    {
        var service = new ProfileService();
        var path = CreateTemporaryProfilePath();
        var json = """
            {
              "ProfileSchemaVersion": 1,
              "Name": "Legacy bridge profile",
              "BridgeSettings": {
                "Enabled": true,
                "VirtualPortName": "COM5",
                "MaxQueuedChunks": 2048,
                "MaxQueuedBytes": 33554432,
                "ManualTxIdleGuardMs": 25
              }
            }
            """;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json);

            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.False(loaded.BridgeSettings.Enabled);
            Assert.Contains("never restored", service.LastError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    private static string CreateTemporaryProfilePath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "SerialMonitor.Tests",
            Guid.NewGuid().ToString("N"),
            "profile.json");
    }

    private static void DeleteTemporaryProfileDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
