using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    private IModbusRtuMonitorService? _modbusService;
    public ModbusRtuViewModel? Modbus { get; private set; }
    public bool IsModbusBusy => Modbus?.IsBusy == true;

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
    }

    private bool CanStartModbusOperation() => !IsShuttingDown && _serialService.IsConnected && !IsBusy &&
        !IsSequenceRunning && !IsAutomationBusy && !IsBridgeActive && !IsBridgeStarting && !IsAutoReconnectRunning &&
        _activeGuiSends == 0 && !_serialConfigurationBusy && !_serialControlLineBusy && !_isProfileSaveActive &&
        !IsTerminalDirectInputEnabled && _terminalInputCleanup.IsCompleted && !_serialService.IsMockStressRunning &&
        (_serialService.AppliedConfiguration?.DataBits ?? 8) == 8;

    private string ModbusUnavailableReason()
    {
        if (!_serialService.IsConnected)
            return UiText.Get("ModbusMonitorNeedConnection", "Connect a serial port before reading or writing.");
        if ((_serialService.AppliedConfiguration?.DataBits ?? 8) != 8)
            return UiText.Get("ModbusMonitorNeedEightBits", "Modbus RTU requires 8 data bits. Correct the serial settings first.");
        if (IsTerminalDirectInputEnabled)
            return UiText.Get("ModbusMonitorDisableDirectInput", "Turn off terminal direct input before starting Modbus.");
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

    private void RefreshModbusAvailability() => Modbus?.RefreshAvailability();

    private async Task SaveModbusSettingsAsync()
    {
        var before = _profileService.SaveCount;
        await SaveProfileAsync();
        if (_profileService.SaveCount == before)
            throw new InvalidOperationException(_profileService.LastError ?? "The profile could not be saved.");
    }

    private Task StopModbusMonitorAsync() => _modbusService?.StopAsync() ?? Task.CompletedTask;
}
