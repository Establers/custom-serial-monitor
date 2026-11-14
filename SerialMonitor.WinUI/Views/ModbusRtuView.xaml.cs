using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Views;

public sealed partial class ModbusRtuView : UserControl
{
    private bool _itemDialogOpen;
    private bool _groupDialogOpen;
    private bool _commandDialogOpen;
    private ModbusRtuViewModel? Model => DataContext as ModbusRtuViewModel;
    public ModbusRtuView() => InitializeComponent();
    private void View_Loaded(object sender, RoutedEventArgs args)
    {
        FitToolbar(ActualWidth);
        Model?.Refresh();
    }
    private void View_SizeChanged(object sender, SizeChangedEventArgs args) => FitToolbar(args.NewSize.Width);
    private void FitToolbar(double width)
    {
        if (width <= 0) return;
        PollingGroupSelector.Width = Math.Clamp(width * 0.25, 110, 240);
        var overflow = width < 760;
        WriteButton.Visibility = GuideButton.Visibility = overflow ? Visibility.Collapsed : Visibility.Visible;
        MonitorMoreButton.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SummaryFlyout_Opening(object sender, object args)
    {
        SummaryPanel.DataContext = Model;
        FitEditor(SummaryScroll, SummaryPanel, 420, 460);
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
    private void GuideFlyout_Opening(object sender, object args)
    {
        GuidePanel.DataContext = Model;
        FitEditor(GuideScroll, GuidePanel, 420, 460);
    }
    private void ShowSettings_Click(object sender, RoutedEventArgs args)
    {
        if (Model?.NeedsPollingGroup == true) Groups_Click(sender, args);
        else SettingsFlyout.ShowAt(SettingsButton);
    }
    private void MoreFlyout_Opening(object sender, object args) => MorePanel.DataContext = Model;
    private void ShowWriteFromOverflow_Click(object sender, RoutedEventArgs args)
    {
        MonitorMoreFlyout.Hide();
        WriteFlyout.ShowAt(MonitorMoreButton);
    }
    private void ShowGuideFromOverflow_Click(object sender, RoutedEventArgs args)
    {
        MonitorMoreFlyout.Hide();
        GuideFlyout.ShowAt(MonitorMoreButton);
    }
    private async void Items_Click(object sender, RoutedEventArgs args)
    {
        if (_itemDialogOpen || _groupDialogOpen || _commandDialogOpen || Model is not { IsSettingsEnabled: true } model) return;
        _itemDialogOpen = true;
        SettingsFlyout.Hide();
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
        if (_itemDialogOpen || _groupDialogOpen || _commandDialogOpen || Model is not { IsSettingsEnabled: true } model) return;
        _groupDialogOpen = true;
        SettingsFlyout.Hide();
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
    private void CommandsFlyout_Opening(object sender, object args)
    {
        CommandsPanel.DataContext = Model;
        FitEditor(CommandsScroll, CommandsPanel, 480, 460);
        Model?.Refresh();
    }
    private async void Commands_Click(object sender, RoutedEventArgs args)
    {
        if (_itemDialogOpen || _groupDialogOpen || _commandDialogOpen || Model is not { IsSettingsEnabled: true } model) return;
        _commandDialogOpen = true;
        CommandsFlyout.Hide();
        try
        {
            var revision = model.CommandSettingsRevision;
            var dialog = new ModbusCommandEditorDialog(model.CreateCommandEditorSettings())
            {
                XamlRoot = XamlRoot, RequestedTheme = ActualTheme
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                model.ApplyCommandSettings(dialog.ResultSettings, revision);
                if (IsLoaded && XamlRoot is not null) CommandsFlyout.ShowAt(CommandsButton);
            }
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("Modbus.CommandEditor", ex);
            model.ReportItemEditorError(ex.Message);
        }
        finally { _commandDialogOpen = false; }
    }
    private void FitEditor(ScrollViewer scroll, FrameworkElement panel, double width, double height)
    {
        if (XamlRoot is not { } root) return;
        // Leave room for popup padding and the overlay scrollbar on small windows.
        panel.Width = Math.Min(width, Math.Max(160, root.Size.Width - 64));
        scroll.MaxHeight = Math.Min(height, Math.Max(96, root.Size.Height - 96));
    }
}
