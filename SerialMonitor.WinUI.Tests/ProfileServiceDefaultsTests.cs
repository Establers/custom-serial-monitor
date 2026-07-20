using System.Text.Json;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class ProfileServiceDefaultsTests
{
    [Fact]
    public void DefaultProfile_UsesRecommendationPlusTwoMilliseconds()
    {
        var service = new ProfileService();

        var profile = service.CreateDefaultProfile();

        Assert.Equal(3, profile.UiSettings.HexGroupTimeoutMs);
        Assert.False(profile.UiSettings.HexGroupTimeoutUserConfigured);
    }

    [Fact]
    public async Task MissingProfile_IsCreatedWithOneVisibleScrollbackSetting()
    {
        var service = new ProfileService();
        var path = CreateTemporaryProfilePath();

        try
        {
            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.True(File.Exists(path));
            Assert.Equal(50_000, loaded.UiSettings.MaxVisibleLogLines);

            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream);
            var uiSettings = document.RootElement.GetProperty(nameof(AppProfile.UiSettings));
            Assert.True(uiSettings.TryGetProperty(nameof(UiSettings.MaxVisibleLogLines), out _));
            Assert.False(uiSettings.TryGetProperty("XtermScrollbackSize", out _));
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    [Fact]
    public async Task AutomaticDefault_FollowsSavedBaudAndFrameFormat()
    {
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.SerialSettings.BaudRate = 9_600;
        profile.UiSettings.HexGroupTimeoutMs = 3;
        profile.UiSettings.HexGroupTimeoutUserConfigured = false;
        var path = CreateTemporaryProfilePath();

        try
        {
            await service.SaveAsync(path, profile, CancellationToken.None);
            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.Equal(4, loaded.UiSettings.HexGroupTimeoutMs);
            Assert.False(loaded.UiSettings.HexGroupTimeoutUserConfigured);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    [Fact]
    public async Task UserConfiguredTimeout_IsPreservedExactly()
    {
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.SerialSettings.BaudRate = 9_600;
        profile.UiSettings.HexGroupTimeoutMs = 37;
        profile.UiSettings.HexGroupTimeoutUserConfigured = true;
        var path = CreateTemporaryProfilePath();

        try
        {
            await service.SaveAsync(path, profile, CancellationToken.None);
            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.Equal(37, loaded.UiSettings.HexGroupTimeoutMs);
            Assert.True(loaded.UiSettings.HexGroupTimeoutUserConfigured);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5_001)]
    public async Task InvalidUserConfiguredTimeout_UsesCurrentBaudDefaultAndReturnsToAutomaticMode(
        int invalidTimeoutMs)
    {
        var service = new ProfileService();
        var path = CreateTemporaryProfilePath();
        var json = $$"""
            {
              "ProfileSchemaVersion": 1,
              "Name": "Invalid custom timeout",
              "SerialSettings": {
                "PortName": "MOCK",
                "BaudRate": 1200,
                "DataBits": 8,
                "Parity": "None",
                "StopBits": "One"
              },
              "UiSettings": {
                "HexGroupTimeoutMs": {{invalidTimeoutMs}},
                "HexGroupTimeoutUserConfigured": true
              }
            }
            """;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json);

            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.Equal(15, loaded.UiSettings.HexGroupTimeoutMs);
            Assert.False(loaded.UiSettings.HexGroupTimeoutUserConfigured);
            Assert.Contains("automatic baud/frame default", service.LastError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    [Fact]
    public async Task LegacyTimeoutWithoutMarker_IsTreatedAsUserConfigured()
    {
        var service = new ProfileService();
        var path = CreateTemporaryProfilePath();
        var json = """
            {
              "ProfileSchemaVersion": 1,
              "Name": "Legacy timeout",
              "SerialSettings": {
                "PortName": "MOCK",
                "BaudRate": 9600,
                "DataBits": 8,
                "Parity": "None",
                "StopBits": "One"
              },
              "UiSettings": {
                "HexGroupTimeoutMs": 29
              }
            }
            """;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json);

            var loaded = await service.LoadAsync(path, CancellationToken.None);

            Assert.Equal(29, loaded.UiSettings.HexGroupTimeoutMs);
            Assert.True(loaded.UiSettings.HexGroupTimeoutUserConfigured);
        }
        finally
        {
            DeleteTemporaryProfileDirectory(path);
        }
    }

    [Fact]
    public void DefaultProfilePath_IsUnderLocalAppData()
    {
        var service = new ProfileService();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localAppData, service.DefaultProfilePath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(
            Path.Combine("SerialMonitor", "profiles", "default.json"),
            service.DefaultProfilePath,
            StringComparison.OrdinalIgnoreCase);
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
