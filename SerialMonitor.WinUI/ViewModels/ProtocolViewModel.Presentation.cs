using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class ProtocolViewModel
{
    private bool _changingViewSelection;
    private IReadOnlyList<ProtocolViewOption>? _presentationOptions;
    public Func<int, bool>? TryChangeRole { get; set; }
    private bool _sensorSelected;
    private ModbusSensorViewModel? _sensor;
    public ModbusSensorViewModel? Sensor
    {
        get => _sensor;
        set
        {
            _sensor = value;
            if (value is not null) value.PropertyChanged += (_, args) =>
            { if (args.PropertyName == nameof(ModbusSensorViewModel.IsBusy)) { OnPropertyChanged(nameof(IsProtocolSelectionEnabled)); NotifyViewSelection(); } };
            OnPropertyChanged();
        }
    }
    public bool IsSensorSelected => SelectedProtocolIndex == 1 && _sensorSelected;
    public Microsoft.UI.Xaml.Visibility SensorVisibility => IsSensorSelected ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string RoleHelp => SelectedViewIndex switch
    {
        1 => "통신 관찰 · 이 연결의 송신 차단 · RX에는 제품의 요청과 센서의 응답이 함께 들어올 수 있어요",
        2 => "장치 직접 시험 · PC가 요청을 보내요 · 같은 버스의 다른 마스터는 중지하세요",
        4 => "센서 시뮬레이터 · 시작하면 제품의 요청에만 응답해요 · 같은 ID의 실제 센서는 분리하세요",
        _ => "일반 통신 · 수동 TX와 시퀀스를 사용할 수 있어요"
    };
    // The five objects and labels stay stable after the first binding read.
    // Update availability on the objects without replacing the ItemsSource.
    public IReadOnlyList<ProtocolViewOption> PresentationOptions => _presentationOptions ??=
    [
        new(DisabledLabel, IsProtocolSelectionEnabled),
        new(ModbusPacketViewLabel, IsModbusViewSelectionEnabled),
        new(ModbusReadWriteViewLabel, IsModbusViewSelectionEnabled),
        new(MstpPacketViewLabel, IsProtocolSelectionEnabled),
        new("Modbus RTU · 센서 시뮬레이터", IsProtocolSelectionEnabled)
    ];
    // Presentation indices are UI-only. Keep the analyzer's existing protocol
    // indices (off/Modbus/BACnet) independent from this combined menu.
    public int SelectedViewIndex
    {
        get => SelectedProtocolIndex switch { 1 => IsSensorSelected ? 4 : IsRegisterMonitorSelected ? 2 : 1, 2 => 3, _ => 0 };
        set
        {
            if (value is < 0 or > 4 || value == SelectedViewIndex) { NotifyViewSelection(); return; }
            if (!IsProtocolSelectionEnabled || TryChangeRole?.Invoke(value) == false) { NotifyViewSelection(); return; }
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
                if (SelectedProtocolIndex == protocol) { _sensorSelected = value == 4; IsRegisterMonitorSelected = value == 2; }
            }
            finally
            {
                _changingViewSelection = false;
                NotifyViewSelection();
            }
        }
    }

    public bool IsModbusViewSelectionEnabled => IsProtocolSelectionEnabled;
    public string ViewSelectionLabel => UiText.Get("ProtocolViewSelection", "Protocol and view");
    public string MoreActionsLabel => UiText.Get("ProtocolMoreActions", "More actions");
    public string ModbusPacketViewLabel => "Modbus RTU · 통신 관찰 (송신 없음)";
    public string ModbusReadWriteViewLabel => "Modbus RTU · 장치 직접 시험";
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
            options[4].IsEnabled = IsProtocolSelectionEnabled;
        }
        OnPropertyChanged(nameof(SelectedViewIndex));
        OnPropertyChanged(nameof(SensorVisibility));
        OnPropertyChanged(nameof(PacketAnalysisVisibility));
        OnPropertyChanged(nameof(RoleHelp));
        OnPropertyChanged(nameof(IsModbusViewSelectionEnabled));
        NotifyWorkspaceHeader();
    }

    private void NotifyWorkspaceHeader()
    {
        OnPropertyChanged(nameof(WorkspaceHeader));
        OnPropertyChanged(nameof(WorkspaceHeaderHelp));
    }
}
