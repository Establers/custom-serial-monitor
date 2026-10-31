using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI;

public sealed partial class MainWindow
{
    private bool _eventContextDialogOpen;
    private async void ShowEventContext_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel.SelectedEvent is null || _eventContextDialogOpen) return;
        _eventContextDialogOpen = true;
        try
        {
        await _viewModel.RefreshCurrentEventContextAsync();
        var text = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            MinWidth = 600, MinHeight = 320, MaxHeight = 540 };
        text.SetBinding(TextBox.TextProperty, new Binding { Source = _viewModel, Path = new PropertyPath("SelectedEventContextText") });
        var previous = new Button { Content = UiText.Get("EventContextPrevious", "Previous page"), Command = _viewModel.PreviousEventContextPageCommand };
        var next = new Button { Content = UiText.Get("EventContextNext", "Next page"), Command = _viewModel.NextEventContextPageCommand };
        var copy = new Button { Content = UiText.Get("EventContextCopy", "Copy page"), Command = _viewModel.CopyEventContextCommand };
        var save = new Button { Content = UiText.Get("EventContextExport", "Export full context") };
        var refresh = new Button { Content = UiText.Get("EventContextRefresh", "Refresh") };
        refresh.Click += async (_, _) => await _viewModel.RefreshCurrentEventContextAsync();
        save.Click += async (_, _) => await _viewModel.ExportSelectedEventContextAsync(name => LogExportFilePicker.PickAsync(
            WinRT.Interop.WindowNative.GetWindowHandle(this), name, UiText.Get("EventContextExport", "Export full context"),
            "Serial log", UiText.Get("LogHistoryExportAction", "Export")));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(previous); actions.Children.Add(next); actions.Children.Add(refresh); actions.Children.Add(copy); actions.Children.Add(save);
        var status = new TextBlock();
        status.SetBinding(TextBlock.TextProperty, new Binding { Source = _viewModel, Path = new PropertyPath("SelectedEventContextHeaderText") });
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(status); content.Children.Add(text); content.Children.Add(actions);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = UiText.Get("EventContextTitle", "Event context"),
            Content = content, CloseButtonText = UiText.Get("EventContextClose", "Close") };
        dialog.Resources["ContentDialogMaxWidth"] = 1000d;
        await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("Event context dialog", ex);
            _viewModel.RecordEventSelectionError("Event context dialog failed: " + ex.Message);
        }
        finally { _eventContextDialogOpen = false; }
    }
}
