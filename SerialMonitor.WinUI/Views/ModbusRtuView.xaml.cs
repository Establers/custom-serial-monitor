using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Views;

public sealed partial class ModbusRtuView : UserControl
{
    private bool _itemDialogOpen;
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
        if (_itemDialogOpen || Model is not { IsSettingsEnabled: true } model) return;
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
    private void FitEditor(ScrollViewer scroll, FrameworkElement panel, double width, double height)
    {
        if (XamlRoot is not { } root) return;
        // Leave room for popup padding and the overlay scrollbar on small windows.
        panel.Width = Math.Min(width, Math.Max(160, root.Size.Width - 64));
        scroll.MaxHeight = Math.Min(height, Math.Max(96, root.Size.Height - 96));
    }
}
