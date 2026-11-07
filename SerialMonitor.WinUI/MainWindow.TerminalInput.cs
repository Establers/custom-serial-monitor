using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace SerialMonitor.WinUI;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _terminalOutputTimer;
    private bool _terminalOutputPumpRunning;
    private long _displayedDirectInputSessionId;
    private bool _terminalInputMenuOpen;

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
            if (!IsXtermVisualAppendSuspended() &&
                !await WaitForXtermAppendQueueIdleAsync(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("The terminal display is still processing previous input.");
            if (IsClosingOrClosed || enabled != _viewModel.IsTerminalDirectInputEnabled ||
                sessionId != _viewModel.TerminalDirectInputSessionId) return;
            restoreLog = !enabled && _displayedDirectInputSessionId != 0;
            var result = await ExecuteXtermScriptAsync(
                $"window.serialMonitorSetDirectInput && window.serialMonitorSetDirectInput({(enabled ? "true" : "false")}, {sessionId}, {(_viewModel.TerminalDirectInputLocalEcho ? "true" : "false")});");
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
                sessionId == _viewModel.TerminalDirectInputSessionId && !_terminalInputMenuOpen)
                await FocusXtermDirectInputAsync();
        }
        catch (Exception ex)
        {
            _viewModel.IsTerminalDirectInputEnabled = false;
            _viewModel.RecordXtermLayoutError($"xterm direct input update failed: {ex.Message}");
        }
        finally { _xtermAppendGate.Release(); }
        if (restoreLog) QueueFullXtermRerender("Direct input ended", debounce: false);
    }

    private async Task FocusXtermDirectInputAsync()
    {
        if (IsClosingOrClosed || !_isXtermReady || _terminalInputMenuOpen ||
            !_viewModel.IsTerminalDirectInputEnabled ||
            _displayedDirectInputSessionId != _viewModel.TerminalDirectInputSessionId) return;
        try
        {
            // Native focus comes first. Giving focus to WebView2 can replace
            // the DOM focus established before the flyout finished closing.
            if (!XtermLogWebView.Focus(FocusState.Programmatic))
                throw new InvalidOperationException("The terminal could not receive keyboard focus.");
            var result = await ExecuteXtermScriptAsync(
                $"window.serialMonitorFocusDirectInput && window.serialMonitorFocusDirectInput({_displayedDirectInputSessionId});");
            if (TryParseScriptBoolean(result) != true)
                throw new InvalidOperationException("The terminal input cursor could not be focused.");
        }
        catch (Exception ex) { _viewModel.RecordXtermLayoutError($"Direct input focus failed: {ex.Message}"); }
    }

    private async Task SyncXtermDirectInputLocalEchoAsync()
    {
        if (!_isXtermReady || IsClosingOrClosed) return;
        try
        {
            await ExecuteXtermScriptAsync(
                $"window.serialMonitorSetDirectInputLocalEcho && window.serialMonitorSetDirectInputLocalEcho({(_viewModel.TerminalDirectInputLocalEcho ? "true" : "false")});");
        }
        catch (Exception ex) { _viewModel.RecordXtermLayoutError($"Direct input echo update failed: {ex.Message}"); }
    }

    private void TerminalDirectInputMenu_Opening(object sender, object args)
    {
        _terminalInputMenuOpen = true;
        TerminalDirectInputOnMenuItem.IsChecked = _viewModel.IsTerminalDirectInputEnabled;
        TerminalDirectInputOffMenuItem.IsChecked = !_viewModel.IsTerminalDirectInputEnabled;
    }

    private void TerminalDirectInputMenu_Closed(object sender, object args)
    {
        _terminalInputMenuOpen = false;
        DispatcherQueue.TryEnqueue(() => _ = FocusXtermDirectInputAsync());
    }

    private void TerminalDirectInputOn_Click(object sender, RoutedEventArgs args) =>
        _viewModel.IsTerminalDirectInputEnabled = true;

    private void TerminalDirectInputOff_Click(object sender, RoutedEventArgs args) =>
        _viewModel.IsTerminalDirectInputEnabled = false;

    private void UpdateTerminalDirectInputIcon()
    {
        if (_viewModel.IsTerminalDirectInputEnabled &&
            Application.Current.Resources.TryGetValue("FileLoggingEnabledTextBrush", out var brush) && brush is Brush foreground)
            TerminalDirectInputMenuIcon.Foreground = foreground;
        else TerminalDirectInputMenuIcon.ClearValue(Microsoft.UI.Xaml.Controls.FontIcon.ForegroundProperty);
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

    private void HandleXtermDirectEchoError(JsonElement root)
    {
        if (!root.TryGetProperty("sessionId", out var session) || !session.TryGetInt64(out var sessionId) ||
            !_viewModel.IsTerminalDirectInputEnabled || sessionId != _viewModel.TerminalDirectInputSessionId) return;
        var error = root.TryGetProperty("error", out var detail) && detail.ValueKind == JsonValueKind.String
            ? detail.GetString() : "unknown display error";
        _viewModel.IsTerminalDirectInputEnabled = false;
        _viewModel.RecordXtermLayoutError($"Direct input local echo stopped: {error}");
    }
}
