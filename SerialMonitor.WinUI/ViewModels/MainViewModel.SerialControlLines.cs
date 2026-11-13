using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    private bool _serialControlLineBusy;
    private string? _serialControlLineError;
    private SerialControlLineStatus _serialControlLineStatus = SerialControlLineStatus.Disconnected;
    private long _serialControlLineErrorGeneration;
    private SerialControlLineUiState? _lastSerialControlLineUiState;

    private readonly record struct SerialControlLineUiState(
        SerialControlLineStatus Status, bool Connected,
        SerialControlLineMode DtrMode, SerialControlLineMode RtsMode,
        bool DtrEnabled, bool RtsEnabled, bool RtsToggle,
        bool CanEditParameters, bool CanEditDtr, bool CanEditRts, bool CanEditToggle,
        bool Busy, string Hint);

    public bool IsSerialControlLineBusy => _serialControlLineBusy;
    private bool CanChangeSerialControlLines => !IsShuttingDown && !IsBusy && !IsModbusBusy && !IsAutoReconnectRunning &&
        !IsAutomationBusy && !_serialControlLineBusy && !_serialConfigurationBusy && !IsSequenceRunning &&
        !_isProfileSaveActive && !IsBridgeStarting;
    public bool CanEditDtrControl => CanChangeSerialControlLines && (IsConnected
        ? SerialControlLinePolicy.IsManual(_serialControlLineStatus.Dtr)
        : !SerialControlLinePolicy.UsesDtrHandshake(_currentSerialSettings.Handshake));
    public bool CanEditRtsControl => CanChangeSerialControlLines && (IsConnected
        ? SerialControlLinePolicy.IsManual(_serialControlLineStatus.Rts)
        : !SerialControlLinePolicy.UsesRtsHandshake(_currentSerialSettings.Handshake) && !RtsToggleOnTransmit);
    public bool CanEditRtsToggle => CanChangeSerialControlLines && (IsConnected
        ? _serialControlLineStatus.Rts is SerialControlLineMode.Off or SerialControlLineMode.On or SerialControlLineMode.Toggle
        : !SerialControlLinePolicy.UsesRtsHandshake(_currentSerialSettings.Handshake));

    public bool RtsToggleOnTransmit
    {
        get => _currentSerialSettings.RtsToggleOnTransmit;
        set
        {
            if (value == RtsToggleOnTransmit) return;
            if (!CanEditRtsToggle) { RefreshSerialControlLineProperties(); return; }
            if (!IsConnected)
            {
                _currentSerialSettings.RtsToggleOnTransmit = value;
                RecordSettingsChange("RTS TX Toggle", SettingsApplyBehavior.ReconnectRequired, value ? "enabled" : "disabled");
                RefreshSerialControlLineProperties();
            }
            else _ = ChangeSerialControlLineAsync(SerialControlLine.Rts, value, toggle: true);
        }
    }

    private SerialControlLineMode DtrOutputMode => IsConnected ? _serialControlLineStatus.Dtr :
        SerialControlLinePolicy.UsesDtrHandshake(_currentSerialSettings.Handshake) ? SerialControlLineMode.Handshake :
        DtrEnable ? SerialControlLineMode.On : SerialControlLineMode.Off;
    private SerialControlLineMode RtsOutputMode => IsConnected ? _serialControlLineStatus.Rts :
        SerialControlLinePolicy.UsesRtsHandshake(_currentSerialSettings.Handshake) ? SerialControlLineMode.Handshake :
        RtsToggleOnTransmit ? SerialControlLineMode.Toggle : RtsEnable ? SerialControlLineMode.On : SerialControlLineMode.Off;
    public string DtrControlText => "DTR " + OutputModeText(DtrOutputMode);
    public string RtsControlText => "RTS " + OutputModeText(RtsOutputMode);
    public bool? DtrControlChecked
    {
        get => SerialControlLinePolicy.IsManual(DtrOutputMode) ? DtrOutputMode == SerialControlLineMode.On : null;
        set { if (value is { } enabled) DtrEnable = enabled; }
    }
    public bool? RtsControlChecked
    {
        get => SerialControlLinePolicy.IsManual(RtsOutputMode) ? RtsOutputMode == SerialControlLineMode.On : null;
        set { if (value is { } enabled) RtsEnable = enabled; }
    }
    public string CtsStatusText => "CTS " + InputText(_serialControlLineStatus.Cts);
    public string DsrStatusText => "DSR " + InputText(_serialControlLineStatus.Dsr);
    public string DcdStatusText => "DCD " + InputText(_serialControlLineStatus.Dcd);
    public string RiStatusText => "RI " + InputText(_serialControlLineStatus.Ri);
    public string SerialControlLineHint => _serialControlLineError ??
        (!IsConnected ? UiText.Get("SerialLinesDisconnected", "Disconnected; output settings apply on connection.") :
        _serialControlLineBusy ? UiText.Get("SerialLinesApplying", "Applying control-line change…") :
        _serialControlLineStatus.InputStatus == SerialModemStatusKind.Mock
            ? UiText.Get("SerialLinesMock", "MOCK: output modes are simulated; physical inputs are unavailable.") :
        _serialControlLineStatus.Error ?? UiText.Get("SerialLinesLive", "Live driver status · AUTO/TX outputs are controlled by the driver."));
    public string SerialControlLineToolTip => SerialInputToolTip;

    private static string OutputModeText(SerialControlLineMode mode) => mode switch
    {
        SerialControlLineMode.On => "ON", SerialControlLineMode.Off => "OFF",
        SerialControlLineMode.Handshake => "AUTO", SerialControlLineMode.Toggle => "TX", _ => "N/A"
    };
    private string InputText(bool? value) => !IsConnected ? "—" : value is true ? "ON" : value is false ? "OFF" : "N/A";

    private void RequestSerialControlLineChange(SerialControlLine line, bool enabled)
    {
        if (enabled == (line == SerialControlLine.Dtr ? DtrEnable : RtsEnable)) return;
        if (!(line == SerialControlLine.Dtr ? CanEditDtrControl : CanEditRtsControl))
        { RefreshSerialControlLineProperties(); return; }
        if (IsConnected) { _ = ChangeSerialControlLineAsync(line, enabled, toggle: false); return; }
        if (line == SerialControlLine.Dtr) _currentSerialSettings.DtrEnable = enabled;
        else _currentSerialSettings.RtsEnable = enabled;
        RecordSettingsChange(line.ToString(), SettingsApplyBehavior.ReconnectRequired, enabled ? "enabled" : "disabled");
        RefreshSerialControlLineProperties();
    }

    private async Task ChangeSerialControlLineAsync(SerialControlLine line, bool enabled, bool toggle)
    {
        var generation = _serialService.ReceiveSessionGeneration;
        var token = _connectionCancellation?.Token ?? CancellationToken.None;
        _serialControlLineBusy = true;
        _serialControlLineError = null;
        _serialControlLineErrorGeneration = generation;
        _activeGuiSends++; // Fence handover to AI/CLI while a driver mutation is pending.
        NotifyCommandStates();
        try
        {
            var applied = await RunOrderedSerialChangeAsync(ct => toggle
                ? _serialService.SetRtsToggleAsync(enabled, generation, ct)
                : _serialService.SetControlLineAsync(line, enabled, generation, ct), token);
            if (!IsConnected || _serialService.ReceiveSessionGeneration != generation) return;
            if (toggle)
            {
                _currentSerialSettings.RtsToggleOnTransmit = applied.Rts == SerialControlLineMode.Toggle;
                if (_lastSuccessfulSerialSettings is { } previous) previous.RtsToggleOnTransmit = _currentSerialSettings.RtsToggleOnTransmit;
            }
            else
            {
                var on = (line == SerialControlLine.Dtr ? applied.Dtr : applied.Rts) == SerialControlLineMode.On;
                if (line == SerialControlLine.Dtr)
                {
                    _currentSerialSettings.DtrEnable = on;
                    if (_lastSuccessfulSerialSettings is { } previous) previous.DtrEnable = on;
                }
                else
                {
                    _currentSerialSettings.RtsEnable = on;
                    if (_lastSuccessfulSerialSettings is { } previous) previous.RtsEnable = on;
                }
            }
            var name = toggle ? "RTS TX Toggle" : line.ToString();
            RecordSettingsChange(name, SettingsApplyBehavior.Immediate, enabled ? "enabled" : "disabled");
            FanOutLogLine(LogLine.System($"{name}: {(enabled ? "ON" : "OFF")} (serial control line changed)."),
                fileEligible: true, detectEvent: false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _serialControlLineError = ex.Message;
            SetStatus(ex.Message);
            RuntimeDiagnostics.RecordError("SerialControlLines.Change", ex);
        }
        finally
        {
            _activeGuiSends--;
            _serialControlLineBusy = false;
            NotifyCommandStates();
        }
    }

    private void RefreshSerialControlLineProperties(bool onlyIfChanged = false)
    {
        _serialControlLineStatus = _serialService?.ControlLineStatus ?? SerialControlLineStatus.Disconnected;
        if (_serialControlLineErrorGeneration != _serialControlLineStatus.Generation)
            _serialControlLineError = null;
        if (onlyIfChanged)
        {
            var state = new SerialControlLineUiState(
                _serialControlLineStatus, IsConnected, DtrOutputMode, RtsOutputMode,
                DtrEnable, RtsEnable, RtsToggleOnTransmit,
                CanEditSerialParameters, CanEditDtrControl, CanEditRtsControl, CanEditRtsToggle,
                IsSerialControlLineBusy, SerialControlLineHint);
            if (_lastSerialControlLineUiState == state) return;
            _lastSerialControlLineUiState = state;
        }
        else
        {
            // Preserve explicit refreshes, including teardown and rejected-edit
            // restoration, without adding getter reads to those lifecycle paths.
            // Re-baseline on the next poll rather than retain an older UI state.
            _lastSerialControlLineUiState = null;
        }
        foreach (var property in new[] { nameof(CanEditSerialParameters), nameof(DtrEnable), nameof(RtsEnable), nameof(RtsToggleOnTransmit),
            nameof(DtrControlChecked), nameof(RtsControlChecked),
            nameof(CanEditDtrControl), nameof(CanEditRtsControl), nameof(CanEditRtsToggle), nameof(IsSerialControlLineBusy),
            nameof(DtrControlText), nameof(RtsControlText), nameof(CtsStatusText), nameof(DsrStatusText),
            nameof(DcdStatusText), nameof(RiStatusText), nameof(SerialControlLineHint) })
            OnPropertyChanged(property);
    }
}
