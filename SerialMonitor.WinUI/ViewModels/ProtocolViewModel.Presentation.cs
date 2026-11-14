using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class ProtocolViewModel
{
    private bool _changingViewSelection;
    private IReadOnlyList<ProtocolViewOption>? _presentationOptions;
    // The four objects and labels stay stable after the first binding read.
    // Update availability on the objects without replacing the ItemsSource.
    public IReadOnlyList<ProtocolViewOption> PresentationOptions => _presentationOptions ??=
    [
        new(DisabledLabel, IsProtocolSelectionEnabled),
        new(ModbusPacketViewLabel, IsModbusViewSelectionEnabled),
        new(ModbusReadWriteViewLabel, IsModbusViewSelectionEnabled),
        new(MstpPacketViewLabel, IsProtocolSelectionEnabled)
    ];
    // Presentation indices are UI-only. Keep the analyzer's existing protocol
    // indices (off/Modbus/BACnet) independent from this combined menu.
    public int SelectedViewIndex
    {
        get => SelectedProtocolIndex switch { 1 => IsRegisterMonitorSelected ? 2 : 1, 2 => 3, _ => 0 };
        set
        {
            if (value is < 0 or > 3) { NotifyViewSelection(); return; }
            var protocol = value == 3 ? 2 : value > 0 ? 1 : 0;
            if (!IsProtocolSelectionEnabled && protocol != SelectedProtocolIndex)
            {
                NotifyViewSelection();
                return;
            }
            _changingViewSelection = true;
            try
            {
                SelectedProtocolIndex = protocol;
                if (SelectedProtocolIndex == protocol) IsRegisterMonitorSelected = value == 2;
            }
            finally
            {
                _changingViewSelection = false;
                NotifyViewSelection();
            }
        }
    }

    public bool IsModbusViewSelectionEnabled => IsProtocolSelectionEnabled || SelectedProtocolIndex == 1;
    public string ViewSelectionLabel => UiText.Get("ProtocolViewSelection", "Protocol and view");
    public string MoreActionsLabel => UiText.Get("ProtocolMoreActions", "More actions");
    public string ModbusPacketViewLabel => UiText.Get("ProtocolModbusPacketView", "Packet analysis");
    public string ModbusReadWriteViewLabel => UiText.Get("ProtocolModbusReadWriteView", "Device monitoring & control");
    public string MstpPacketViewLabel => UiText.Get("ProtocolMstpPacketView", "BACnet MS/TP · Packet analysis");

    private bool ShowModbusHeader => RegisterMonitorVisibility == Microsoft.UI.Xaml.Visibility.Visible || Modbus?.IsBusy == true;
    public string WorkspaceHeader => ShowModbusHeader && Modbus is { } model
        ? $"{model.Status} · {model.RequestSummary}" : $"{Activity} · {RetainedSummary}";
    public string WorkspaceHeaderHelp => ShowModbusHeader ? WorkspaceHeader : Status;

    private void NotifyViewSelection()
    {
        if (_changingViewSelection) return;
        if (_presentationOptions is { } options)
        {
            options[0].IsEnabled = IsProtocolSelectionEnabled;
            options[1].IsEnabled = IsModbusViewSelectionEnabled;
            options[2].IsEnabled = IsModbusViewSelectionEnabled;
            options[3].IsEnabled = IsProtocolSelectionEnabled;
        }
        OnPropertyChanged(nameof(SelectedViewIndex));
        OnPropertyChanged(nameof(IsModbusViewSelectionEnabled));
        NotifyWorkspaceHeader();
    }

    private void NotifyWorkspaceHeader()
    {
        OnPropertyChanged(nameof(WorkspaceHeader));
        OnPropertyChanged(nameof(WorkspaceHeaderHelp));
    }
}
