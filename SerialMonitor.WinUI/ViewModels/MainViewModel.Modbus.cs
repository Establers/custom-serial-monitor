using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    private IModbusRtuMonitorService? _modbusService;
    public ModbusRtuViewModel? Modbus { get; private set; }
    public ModbusSensorViewModel? Sensor { get; private set; }
    public bool IsModbusBusy => Modbus?.IsBusy == true || Sensor?.IsBusy == true;

    private void InitializeModbusMonitor()
    {
        _modbusService = new ModbusRtuMonitorService(_serialService, (bytes, generation) =>
        {
            var line = LogLine.Tx(FormatBytesAsHex(bytes), bytes, contentMode: LogRuleMatchMode.Hex);
            FanOutLogLine(line, fileEligible: true, detectEvent: true, protocolSession: generation);
        });
        Modbus = new ModbusRtuViewModel(_modbusService, CanStartModbusOperation, SaveModbusSettingsAsync,
            ModbusStateChanged, ModbusUnavailableReason);
        Protocol.Modbus = Modbus;
        Sensor = new(new ModbusSensorService(_serialService, (bytes, generation) =>
            FanOutLogLine(LogLine.Tx(FormatBytesAsHex(bytes), bytes, contentMode: LogRuleMatchMode.Hex),
                fileEligible: true, detectEvent: true, protocolSession: generation)),
            () => Protocol.SelectedViewIndex == 4 && CanStartModbusTransport(), ModbusStateChanged, ModbusTransportUnavailableReason);
        Protocol.Sensor = Sensor;
        Protocol.TryChangeRole = view =>
        {
            if (IsModbusBusy || IsSequenceRunning || IsAutomationBusy || IsBridgeActive || IsBridgeStarting || _activeGuiSends != 0) return false;
            try
            {
                _serialService.SetTransmitMode(view switch { 1 => SerialTransmitMode.Observe, 2 => SerialTransmitMode.DirectTest, 4 => SerialTransmitMode.Sensor, _ => SerialTransmitMode.Normal });
                return true;
            }
            catch (InvalidOperationException ex) { SetStatus(ex.Message); return false; }
        };
        _serialService.SetTransmitMode(SerialTransmitMode.Observe);
        var lastRole = Protocol.SelectedViewIndex;
        Protocol.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(Protocol.SelectedViewIndex) || lastRole == Protocol.SelectedViewIndex) return;
            lastRole = Protocol.SelectedViewIndex; ModbusStateChanged(); RefreshModbusAvailability();
        };
    }

    private bool CanStartModbusOperation() => Protocol.SelectedViewIndex == 2 && CanStartModbusTransport();
    private bool CanStartModbusTransport() => !IsShuttingDown && _serialService.IsConnected && !IsBusy &&
        !IsSequenceRunning && !IsAutomationBusy && !IsBridgeActive && !IsBridgeStarting && !IsAutoReconnectRunning &&
        _activeGuiSends == 0 && !_serialConfigurationBusy && !_serialControlLineBusy && !_isProfileSaveActive &&
        !IsTerminalDirectInputEnabled && _terminalInputCleanup.IsCompleted && !_serialService.IsMockStressRunning &&
        (_serialService.AppliedConfiguration?.DataBits ?? 8) == 8;

    private string ModbusUnavailableReason()
    {
        if (Protocol.SelectedViewIndex != 2) return "직접 요청하려면 장치 직접 시험을 선택하세요.";
        return ModbusTransportUnavailableReason();
    }

    private string ModbusTransportUnavailableReason()
    {
        if (!_serialService.IsConnected)
            return UiText.Get("ModbusMonitorNeedConnection", "Connect a serial port before reading or writing.");
        if ((_serialService.AppliedConfiguration?.DataBits ?? 8) != 8)
            return UiText.Get("ModbusMonitorNeedEightBits", "Modbus RTU requires 8 data bits. Correct the serial settings first.");
        if (IsTerminalDirectInputEnabled)
            return UiText.Get("ModbusMonitorDisableDirectInput", "Switch to log view before starting Modbus RTU.");
        if (IsSequenceRunning || IsAutomationBusy || IsBridgeActive || IsBridgeStarting)
            return UiText.Get("ModbusMonitorOtherOwner", "Stop the sequence, AI operation or bridge before starting Modbus.");
        return UiText.Get("ModbusMonitorWait", "Wait for the current connection, transmit, settings or save operation to finish.");
    }

    private void ModbusStateChanged()
    {
        OnPropertyChanged(nameof(IsModbusBusy));
        OnPropertyChanged(nameof(CanSendManualTx));
        OnPropertyChanged(nameof(CanStartBridge));
        OnPropertyChanged(nameof(CanChangeTerminalMode));
        OnPropertyChanged(nameof(CanEditConnectionSettings));
        OnPropertyChanged(nameof(CanEnableTerminalDirectInput));
        OnPropertyChanged(nameof(CanConnect));
        StartBridgeCommand.NotifyCanExecuteChanged();
        NotifyCommandStates();
    }

    private void RefreshModbusAvailability() { Modbus?.RefreshAvailability(); Sensor?.Refresh(); }

    private async Task SaveModbusSettingsAsync()
    {
        var before = _profileService.SaveCount;
        await SaveProfileAsync();
        if (_profileService.SaveCount == before)
            throw new InvalidOperationException(_profileService.LastError ?? "The profile could not be saved.");
    }

    private async Task StopModbusMonitorAsync()
    {
        await (Sensor?.StopAsync() ?? Task.CompletedTask);
        await (_modbusService?.StopAsync() ?? Task.CompletedTask);
    }
}
