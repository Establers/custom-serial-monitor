using System.Text.Json.Nodes;
using System.Text.Json;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;
using Windows.System;

namespace SerialMonitor.WinUI.Tests;

public sealed class ShortcutCatalogTests
{
    [Fact]
    public void EveryActualAppBindingAndModifierVariantIsReserved()
    {
        var catalog = AppShortcuts.Catalog;
        Assert.Equal(Enum.GetValues<AppShortcutAction>().Length, catalog.Bindings.Select(binding => binding.Action).Distinct().Count());
        foreach (var binding in catalog.Bindings)
        {
            var text = SavedCommandShortcut.CreateGestureText(binding.Key, binding.Modifiers)!;
            Assert.True(catalog.Matches(binding.Action, binding.Key, binding.Modifiers), text);
            Assert.NotNull(ShortcutReservations.Find(text));
            Assert.Null(SavedCommandShortcut.CreateText(binding.Key, binding.Modifiers));
            Assert.False(MainViewModel.TryNormalizeSavedCommandShortcut(text, out _, out _), text);
        }
        // CI compares the compiled .NET catalog with the actual browser asset.
        var auditPath = Environment.GetEnvironmentVariable("SERIAL_MONITOR_SHORTCUT_AUDIT");
        if (!string.IsNullOrWhiteSpace(auditPath))
        {
            auditPath = Path.GetFullPath(auditPath);
            Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);
            File.WriteAllText(auditPath, JsonSerializer.Serialize(catalog.Bindings.Select(binding => new
            {
                action = binding.Action.ToString(),
                gesture = SavedCommandShortcut.CreateGestureText(binding.Key, binding.Modifiers)
            })));
        }
    }

    [Fact]
    public void EntireRecordableKeySpaceCannotOverlapAnyBuiltInAction()
    {
        foreach (var key in Enum.GetValues<VirtualKey>())
        for (var bits = 0; bits < 16; bits++)
        {
            var modifiers = (VirtualKeyModifiers)bits;
            var recorded = SavedCommandShortcut.CreateText(key, modifiers);
            if (recorded is null) continue;
            Assert.True(MainViewModel.TryNormalizeSavedCommandShortcut(recorded, out _, out _), recorded);
            Assert.Null(AppShortcuts.Catalog.Find(recorded));
            foreach (var action in Enum.GetValues<AppShortcutAction>())
                Assert.False(AppShortcuts.Catalog.Matches(action, key, modifiers), $"{action}: {recorded}");
        }
    }

    [Fact]
    public void AddingGestureToActionAutomaticallyAddsMatchingAndReservation()
    {
        var asset = AppShortcuts.ReadAsset();
        var definitions = JsonNode.Parse(asset.Trim()[ShortcutCatalog.AssetPrefix.Length..^1])!.AsArray();
        definitions.Single(node => node!["action"]!.GetValue<string>() == nameof(AppShortcutAction.GoTo))!["gestures"]!.AsArray().Add("F2");
        var future = ShortcutCatalog.ParseAsset(ShortcutCatalog.AssetPrefix + definitions.ToJsonString() + ";");

        Assert.Null(AppShortcuts.Catalog.Find("F2"));
        Assert.False(AppShortcuts.Catalog.Matches(AppShortcutAction.GoTo, VirtualKey.F2, VirtualKeyModifiers.None));
        Assert.True(future.Matches(AppShortcutAction.GoTo, VirtualKey.F2, VirtualKeyModifiers.None));
        Assert.NotNull(future.Find("F2"));
        Assert.Contains("F2", future.ReservedKeys);
    }

    [Theory]
    [InlineData(VirtualKey.C, VirtualKeyModifiers.Menu | VirtualKeyModifiers.Shift, nameof(AppShortcutAction.SearchCase))]
    [InlineData(VirtualKey.W, VirtualKeyModifiers.Menu | VirtualKeyModifiers.Shift, nameof(AppShortcutAction.SearchWord))]
    [InlineData(VirtualKey.R, VirtualKeyModifiers.Menu | VirtualKeyModifiers.Shift, nameof(AppShortcutAction.SearchRegex))]
    [InlineData(VirtualKey.F4, VirtualKeyModifiers.Shift, nameof(AppShortcutAction.SearchPrevious))]
    [InlineData(VirtualKey.Enter, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, nameof(AppShortcutAction.EditorConfirm))]
    public void PreviouslyImplicitModifierVariantsAreAlsoCovered(VirtualKey key, VirtualKeyModifiers modifiers, string action)
    {
        Assert.True(AppShortcuts.Catalog.Matches(Enum.Parse<AppShortcutAction>(action), key, modifiers));
        Assert.NotNull(ShortcutReservations.Find(SavedCommandShortcut.CreateGestureText(key, modifiers)));
    }
}
