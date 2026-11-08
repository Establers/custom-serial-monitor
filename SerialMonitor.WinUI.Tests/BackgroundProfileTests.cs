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
    public async Task CustomImage_PreservesOwnedCopyAndModeAcrossRestart(string extension, bool enabled)
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
        Assert.NotEqual(imagePath, loaded.UiSettings.CuteBackgroundImagePath);
        Assert.True(new BackgroundImageStore().IsStoredImage(ProfilePath, 1, loaded.UiSettings.CuteBackgroundImagePath));
        Assert.Equal(await File.ReadAllBytesAsync(imagePath), await File.ReadAllBytesAsync(loaded.UiSettings.CuteBackgroundImagePath));
        Assert.False(restartedService.LastCuteBackgroundCustomPathCleared);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableImage_DoesNotErasePreferenceOnLoadOrSave(bool legacyPath)
    {
        var imagePath = Path.Combine(_directory, legacyPath ? "old-version background.png" : "temporarily unavailable.png");
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

    [Fact]
    public async Task FivePresets_KeepIndependentImagesOpacityAndSelectionAfterOriginalsAreDeleted()
    {
        Directory.CreateDirectory(_directory);
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.UiSettings.EnsureBackgroundPresets();
        var originals = new List<string>();
        for (var number = 1; number <= 5; number++)
        {
            var original = Path.Combine(_directory, $"original-{number}.png");
            await File.WriteAllBytesAsync(original, [(byte)number, 42, 99]);
            originals.Add(original);
            profile.UiSettings.BackgroundPresets![number - 1] = new() { ImagePath = original, Opacity = number / 10.0 };
        }
        profile.UiSettings.SelectedBackgroundPreset = 4;
        await service.SaveAsync(ProfilePath, profile, CancellationToken.None);
        foreach (var original in originals) File.Delete(original);

        var loaded = await new ProfileService().LoadAsync(ProfilePath, CancellationToken.None);

        Assert.Equal(4, loaded.UiSettings.SelectedBackgroundPreset);
        Assert.Equal(0.4, loaded.UiSettings.CuteBackgroundOpacity);
        Assert.Equal(5, loaded.UiSettings.BackgroundPresets!.Count);
        Assert.Equal(5, loaded.UiSettings.BackgroundPresets.Select(preset => preset.ImagePath).Distinct().Count());
        for (var number = 1; number <= 5; number++)
        {
            var preset = loaded.UiSettings.BackgroundPresets[number - 1];
            Assert.Equal(new byte[] { (byte)number, 42, 99 }, await File.ReadAllBytesAsync(preset.ImagePath));
            Assert.Equal(number / 10.0, preset.Opacity);
        }
        Assert.Equal(loaded.UiSettings.BackgroundPresets[3].ImagePath, loaded.UiSettings.CuteBackgroundImagePath);
    }

    [Fact]
    public async Task LegacyProfile_MigratesToPresetOneAndCommitsCopyBeforeShutdown()
    {
        Directory.CreateDirectory(_directory);
        var original = Path.Combine(_directory, "old background.jpg");
        await File.WriteAllBytesAsync(original, [11, 22, 33]);
        var legacy = new ProfileService().CreateDefaultProfile();
        legacy.UiSettings.CuteBackgroundImagePath = original;
        legacy.UiSettings.CuteBackgroundOpacity = 0.73;
        legacy.UiSettings.CuteBackgroundMode = true;
        await File.WriteAllTextAsync(ProfilePath, JsonSerializer.Serialize(legacy));

        var loaded = await new ProfileService().LoadAsync(ProfilePath, CancellationToken.None);
        File.Delete(original);
        // No explicit save or application shutdown between these two loads.
        var restarted = await new ProfileService().LoadAsync(ProfilePath, CancellationToken.None);

        Assert.True(restarted.UiSettings.CuteBackgroundMode);
        Assert.Equal(1, restarted.UiSettings.SelectedBackgroundPreset);
        Assert.Equal(0.73, restarted.UiSettings.BackgroundPresets![0].Opacity);
        Assert.Equal(loaded.UiSettings.CuteBackgroundImagePath, restarted.UiSettings.CuteBackgroundImagePath);
        Assert.Equal(new byte[] { 11, 22, 33 }, await File.ReadAllBytesAsync(restarted.UiSettings.CuteBackgroundImagePath));
        Assert.All(restarted.UiSettings.BackgroundPresets.Skip(1), preset =>
        {
            Assert.Empty(preset.ImagePath);
            Assert.Equal(0.25, preset.Opacity);
        });
    }

    [Fact]
    public async Task ReplacingOnePreset_LeavesOtherImagesAndSettingsUntouched()
    {
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "source.png");
        var store = new BackgroundImageStore();
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var first = await store.ImportAsync(ProfilePath, 1, source, CancellationToken.None);
        var second = await store.ImportAsync(ProfilePath, 2, source, CancellationToken.None);
        await File.WriteAllBytesAsync(source, [4, 5, 6]);
        Assert.Equal(first, await store.ImportAsync(ProfilePath, 1, source, CancellationToken.None));

        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(first));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(second));
        Assert.Equal(2, Directory.GetFiles(ProfilePath + ".backgrounds").Length);
    }

    [Fact]
    public async Task CancelledOrOversizedImport_DoesNotOverwriteStoredImageOrLeaveTemporaryFiles()
    {
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "source.png");
        var store = new BackgroundImageStore();
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var stored = await store.ImportAsync(ProfilePath, 1, source, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ImportAsync(ProfilePath, 1, source, cancellation.Token));
        await using (var oversized = File.OpenWrite(source)) oversized.SetLength(BackgroundImageStore.MaxImageBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(ProfilePath, 1, source, CancellationToken.None));

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(stored));
        Assert.Single(Directory.GetFiles(ProfilePath + ".backgrounds"));
    }

    [Fact]
    public async Task InvalidPresetMetadata_IsBoundedAndNormalizedWithoutLosingOtherSlots()
    {
        Directory.CreateDirectory(_directory);
        var profile = new ProfileService().CreateDefaultProfile();
        profile.UiSettings.SelectedBackgroundPreset = 99;
        profile.UiSettings.BackgroundPresets = [null!, new() { ImagePath = "missing.png", Opacity = -1 }, new(), new(), new(), new()];
        await File.WriteAllTextAsync(ProfilePath, JsonSerializer.Serialize(profile));

        var loaded = await new ProfileService().LoadAsync(ProfilePath, CancellationToken.None);

        Assert.Equal(1, loaded.UiSettings.SelectedBackgroundPreset);
        Assert.Equal(5, loaded.UiSettings.BackgroundPresets!.Count);
        Assert.Empty(loaded.UiSettings.BackgroundPresets[0].ImagePath);
        Assert.Equal("missing.png", loaded.UiSettings.BackgroundPresets[1].ImagePath);
        Assert.Equal(0.25, loaded.UiSettings.BackgroundPresets[1].Opacity);
    }

    [Fact]
    public void Clone_DoesNotShareEditablePresetState()
    {
        var settings = new UiSettings { CuteBackgroundImagePath = "legacy.png", CuteBackgroundOpacity = 0.6 };
        settings.EnsureBackgroundPresets();
        var clone = settings.Clone();
        clone.BackgroundPresets![0].ImagePath = "replacement.png";
        clone.BackgroundPresets[0].Opacity = 0.1;
        clone.SelectedBackgroundPreset = 5;
        clone.ApplySelectedBackgroundPreset();

        Assert.Equal("legacy.png", settings.BackgroundPresets![0].ImagePath);
        Assert.Equal(0.6, settings.BackgroundPresets[0].Opacity);
        Assert.Equal(1, settings.SelectedBackgroundPreset);
        Assert.Empty(clone.CuteBackgroundImagePath);
        Assert.Equal(0.25, clone.CuteBackgroundOpacity);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
