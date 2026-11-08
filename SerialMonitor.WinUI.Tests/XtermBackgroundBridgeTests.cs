using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class XtermBackgroundBridgeTests
{
    private readonly string _presets = Path.Combine(Path.GetTempPath(), "native background profile.json.backgrounds");
    private readonly string _bundled = Path.Combine(Path.GetTempPath(), "FunBackgrounds");

    [Fact]
    public void BundledAndImportedImagesUseSeparateStableHosts()
    {
        var bundled = new Uri(XtermBackgroundBridge.CreateImageUrl(Path.Combine(_bundled, "default.jpg"), _presets, _bundled, 1));
        var imported = new Uri(XtermBackgroundBridge.CreateImageUrl(Path.Combine(_presets, "preset-1.png"), _presets, _bundled, 2));
        Assert.Equal("serialmonitor-background-bundled.local", bundled.Host);
        Assert.Equal("serialmonitor-background.local", imported.Host);
    }

    [Fact]
    public void UnicodeReservedCharactersAndInPlaceReplacementGetValidDistinctUrls()
    {
        var image = Path.Combine(_presets, "배경 #1.png");
        var first = new Uri(XtermBackgroundBridge.CreateImageUrl(image, _presets, _bundled, 1));
        var replaced = new Uri(XtermBackgroundBridge.CreateImageUrl(image, _presets, _bundled, 2));
        Assert.Equal("/배경 #1.png", Uri.UnescapeDataString(first.AbsolutePath));
        Assert.Empty(first.Fragment);
        Assert.NotEqual(first.Query, replaced.Query);
    }

    [Fact]
    public void DisabledImagesHaveNoUrlAndSiblingFoldersCannotEscapeRegisteredRoots()
    {
        Assert.Empty(XtermBackgroundBridge.CreateImageUrl("", _presets, _bundled, 1));
        Assert.Throws<InvalidOperationException>(() => XtermBackgroundBridge.CreateImageUrl(
            Path.Combine(_presets + "-other", "image.png"), _presets, _bundled, 1));
    }
}
