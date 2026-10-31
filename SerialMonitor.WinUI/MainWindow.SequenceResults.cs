using Microsoft.UI.Xaml;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Views;

namespace SerialMonitor.WinUI;

public sealed partial class MainWindow
{
    private SequenceResultsWindow? _sequenceResultsWindow;
    private void OpenSequenceResults_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (_sequenceResultsWindow is null)
            {
                _sequenceResultsWindow = new SequenceResultsWindow(_viewModel.SequenceResultsStore, Root.ActualTheme);
                _sequenceResultsWindow.Closed += (_, _) => _sequenceResultsWindow = null;
            }
            UpdateSequenceResultLogPresentation();
            _sequenceResultsWindow.Activate();
        }
        catch (Exception ex) { _viewModel.RecordSequenceFilePickerError("실행 결과 창 열기 실패: " + ex.Message); }
    }

    private void UpdateSequenceResultLogPresentation() => _sequenceResultsWindow?.UpdateLogPresentation(new(
        _viewModel.SelectedRxLineEnding, _viewModel.SelectedTimestampDisplayFormatOption.Format,
        _viewModel.ShowTimestampInLogView, _viewModel.ShowRxTxDirectionPrefixInLogView));
}
