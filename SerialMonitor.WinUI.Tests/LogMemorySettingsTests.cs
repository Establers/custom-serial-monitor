using System.Text.Json;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogMemorySettingsTests
{
    [Fact]
    public void LegacyProfilesDefaultTo256MiBAndClonesPreserveCustomBudgets()
    {
        Assert.Equal(256, JsonSerializer.Deserialize<UiSettings>("{}")!.LogMemoryBudgetMiB);
        Assert.Equal(512, new UiSettings { LogMemoryBudgetMiB = 512 }.Clone().LogMemoryBudgetMiB);
    }

    [Theory]
    [InlineData(64, 64)]
    [InlineData(384, 384)]
    [InlineData(2048, 2048)]
    [InlineData(0, 256)]
    [InlineData(-1, 256)]
    [InlineData(2049, 256)]
    public async Task ProfileRoundTripValidatesAndPreservesMemoryBudgets(int configured, int expected)
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(LogMemorySettingsTests), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "profile.json");
        try
        {
            Directory.CreateDirectory(root);
            var service = new ProfileService();
            var profile = service.CreateDefaultProfile();
            profile.UiSettings.LogMemoryBudgetMiB = configured;
            // Read also validates hand-edited settings, independently of Save.
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(profile));
            var loaded = await service.LoadAsync(path, CancellationToken.None);
            Assert.Equal(expected, loaded.UiSettings.LogMemoryBudgetMiB);
            await service.SaveAsync(path, loaded, CancellationToken.None);
            Assert.Equal(expected, (await new ProfileService().LoadAsync(path, CancellationToken.None)).UiSettings.LogMemoryBudgetMiB);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
