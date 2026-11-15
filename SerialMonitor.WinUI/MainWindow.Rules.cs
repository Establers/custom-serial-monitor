using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Views;

namespace SerialMonitor.WinUI;

public sealed partial class MainWindow
{
    private async void ExportLogRules_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var dialog = new LogRuleTransferDialog(_viewModel.LogRules, _viewModel.SelectedLogRule) { XamlRoot = Root.XamlRoot };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("MainWindow.ExportLogRules", ex);
            _viewModel.ReportRuleTransferError(ex);
        }
    }

    private async void ImportLogRules_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var dialog = new LogRuleTransferDialog { XamlRoot = Root.XamlRoot };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.ImportedRules is { } rules)
                _viewModel.ImportLogRules(rules);
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("MainWindow.ImportLogRules", ex);
            _viewModel.ReportRuleTransferError(ex);
        }
    }
}
