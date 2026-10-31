using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI;

public sealed partial class MainWindow
{
    private async void Root_PreviewSavedCommandKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (IsClosingOrClosed || args.Handled || IsWebViewSource(args.OriginalSource) ||
            HasAncestor<ContentDialog>(args.OriginalSource)) return;

        // Function keys work while composing TX/search text. Printable keys
        // retain native text input and type-to-select behavior in all controls;
        // their quick commands are dispatched from the log WebView instead.
        if (!SavedCommandShortcut.IsFunctionKey(args.Key) && HasAncestor<Control>(args.OriginalSource)) return;
        var shortcut = SavedCommandShortcut.CreateText(args.Key);
        if (shortcut is null || shortcut.Contains('+') || !_viewModel.HasSavedCommandShortcut(shortcut)) return;

        args.Handled = true;
        if (!args.KeyStatus.WasKeyDown) await ExecuteSavedCommandShortcutAsync(shortcut);
    }

    private async Task SyncXtermCommandShortcutsAsync()
    {
        if (!_isXtermReady || IsClosingOrClosed) return;
        try
        {
            var shortcuts = JsonSerializer.Serialize(_viewModel.SavedCommandShortcuts);
            var reserved = JsonSerializer.Serialize(ShortcutReservations.Keys);
            await ExecuteXtermScriptAsync($"window.serialMonitorSetCommandShortcuts({shortcuts}, {reserved});");
        }
        catch (Exception ex)
        {
            _viewModel.RecordXtermLayoutError($"Quick command shortcut sync failed: {ex.Message}");
        }
    }
}
