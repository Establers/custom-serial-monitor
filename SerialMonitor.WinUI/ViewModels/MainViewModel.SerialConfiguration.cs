using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    private Task<T> RunOrderedSerialChangeAsync<T>(Func<CancellationToken, Task<T>> change, CancellationToken token) =>
        _terminalInputSession is { } input ? input.RunOrderedAsync(change, token) : change(token);
    private bool _serialConfigurationBusy;
    private bool _automationSerialControl;

    public bool CanEditSerialParameters => !IsShuttingDown && !IsBusy && !IsModbusBusy && !IsAutoReconnectRunning &&
        !IsAutomationBusy && !_serialControlLineBusy && !_serialConfigurationBusy && !IsSequenceRunning &&
        !_isProfileSaveActive && !IsBridgeStarting && !IsBridgeActive;

    private bool RequestLiveSerialParameter(Func<SerialPortConfiguration, SerialPortConfiguration> change)
    {
        if (!IsConnected) return false;
        if (CanEditSerialParameters)
            _ = ChangeSerialConfigurationFromGuiAsync(change(_serialService.AppliedConfiguration ??
                SerialPortConfiguration.FromSettings(_currentSerialSettings)));
        else RefreshSerialParameterProperties();
        return true;
    }

    private async Task ChangeSerialConfigurationFromGuiAsync(SerialPortConfiguration requested)
    {
        var generation = _serialService.ReceiveSessionGeneration;
        var token = _connectionCancellation?.Token ?? CancellationToken.None;
        _serialConfigurationBusy = true;
        _activeGuiSends++;
        NotifyCommandStates();
        try
        {
            var applied = await RunOrderedSerialChangeAsync(ct => _serialService.ConfigureAsync(requested, generation, ct), token);
            if (IsConnected && _serialService.ReceiveSessionGeneration == generation)
                ApplyConfirmedSerialConfiguration(applied);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            Infrastructure.RuntimeDiagnostics.RecordError("SerialConfiguration.Change", ex);
        }
        finally
        {
            _activeGuiSends--;
            _serialConfigurationBusy = false;
            RefreshSerialParameterProperties();
            NotifyCommandStates();
        }
    }

    private void ApplyConfirmedSerialConfiguration(SerialPortConfiguration applied)
    {
        applied.ApplyTo(_currentSerialSettings);
        if (_lastSuccessfulSerialSettings is { } previous) applied.ApplyTo(previous);
        _selectedBaudRate = applied.BaudRate;
        _lastSuccessfulBaudRate = applied.BaudRate;
        if (!BaudRates.Contains(applied.BaudRate)) BaudRates.Add(applied.BaudRate);
        RefreshAutomaticHexGroupTimeoutDefault();
        RefreshSerialParameterProperties();
        NotifyConnectionSelectionCommandState();
        var summary = $"{applied.BaudRate} bps, {applied.DataBits} data bits, {applied.Parity}, {applied.StopBits} stop bits, {applied.Handshake}";
        RecordSettingsChange("Serial port", SettingsApplyBehavior.Immediate, summary);
        FanOutLogLine(LogLine.System("Serial configuration: " + summary), fileEligible: true, detectEvent: false);
        RestartSerialBusUtilizationMeasurement("serial configuration changed");
        SetStatus("Serial configuration applied: " + summary);
    }

    private void RefreshSerialParameterProperties()
    {
        foreach (var name in new[] { nameof(CanEditSerialParameters), nameof(SelectedBaudRate), nameof(SelectedDataBits),
            nameof(SelectedParity), nameof(SelectedStopBits), nameof(SelectedHandshake), nameof(ConnectionStateText),
            nameof(CompactConnectionStatusText), nameof(HexGroupTimeoutRecommendationText) }) OnPropertyChanged(name);
        RefreshSerialControlLineProperties();
    }
}
