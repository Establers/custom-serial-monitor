using System.Globalization;
using System.Text.Json;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    private async Task<object> ConfigureAutomationSerialAsync(JsonElement args, CancellationToken cancellationToken)
    {
        // Parse all fields before any driver or UI mutation. In particular an
        // invalid timeout cannot change the baud rate as a partial side effect.
        var configuration = AutomationSerialConfiguration.Parse(args);
        if (!IsAutomationBusy || IsBusy || IsSequenceRunning || IsBridgeActive || IsBridgeStarting ||
            IsAutoReconnectRunning || _activeGuiSends > 0 || _isProfileSaveActive)
            throw new AutomationException("busy", "GUI TX, sequence, bridge, reconnect or connection change is active.");
        var state = AutomationState();
        if (!state.Connected)
            throw new AutomationException("disconnected", "Connect the intended serial port before configuring it.");
        if (args.String("port") is { } expectedPort && !string.Equals(expectedPort, state.Port, StringComparison.OrdinalIgnoreCase))
            throw new AutomationException("port_mismatch", "The connected port does not match the expected port.");
        cancellationToken.ThrowIfCancellationRequested();

        if (configuration.BaudRate is { } requestedBaudRate)
        {
            int effectiveBaudRate;
            try
            {
                effectiveBaudRate = await _serialService.ChangeBaudRateAsync(requestedBaudRate, cancellationToken);
            }
            catch (TimeoutException ex)
            {
                throw new AutomationException("busy", ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RuntimeDiagnostics.RecordError("Automation.ConfigureSerial", ex);
                throw new AutomationException("configure_error", ex.Message);
            }

            // The driver change is committed. Finish updating the UI-owned
            // snapshot even if cancellation arrives after the successful call.
            if (!BaudRates.Contains(effectiveBaudRate)) BaudRates.Add(effectiveBaudRate);
            _selectedBaudRate = effectiveBaudRate;
            _currentSerialSettings.BaudRate = effectiveBaudRate;
            if (_lastSuccessfulSerialSettings is { } connectedSettings)
                connectedSettings.BaudRate = effectiveBaudRate;
            _lastSuccessfulBaudRate = effectiveBaudRate;
            OnPropertyChanged(nameof(SelectedBaudRate));
            OnPropertyChanged(nameof(HexGroupTimeoutRecommendationText));
            OnPropertyChanged(nameof(ConnectionStateText));
            OnPropertyChanged(nameof(CompactConnectionStatusText));
            NotifyConnectionSelectionCommandState();
            RecordSettingsChange("Baudrate", SettingsApplyBehavior.Immediate,
                effectiveBaudRate.ToString(CultureInfo.InvariantCulture));
            NotifyConnectDiagnosticsChanged();
            RestartSerialBusUtilizationMeasurement("serial baud rate changed by AI/CLI");
        }

        if (configuration.ByteTimeoutMs is { } byteTimeoutMs)
            HexGroupTimeoutMs = byteTimeoutMs;

        var applied = AutomationState();
        _automationLastResult = $"AI/CLI configured {applied.Port}: {applied.BaudRate:N0} bps, byte timeout {applied.ByteTimeoutMs} ms";
        SetAutomationStatus(_automationLastResult);
        SetStatus(_automationLastResult);
        return applied;
    }
}
