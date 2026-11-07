using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace SerialMonitor.WinUI;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _terminalOutputTimer;
    private bool _terminalOutputPumpRunning;
    private long _displayedDirectInputSessionId;

    private async Task SyncXtermDirectInputAsync()
    {
        if (!_isXtermReady || IsClosingOrClosed) return;
        var sessionId = _viewModel.TerminalDirectInputSessionId;
        var enabled = _viewModel.IsTerminalDirectInputEnabled;
        var restoreLog = false;
        await _xtermAppendGate.WaitAsync();
        try
        {
            if (IsClosingOrClosed || enabled != _viewModel.IsTerminalDirectInputEnabled ||
                sessionId != _viewModel.TerminalDirectInputSessionId) return;
            restoreLog = !enabled && _displayedDirectInputSessionId != 0;
            var result = await ExecuteXtermScriptAsync(
                $"window.serialMonitorSetDirectInput && window.serialMonitorSetDirectInput({(enabled ? "true" : "false")}, {sessionId});");
            if (TryParseScriptBoolean(result) != true)
                throw new InvalidOperationException("xterm rejected the direct input state.");
            _displayedDirectInputSessionId = enabled ? sessionId : 0;
            if (enabled)
            {
                if (_terminalOutputTimer is null)
                {
                    _terminalOutputTimer = DispatcherQueue.CreateTimer();
                    _terminalOutputTimer.Interval = TimeSpan.FromMilliseconds(16);
                    _terminalOutputTimer.Tick += OnTerminalOutputTick;
                }
                _terminalOutputTimer.Start();
            }
            else _terminalOutputTimer?.Stop();
            if (enabled && _viewModel.IsTerminalDirectInputEnabled &&
                sessionId == _viewModel.TerminalDirectInputSessionId)
                XtermLogWebView.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            _viewModel.IsTerminalDirectInputEnabled = false;
            _viewModel.RecordXtermLayoutError($"xterm direct input update failed: {ex.Message}");
        }
        finally { _xtermAppendGate.Release(); }
        if (restoreLog) QueueFullXtermRerender("Direct input ended", debounce: false);
    }

    private async void OnTerminalOutputTick(DispatcherQueueTimer sender, object args)
    {
        if (_terminalOutputPumpRunning || IsClosingOrClosed || IsXtermVisualAppendSuspended() ||
            !_viewModel.IsTerminalDirectInputEnabled || _displayedDirectInputSessionId == 0) return;
        _terminalOutputPumpRunning = true;
        var sessionId = _displayedDirectInputSessionId;
        try
        {
            await _xtermAppendGate.WaitAsync();
            try
            {
                if (!_viewModel.IsTerminalDirectInputEnabled || sessionId != _viewModel.TerminalDirectInputSessionId ||
                    sessionId != _displayedDirectInputSessionId) return;
                var text = _viewModel.DrainTerminalOutput(sessionId);
                if (text.Length == 0) return;
                if (!await ExecuteXtermAppendChunksAsync([new XtermAppendChunk(text, 0)],
                    _viewModel.IsEffectiveXtermAutoScrollEnabled, directInputSessionId: sessionId))
                    throw new InvalidOperationException("The terminal did not acknowledge received text.");
            }
            finally { _xtermAppendGate.Release(); }
        }
        catch (Exception ex)
        {
            if (_viewModel.IsTerminalDirectInputEnabled && sessionId == _viewModel.TerminalDirectInputSessionId)
            {
                _viewModel.IsTerminalDirectInputEnabled = false;
                _viewModel.RecordXtermAppendError($"Direct RX display stopped: {ex.Message}");
            }
        }
        finally { _terminalOutputPumpRunning = false; }
    }

    private void HandleXtermDirectInput(JsonElement root)
    {
        if (!root.TryGetProperty("sessionId", out var session) || !session.TryGetInt64(out var sessionId) ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String) return;
        _viewModel.QueueTerminalDirectInput(sessionId, data.GetString()!);
    }
}
