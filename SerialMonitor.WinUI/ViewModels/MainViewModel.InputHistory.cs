using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    private readonly PortInputHistoryState _portInputHistories = new();
    private bool _portInputHistoryReady;

    private string CaptureTransmitHistoryPort() => _portInputHistoryReady
        ? _portInputHistories.ActivePort
        : ProfileInputHistory.PortKey(_lastSuccessfulSerialSettings?.PortName ?? GetActualPortName(SelectedPort));

    private void RecordTransmittedCommandHistory(string port, string commandText)
    {
        if (!_portInputHistoryReady || string.Equals(_portInputHistories.ActivePort, port, StringComparison.Ordinal))
            Commands.AddToHistory(commandText);
        else
            // Completion can resume after disconnect/port selection or a TX event.
            // Do not replace the visible history/search/draft of the newly selected port.
            _portInputHistories.RecordCommand(port, commandText, DateTimeOffset.Now);
    }

    private void CaptureCurrentPortInputHistory()
    {
        if (!_portInputHistoryReady) return;
        _portInputHistories.Capture(new InstanceInputHistory
        {
            CommandHistory = Commands.GetHistorySnapshot().ToList(),
            SearchHistory = _searchHistory.ToList(),
            LastSearchText = SearchText
        });
    }

    private void RestorePortInputHistory(AppProfile profile)
    {
        _portInputHistories.Load(profile);
        _portInputHistoryReady = true;
        ApplyPortInputHistory(_portInputHistories.SwitchTo(_portInputHistories.ActivePort));
    }

    private void SwitchPortInputHistory(string? port)
    {
        if (!_portInputHistoryReady ||
            string.Equals(_portInputHistories.ActivePort, ProfileInputHistory.PortKey(port), StringComparison.Ordinal)) return;
        CaptureCurrentPortInputHistory();
        ApplyPortInputHistory(_portInputHistories.SwitchTo(port));
    }

    private void ApplyPortInputHistory(InstanceInputHistory history)
    {
        var previousSuppress = _suppressSettingsApplyRecording;
        _suppressSettingsApplyRecording = true;
        try
        {
            Commands.LoadHistory(history.CommandHistory);
            Commands.HistorySearchText = string.Empty;
            Commands.CurrentCommandText = string.Empty;
            SearchText = history.LastSearchText;
            RestoreSearchHistory(history.SearchHistory);
            _currentUiSettings.SearchHistory = history.SearchHistory.ToList();
        }
        finally { _suppressSettingsApplyRecording = previousSuppress; }
    }

    private void RestoreSearchHistory(IEnumerable<string>? history)
    {
        _searchHistory.Clear();
        _searchHistory.AddRange(ProfileInputHistory.NormalizeSearchHistory(history));
        ResetSearchHistoryNavigation();
    }
}
