using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Views;

public sealed partial class ModbusRtuView : UserControl
{
    private bool _itemDialogOpen;
    private bool _groupDialogOpen;
    private ModbusRtuViewModel? Model => DataContext as ModbusRtuViewModel;
    public ModbusRtuView() => InitializeComponent();
    private void View_Loaded(object sender, RoutedEventArgs args)
    {
        Model?.Refresh();
    }
    private void SettingsFlyout_Opening(object sender, object args)
    {
        SettingsEditorRoot.DataContext = Model;
        FitEditor(SettingsScroll, SettingsPanel, 360, 460);
    }
    private void WriteFlyout_Opening(object sender, object args)
    {
        WriteEditorRoot.DataContext = Model;
        FitEditor(WriteScroll, WritePanel, 400, 360);
    }
    private async void Items_Click(object sender, RoutedEventArgs args)
    {
        if (_itemDialogOpen || _groupDialogOpen || Model is not { IsSettingsEnabled: true } model) return;
        _itemDialogOpen = true;
        try
        {
            var settingsRevision = model.SettingsRevision;
            var dialog = new ModbusItemEditorDialog(model.CreateItemEditorSettings(), model.SelectedRow?.Address)
            {
                XamlRoot = XamlRoot, RequestedTheme = ActualTheme
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                model.ApplyItemDefinitions(dialog.ResultItems, settingsRevision);
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("Modbus.ItemEditor", ex);
            model.ReportItemEditorError(ex.Message);
        }
        finally { _itemDialogOpen = false; }
    }
    private async void Groups_Click(object sender, RoutedEventArgs args)
    {
        if (_itemDialogOpen || _groupDialogOpen || Model is not { IsSettingsEnabled: true } model) return;
        _groupDialogOpen = true;
        try
        {
            var settingsRevision = model.SettingsRevision;
            var dialog = new ModbusPollingEditorDialog(model.CreatePollingEditorSettings())
            {
                XamlRoot = XamlRoot, RequestedTheme = ActualTheme
            };
            while (true)
            {
                var result = await dialog.ShowAsync();
                if (dialog.ItemEditorSettings is { } itemSettings)
                {
                    dialog.ClearItemEditorRequest();
                    var items = new ModbusItemEditorDialog(itemSettings) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
                    if (await items.ShowAsync() == ContentDialogResult.Primary)
                        dialog.Model.ApplyItemDefinitions(items.ResultItems);
                    continue;
                }
                if (result == ContentDialogResult.Primary)
                    model.ApplyPollingSettings(dialog.ResultSettings, settingsRevision);
                break;
            }
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("Modbus.PollingEditor", ex);
            model.ReportItemEditorError(ex.Message);
        }
        finally { _groupDialogOpen = false; }
    }
    private void FitEditor(ScrollViewer scroll, FrameworkElement panel, double width, double height)
    {
        if (XamlRoot is not { } root) return;
        // Leave room for popup padding and the overlay scrollbar on small windows.
        panel.Width = Math.Min(width, Math.Max(160, root.Size.Width - 64));
        scroll.MaxHeight = Math.Min(height, Math.Max(96, root.Size.Height - 96));
    }
}
