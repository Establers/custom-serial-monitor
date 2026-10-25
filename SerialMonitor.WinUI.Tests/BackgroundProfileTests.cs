using System.Text.Json;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class BackgroundProfileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SerialMonitor.BackgroundTests", Guid.NewGuid().ToString("N"));
    private string ProfilePath => Path.Combine(_directory, "profile.json");

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.01)]
    [InlineData(0.25)]
    [InlineData(0.50)]
    [InlineData(0.73)]
    [InlineData(1.0)]
    public async Task Background_PreservesFullOpacityRangeAcrossRestart(double opacity)
    {
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.UiSettings.CuteBackgroundMode = true;
        profile.UiSettings.CuteBackgroundOpacity = opacity;

        await service.SaveAsync(ProfilePath, profile, CancellationToken.None);
        var restartedService = new ProfileService();
        var loaded = await restartedService.LoadAsync(ProfilePath, CancellationToken.None);

        Assert.True(loaded.UiSettings.CuteBackgroundMode);
        Assert.Equal(opacity, loaded.UiSettings.CuteBackgroundOpacity);
        Assert.Null(restartedService.LastError);
    }

    [Theory]
    [InlineData(".jpg", true)]
    [InlineData(".png", true)]
    [InlineData(".png", false)]
    public async Task CustomImage_PreservesPathAndModeAcrossRestart(string extension, bool enabled)
    {
        Directory.CreateDirectory(_directory);
        var imagePath = Path.Combine(_directory, "사용자 배경" + extension);
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.UiSettings.CuteBackgroundMode = enabled;
        profile.UiSettings.CuteBackgroundImagePath = imagePath;

        await service.SaveAsync(ProfilePath, profile, CancellationToken.None);
        var restartedService = new ProfileService();
        var loaded = await restartedService.LoadAsync(ProfilePath, CancellationToken.None);

        Assert.Equal(enabled, loaded.UiSettings.CuteBackgroundMode);
        Assert.Equal(imagePath, loaded.UiSettings.CuteBackgroundImagePath);
        Assert.False(restartedService.LastCuteBackgroundCustomPathCleared);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableImage_DoesNotErasePreferenceOnLoadOrSave(bool legacyPath)
    {
        var imagePath = legacyPath ? @"C:\Users\pjh\Downloads\s88.png" : Path.Combine(_directory, "temporarily unavailable.png");
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.UiSettings.CuteBackgroundMode = true;
        profile.UiSettings.CuteBackgroundImagePath = imagePath;
        // Simulate a profile from an earlier version as well as subsequent saves.
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(ProfilePath, JsonSerializer.Serialize(profile));

        var loaded = await service.LoadAsync(ProfilePath, CancellationToken.None);
        Assert.Equal(imagePath, loaded.UiSettings.CuteBackgroundImagePath);
        Assert.False(service.LastCuteBackgroundCustomPathCleared);
        await service.SaveAsync(ProfilePath, loaded, CancellationToken.None);
        var reloaded = await new ProfileService().LoadAsync(ProfilePath, CancellationToken.None);
        Assert.True(reloaded.UiSettings.CuteBackgroundMode);
        Assert.Equal(imagePath, reloaded.UiSettings.CuteBackgroundImagePath);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public async Task InvalidOpacity_RestoresDefault(double opacity)
    {
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.UiSettings.CuteBackgroundOpacity = opacity;
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(ProfilePath, JsonSerializer.Serialize(profile));

        var loaded = await service.LoadAsync(ProfilePath, CancellationToken.None);

        Assert.Equal(new UiSettings().CuteBackgroundOpacity, loaded.UiSettings.CuteBackgroundOpacity);
        Assert.Contains("opacity", service.LastError!);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
