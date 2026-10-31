using System.Runtime.CompilerServices;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;
using Windows.System;

namespace SerialMonitor.WinUI.Tests;

public sealed class QuickCommandShortcutTests
{
    [Theory]
    [InlineData(VirtualKey.F1)]
    [InlineData(VirtualKey.F8)]
    [InlineData(VirtualKey.F24)]
    [InlineData(VirtualKey.A)]
    [InlineData(VirtualKey.Number7)]
    [InlineData(VirtualKey.NumberPad7)]
    public async Task RecordedSingleKeySurvivesValidationProfileAndDispatchLookup(VirtualKey key)
    {
        var recorded = SavedCommandShortcut.CreateText(key, VirtualKeyModifiers.None);
        Assert.NotNull(recorded);
        Assert.True(MainViewModel.TryNormalizeSavedCommandShortcut(recorded.ToLowerInvariant(), out var normalized, out _));
        Assert.Equal(recorded, normalized);
        var directory = Path.Combine(Path.GetTempPath(), nameof(QuickCommandShortcutTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var service = new ProfileService();
            var profile = service.CreateDefaultProfile();
            profile.SavedCommands = [new TxCommand("Single key", "PING") { OptionalShortcut = normalized }];
            var path = Path.Combine(directory, "profile.json");
            await service.SaveAsync(path, profile, default);
            var loaded = await service.LoadAsync(path, default);
            Assert.Equal(recorded, loaded.SavedCommands[0].OptionalShortcut);
            var model = ModelWith(loaded.SavedCommands[0]);
            Assert.True(model.HasSavedCommandShortcut(recorded));
            Assert.False(model.HasSavedCommandShortcut("F2"));
            Assert.Equal(recorded, Assert.Single(model.SavedCommandShortcuts));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("Escape")]
    [InlineData("Esc")]
    [InlineData("Control")]
    [InlineData("Shift")]
    [InlineData("Menu")]
    [InlineData("LeftWindows")]
    [InlineData("F25")]
    [InlineData("F1+Alt")]
    [InlineData("Alt++C")]
    [InlineData("+Alt+W")]
    [InlineData("Alt+R+")]
    [InlineData("Ctrl++0")]
    public void CancelModifiersAndUnsupportedKeysCannotBeSaved(string text) =>
        Assert.False(MainViewModel.TryNormalizeSavedCommandShortcut(text, out _, out _));

    [Theory]
    [InlineData("Enter")]
    [InlineData("Tab")]
    [InlineData("Space")]
    [InlineData("Backspace")]
    [InlineData("Delete")]
    [InlineData("Insert")]
    [InlineData("Home")]
    [InlineData("End")]
    [InlineData("Left")]
    [InlineData("Right")]
    [InlineData("Up")]
    [InlineData("Down")]
    [InlineData("PageUp")]
    [InlineData("PageDown")]
    [InlineData("F3")]
    [InlineData("F4")]
    [InlineData("Shift+F3")]
    [InlineData("Ctrl+F")]
    [InlineData(" ctrl + g ")]
    [InlineData("Control+C")]
    [InlineData("Ctrl+M")]
    [InlineData("Alt+W")]
    [InlineData("Alt+C")]
    [InlineData("alt+r")]
    [InlineData("F5")]
    [InlineData("F7")]
    [InlineData("F12")]
    [InlineData("Ctrl+0")]
    public async Task ReservedBindingsAreRejectedAndOldProfilesCannotActivateThem(string shortcut)
    {
        Assert.False(MainViewModel.TryNormalizeSavedCommandShortcut(shortcut, out _, out var error));
        Assert.Contains("reserved", error);
        var command = new TxCommand("Legacy binding", "PING") { OptionalShortcut = shortcut };
        var model = ModelWith(command);
        Assert.False(model.HasSavedCommandShortcut(shortcut));
        Assert.Empty(model.SavedCommandShortcuts);
        // The guard must reject a stale message before accessing TX or UI services.
        Assert.False(await model.SendSavedCommandShortcutAsync(shortcut));
        Assert.Equal(shortcut, command.OptionalShortcut);
    }

    [Theory]
    [InlineData(VirtualKey.Enter, VirtualKeyModifiers.None)]
    [InlineData(VirtualKey.Tab, VirtualKeyModifiers.None)]
    [InlineData(VirtualKey.F3, VirtualKeyModifiers.None)]
    [InlineData(VirtualKey.F3, VirtualKeyModifiers.Shift)]
    [InlineData(VirtualKey.F4, VirtualKeyModifiers.None)]
    [InlineData(VirtualKey.G, VirtualKeyModifiers.Control)]
    [InlineData(VirtualKey.F, VirtualKeyModifiers.Control)]
    [InlineData(VirtualKey.W, VirtualKeyModifiers.Menu)]
    [InlineData(VirtualKey.C, VirtualKeyModifiers.Menu)]
    [InlineData(VirtualKey.R, VirtualKeyModifiers.Menu)]
    [InlineData(VirtualKey.NumberPad0, VirtualKeyModifiers.Control)]
    public void ReservedPhysicalKeysCannotBecomeDispatchableBindings(VirtualKey key, VirtualKeyModifiers modifiers)
    {
        var gesture = SavedCommandShortcut.CreateGestureText(key, modifiers);
        Assert.NotNull(ShortcutReservations.Find(gesture));
        Assert.Null(SavedCommandShortcut.CreateText(key, modifiers));
    }

    [Fact]
    public void DuplicateQuickCommandIsRejectedButEditingItsOwnBindingIsAllowed()
    {
        var source = new TxCommand("First", "PING") { OptionalShortcut = " f1 " };
        var model = ModelWith(source);
        Assert.False(model.TryValidateSavedCommandShortcut("F1", null, out _, out var error));
        Assert.Contains("First", error);
        Assert.True(model.TryValidateSavedCommandShortcut("F1", source, out var normalized, out _));
        Assert.Equal("F1", normalized);
        Assert.True(model.TryValidateSavedCommandShortcut("F2", null, out _, out _));

        model.Commands.SavedCommands.Add(new("Second", "PONG") { OptionalShortcut = "F1" });
        Assert.False(model.TryValidateSavedCommandShortcut("F1", source, out _, out _));
        Assert.False(model.HasSavedCommandShortcut("F1"));
        Assert.Empty(model.SavedCommandShortcuts);
    }

    private static MainViewModel ModelWith(TxCommand command)
    {
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        var commands = new CommandViewModel();
        commands.SavedCommands.Add(command);
        typeof(MainViewModel).GetField("<Commands>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(model, commands);
        return model;
    }
}
