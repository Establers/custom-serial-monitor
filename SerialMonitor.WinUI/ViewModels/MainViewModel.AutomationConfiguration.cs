using System.Globalization;
using System.Text.Json;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    private async Task<object> ConfigureAutomationSerialAsync(JsonElement args, CancellationToken cancellationToken)
    {
        // Parse all fields before any driver or UI mutation. In particular an
        // invalid timeout cannot change the baud rate as a partial side effect.
        var configuration = AutomationSerialConfiguration.Parse(args);
        if (!IsAutomationBusy || IsBusy || IsModbusBusy || IsSequenceRunning || IsBridgeActive || IsBridgeStarting ||
            IsAutoReconnectRunning || _activeGuiSends > 0 || _isProfileSaveActive)
            throw new AutomationException("busy", "GUI TX, sequence, bridge, reconnect or connection change is active.");
        var state = AutomationState();
        if (!state.Connected)
            throw new AutomationException("disconnected", "Connect the intended serial port before configuring it.");
        if (args.String("port") is { } expectedPort && !string.Equals(expectedPort, state.Port, StringComparison.OrdinalIgnoreCase))
            throw new AutomationException("port_mismatch", "The connected port does not match the expected port.");
        cancellationToken.ThrowIfCancellationRequested();

        if (configuration.BaudRate.HasValue || configuration.DataBits.HasValue || configuration.Parity is not null ||
            configuration.StopBits is not null || configuration.Handshake is not null)
        {
            var current = _serialService.AppliedConfiguration ?? SerialPortConfiguration.FromSettings(_currentSerialSettings);
            var handshake = configuration.Handshake is { } flow
                ? Enum.Parse<SerialHandshakeMode>(flow, ignoreCase: true) : current.Handshake;
            var requested = current with
            {
                BaudRate = configuration.BaudRate ?? current.BaudRate,
                DataBits = configuration.DataBits ?? current.DataBits,
                Parity = configuration.Parity is { } parity ? Enum.Parse<SerialParityMode>(parity, true) : current.Parity,
                StopBits = configuration.StopBits switch
                {
                    "1" => SerialStopBitsMode.One, "1.5" => SerialStopBitsMode.OnePointFive,
                    "2" => SerialStopBitsMode.Two, _ => current.StopBits
                },
                Handshake = handshake,
                RtsToggleOnTransmit = !SerialControlLinePolicy.UsesRtsHandshake(handshake) && current.RtsToggleOnTransmit
            };
            try
            {
                requested.Validate();
                var generation = _serialService.ReceiveSessionGeneration;
                var appliedConfiguration = await RunOrderedSerialChangeAsync(ct => _serialService.ConfigureAsync(requested,
                    generation, ct), cancellationToken);
                ApplyConfirmedSerialConfiguration(appliedConfiguration);
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
