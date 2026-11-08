using System.ComponentModel;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    private TerminalInputSession? _terminalInputSession;
    private TerminalTransmitHistory? _terminalTransmitHistory;
    private Task _terminalInputCleanup = Task.CompletedTask;
    private long _terminalInputSessionId;
    private long _terminalInputSerialGeneration;
    private TerminalOutputBuffer? _terminalOutput;
    private bool _terminalDirectInputLocalEcho = true;

    // Keystrokes and driver changes share the service's write gate. A pending
    // control-line/configuration operation must not tear down the input session.
    public bool CanEnableTerminalDirectInput => !IsShuttingDown && IsConnected && !IsBusy && !IsManualTxBusy &&
        (!IsAutomationBusy || _automationSerialControl) && !IsHexRxViewSelected &&
        IsXtermReady && !IsBridgeActive && !IsBridgeStarting && !IsLogRenderingPaused;

    public bool IsTerminalDirectInputLocked => IsTerminalDirectInputEnabled && IsSequenceRunning;

    public string TerminalDirectInputVisibility => IsHexRxViewSelected ? "Collapsed" : "Visible";

    public string TerminalDirectInputButtonText => IsTerminalDirectInputEnabled
        ? UiText.Get("TerminalDirectInputOn", "Direct input: ON")
        : UiText.Get("TerminalDirectInputOff", "Direct input: OFF");

    public string TerminalDirectInputMenuToolTip => IsTerminalDirectInputLocked
        ? UiText.Get("TerminalDirectInputSequenceLocked", "Direct input: ON. Keyboard input is locked while the sequence runs.")
        : UiText.Format("TerminalDirectInputMenuHint",
        "{0}. Open the menu to switch direct input or local echo.", TerminalDirectInputButtonText);

    public bool TerminalDirectInputLocalEcho
    {
        get => _terminalDirectInputLocalEcho;
        set => SetProperty(ref _terminalDirectInputLocalEcho, value);
    }

    public bool IsTerminalDirectInputEnabled
    {
        get => _terminalInputSession is not null;
        set
        {
            if (value == IsTerminalDirectInputEnabled) return;
            if (value)
            {
                if (!CanEnableTerminalDirectInput || !_serialService.IsConnected) return;
                _terminalInputSerialGeneration = _serialService.ReceiveSessionGeneration;
                var serialGeneration = _terminalInputSerialGeneration;
                var sessionId = ++_terminalInputSessionId;
                Volatile.Write(ref _terminalOutput, new TerminalOutputBuffer(serialGeneration, SelectedRxEncoding));
                var visibleGeneration = Interlocked.Read(ref _visibleLogGeneration);
                var history = new TerminalTransmitHistory(line => RetainTerminalTransmit(line, visibleGeneration));
                _terminalTransmitHistory = history;
                _terminalInputSession = new TerminalInputSession(
                    async (text, bytes, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (_serialService.ReceiveSessionGeneration != serialGeneration || !_serialService.IsConnected)
                            throw new InvalidOperationException("The original serial connection has ended.");
                        await _serialService.SendTerminalInputAsync(bytes, serialGeneration, token).ConfigureAwait(false);
                        // Raw TX evidence is independent of coalesced retained UI records.
                        FanOutLogLine(LogLine.Tx(text, bytes), fileEligible: true, detectEvent: true,
                            renderInView: false, protocolSession: serialGeneration);
                        history.Sent(text, DateTimeOffset.Now);
                    },
                    message => RunOnUiThread(() =>
                    {
                        if (sessionId != _terminalInputSessionId || !IsTerminalDirectInputEnabled) return;
                        IsTerminalDirectInputEnabled = false;
                        RecordTxError(message);
                    }));
                _terminalInputSession.SetInputLocked(IsSequenceRunning);
            }
            else
            {
                var session = _terminalInputSession!;
                var history = _terminalTransmitHistory!;
                _terminalTransmitHistory = null;
                _terminalInputSession = null;
                Interlocked.Exchange(ref _terminalOutput, null)?.Stop();
                session.Stop();
                _terminalInputCleanup = Task.WhenAll(_terminalInputCleanup, DisposeTerminalInputAsync(session, history));
            }
            ResetTerminalScreenSearch();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTerminalDirectInputLocked));
            OnPropertyChanged(nameof(TerminalDirectInputButtonText));
            OnPropertyChanged(nameof(TerminalDirectInputMenuToolTip));
            OnPropertyChanged(nameof(CanStartBridge));
            StartBridgeCommand.NotifyCanExecuteChanged();
        }
    }

    public long TerminalDirectInputSessionId => _terminalInputSessionId;

    private void OnTerminalBytesReceived(long generation, ReceivedByteChunk chunk) =>
        Volatile.Read(ref _terminalOutput)?.Offer(generation, chunk);

    public string DrainTerminalOutput(long sessionId)
    {
        if (sessionId != _terminalInputSessionId || !IsTerminalDirectInputEnabled) return string.Empty;
        if (!_serialService.IsConnected || _serialService.ReceiveSessionGeneration != _terminalInputSerialGeneration)
        {
            IsTerminalDirectInputEnabled = false;
            return string.Empty;
        }
        var text = _terminalOutput!.Drain(out var error);
        if (error is not null)
        {
            IsTerminalDirectInputEnabled = false;
            RecordTxError(error);
        }
        return text;
    }

    public void QueueTerminalDirectInput(long sessionId, string text)
    {
        if (!IsTerminalDirectInputEnabled || sessionId != _terminalInputSessionId || IsTerminalDirectInputLocked) return;
        if (!CanEnableTerminalDirectInput || !_serialService.IsConnected ||
            _serialService.ReceiveSessionGeneration != _terminalInputSerialGeneration)
        {
            IsTerminalDirectInputEnabled = false;
            return;
        }
        _terminalInputSession?.TryEnqueue(text);
    }

    private void OnTerminalInputStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(CanSendManualTx) or nameof(IsConnected) or nameof(IsBusy) or
            nameof(IsSequenceRunning) or nameof(IsAutomationBusy) or nameof(SelectedRxDisplayMode) or
            nameof(IsXtermReady) or nameof(IsBridgeActive) or nameof(IsBridgeStarting) or
            nameof(IsLogRenderingPaused) or nameof(SelectedRxEncoding))) return;

        _terminalInputSession?.SetInputLocked(IsSequenceRunning);
        OnPropertyChanged(nameof(IsTerminalDirectInputLocked));
        OnPropertyChanged(nameof(TerminalDirectInputMenuToolTip));
        if (!CanEnableTerminalDirectInput || args.PropertyName == nameof(SelectedRxEncoding))
            IsTerminalDirectInputEnabled = false;
        OnPropertyChanged(nameof(CanEnableTerminalDirectInput));
        OnPropertyChanged(nameof(TerminalDirectInputVisibility));
    }

    private void RetainTerminalTransmit(LogLine line, long visibleGeneration)
    {
        lock (_viewPauseGate)
        {
            if (visibleGeneration == _visibleLogGeneration &&
                !_viewPause.ClassifyRecord(false, FileLoggingEnabled, FileLoggingWhileViewPaused).OmitFromView)
                PostAcceptedVisualLog(line);
        }
    }

    private async Task PrepareTerminalInputForSequenceAsync(CancellationToken token)
    {
        var session = _terminalInputSession;
        var history = _terminalTransmitHistory;
        if (session is null || history is null) return;
        // IsSequenceRunning locks both the native receiver and JS immediately.
        // Drain accepted keys before a sequence can acquire the serial write gate.
        await session.RunOrderedAsync(_ =>
        {
            if (history.MayHavePendingCommand)
                throw new InvalidOperationException("Finish the terminal input with Enter or cancel it with Ctrl+C before running a sequence.");
            history.Flush();
            return Task.FromResult(true);
        }, token);
    }

    private static async Task DisposeTerminalInputAsync(TerminalInputSession session, TerminalTransmitHistory history)
    {
        try { await session.DisposeAsync(); }
        catch (Exception ex) { RuntimeDiagnostics.RecordError("TerminalInput.Dispose", ex); }
        finally
        {
            try { history.Flush(); }
            catch (Exception ex) { RuntimeDiagnostics.RecordError("TerminalInput.HistoryFlush", ex); }
        }
    }
}
