using System.ComponentModel;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    private TerminalInputSession? _terminalInputSession;
    private Task _terminalInputCleanup = Task.CompletedTask;
    private long _terminalInputSessionId;
    private long _terminalInputSerialGeneration;
    private TerminalOutputBuffer? _terminalOutput;
    private bool _terminalDirectInputLocalEcho = true;

    public bool CanEnableTerminalDirectInput => CanSendManualTx && !IsHexRxViewSelected &&
        IsXtermReady && !IsBridgeActive && !IsBridgeStarting && !IsLogRenderingPaused;

    public string TerminalDirectInputVisibility => IsHexRxViewSelected ? "Collapsed" : "Visible";

    public string TerminalDirectInputButtonText => IsTerminalDirectInputEnabled
        ? UiText.Get("TerminalDirectInputOn", "Direct input: ON")
        : UiText.Get("TerminalDirectInputOff", "Direct input: OFF");

    public string TerminalDirectInputMenuToolTip => UiText.Format("TerminalDirectInputMenuHint",
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
                _terminalInputSession = new TerminalInputSession(
                    async (text, bytes, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (_serialService.ReceiveSessionGeneration != serialGeneration || !_serialService.IsConnected)
                            throw new InvalidOperationException("The original serial connection has ended.");
                        await _serialService.SendTerminalInputAsync(bytes, serialGeneration, token).ConfigureAwait(false);
                        // Remote echo owns the on-screen cursor. Keep TX evidence on disk
                        // without adding one displayed TX row for every typed character.
                        FanOutLogLine(LogLine.Tx(text, bytes), fileEligible: true, detectEvent: true,
                            renderInView: false, protocolSession: serialGeneration);
                    },
                    message => RunOnUiThread(() =>
                    {
                        if (sessionId != _terminalInputSessionId || !IsTerminalDirectInputEnabled) return;
                        IsTerminalDirectInputEnabled = false;
                        RecordTxError(message);
                    }));
            }
            else
            {
                var session = _terminalInputSession!;
                _terminalInputSession = null;
                Interlocked.Exchange(ref _terminalOutput, null)?.Stop();
                session.Stop();
                _terminalInputCleanup = Task.WhenAll(_terminalInputCleanup, DisposeTerminalInputAsync(session));
            }
            OnPropertyChanged();
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
        if (!IsTerminalDirectInputEnabled || sessionId != _terminalInputSessionId) return;
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

        if (!CanEnableTerminalDirectInput || args.PropertyName == nameof(SelectedRxEncoding))
            IsTerminalDirectInputEnabled = false;
        OnPropertyChanged(nameof(CanEnableTerminalDirectInput));
        OnPropertyChanged(nameof(TerminalDirectInputVisibility));
    }

    private static async Task DisposeTerminalInputAsync(TerminalInputSession session)
    {
        try { await session.DisposeAsync(); }
        catch (Exception ex) { RuntimeDiagnostics.RecordError("TerminalInput.Dispose", ex); }
    }
}
