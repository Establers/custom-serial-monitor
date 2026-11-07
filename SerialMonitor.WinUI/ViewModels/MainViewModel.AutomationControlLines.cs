using System.Text.Json;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    private object ReadAutomationControlLines(JsonElement arguments)
    {
        if (arguments.String("port") is { } port &&
            !string.Equals(port, AutomationState().Port, StringComparison.OrdinalIgnoreCase))
            throw new AutomationException("port_mismatch", "The current port does not match the expected port.");
        var status = _serialService.ControlLineStatus;
        return JsonSerializer.SerializeToElement(new
        {
            connected = _serialService.IsConnected, port = AutomationState().Port,
            sessionGeneration = _serialService.ReceiveSessionGeneration,
            dtr = OutputModeText(status.Dtr), rts = OutputModeText(status.Rts),
            inputStatus = status.InputStatus.ToString(),
            cts = status.Cts, dsr = status.Dsr, dcd = status.Dcd, ri = status.Ri, error = status.Error
        }, new JsonSerializerOptions(AutomationJson.Options)
        { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never });
    }

    private async Task<object> ChangeAutomationControlLineAsync(JsonElement arguments, CancellationToken token)
    {
        var change = AutomationControlLineChange.Parse(arguments);
        if (!IsAutomationBusy) throw new AutomationException("busy", "No control reservation.");
        _ = ReadAutomationControlLines(arguments);
        if (!_serialService.IsConnected) throw new AutomationException("disconnected", "Connect the intended port first.");
        var generation = change.SessionGeneration ?? _serialService.ReceiveSessionGeneration;
        if (generation != _serialService.ReceiveSessionGeneration)
            throw new AutomationException("session_changed", "The original connection has ended.");
        _ = await RunOrderedSerialChangeAsync(ct => change.Line == "rts_toggle"
            ? _serialService.SetRtsToggleAsync(change.Enabled, generation, ct)
            : _serialService.SetControlLineAsync(change.Line == "dtr" ? SerialControlLine.Dtr : SerialControlLine.Rts,
                change.Enabled, generation, ct), token);
        if (_serialService.ReceiveSessionGeneration != generation)
            throw new AutomationException("session_changed", "The original connection has ended.");
        if (_serialService.AppliedConfiguration is { } configuration)
        {
            configuration.ApplyTo(_currentSerialSettings);
            if (_lastSuccessfulSerialSettings is { } previous) configuration.ApplyTo(previous);
        }
        RefreshSerialControlLineProperties();
        FanOutLogLine(LogLine.System($"{change.Line}: {(change.Enabled ? "ON" : "OFF")} (AI/CLI control line)."),
            fileEligible: true, detectEvent: false);
        RecordSettingsChange(change.Line, SettingsApplyBehavior.Immediate, change.Enabled ? "enabled" : "disabled");
        return ReadAutomationControlLines(arguments);
    }
}
