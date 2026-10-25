using System.Text.Json;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    private RxCaptureService? _captures;
    private AutomationPipeHost? _automationHost;
    private readonly AutomationControlGate _automationControl = new();
    private int _activeGuiSends;
    private string? _automationLastResult;
    private string _automationStatus = UiText.Get("AutomationReady", "AI/CLI ready");

    public bool IsAutomationBusy => _automationControl.IsAutomationReserved;
    public bool IsBridgeStarting => _automationControl.IsBridgeStarting;
    public string AutomationStatusText => _automationStatus;
    public Infrastructure.AsyncRelayCommand CancelAutomationCommand { get; private set; } = null!;

    private void StartAutomation()
    {
        CancelAutomationCommand = new Infrastructure.AsyncRelayCommand(() =>
        {
            _captures?.Cancel();
            return Task.CompletedTask;
        }, () => IsAutomationBusy);
        try
        {
            var instanceId = RuntimeInstanceStorage.Current.InstanceId;
            _captures = new RxCaptureService(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SerialMonitor", "automation", "captures", instanceId));
            _serialService.CaptureBytesReceived += OnCaptureBytes;
            _serialService.StatusChanged += OnCaptureConnectionChanged;
            var controller = new AutomationController(new AutomationDeviceAdapter(this), _captures);
            _automationHost = new AutomationPipeHost(instanceId, controller.HandleAsync, ex =>
            {
                RuntimeDiagnostics.RecordError("AutomationPipeHost", ex);
                RunOnUiThread(() => SetAutomationStatus("AI/CLI error: " + ex.Message));
            });
            _automationHost.Start();
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("Automation.Start", ex);
            SetAutomationStatus("AI/CLI unavailable: " + ex.Message);
        }
    }

    private async Task StopAutomationAsync()
    {
        _captures?.Cancel();
        if (_automationHost is not null) await _automationHost.DisposeAsync();
        _serialService.CaptureBytesReceived -= OnCaptureBytes;
        _serialService.StatusChanged -= OnCaptureConnectionChanged;
        if (_captures is not null) await _captures.DisposeAsync();
    }

    private void OnCaptureBytes(long generation, ReceivedByteChunk chunk) =>
        _captures?.Offer(generation, chunk.Bytes, chunk.ReceivedTimestamp);

    private CaptureConnection CaptureConnectionSnapshot => new(_serialService.IsConnected,
        _serialService.ReceiveSessionGeneration, _serialService.SerialFrameErrorCount + _serialService.SerialParityErrorCount +
        _serialService.SerialOverrunErrorCount + _serialService.SerialRxOverErrorCount);

    private void OnCaptureConnectionChanged(object? sender, EventArgs args) =>
        _captures?.ConnectionChanged(CaptureConnectionSnapshot);

    private AutomationDeviceState AutomationState()
    {
        var settings = _lastSuccessfulSerialSettings ?? _currentSerialSettings;
        return new(_serialService.IsConnected, settings.PortName, settings.BaudRate,
            _serialService.ReceiveSessionGeneration, _currentSerialSettings.Encoding.ToString(),
            CaptureConnectionSnapshot.SerialErrorCount, IsBusy || IsSequenceRunning || IsBridgeActive || IsBridgeStarting ||
            IsAutoReconnectRunning || IsAutomationBusy || _activeGuiSends > 0, _serialService.LastError,
            settings.DataBits, settings.Parity.ToString(), settings.StopBits.ToString(), settings.Handshake.ToString());
    }

    private void SetAutomationStatus(string text)
    {
        _automationStatus = text;
        OnPropertyChanged(nameof(AutomationStatusText));
        OnPropertyChanged(nameof(IsAutomationBusy));
        OnPropertyChanged(nameof(CanSendManualTx));
        OnPropertyChanged(nameof(CanStartBridge));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanToggleConnection));
        OnPropertyChanged(nameof(CanEditConnectionSettings));
        ConnectCommand.NotifyCanExecuteChanged();
        ToggleConnectionCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        SendSavedCommandCommand.NotifyCanExecuteChanged();
        RunCommandSequenceCommand.NotifyCanExecuteChanged();
        StartBridgeCommand.NotifyCanExecuteChanged();
        CancelAutomationCommand.NotifyCanExecuteChanged();
    }

    private async Task<object> InvokeAutomationOnUiAsync(string operation, JsonElement args, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _shutdownStarted) != 0 && operation != "release")
            throw new AutomationException("shutting_down", "The GUI is shutting down.");
        if (operation != "release") await InitializeAsync();
        cancellationToken.ThrowIfCancellationRequested();
        switch (operation)
        {
            case "status": return AutomationState();
            case "ports":
                var ports = await _serialService.GetAvailablePortsAsync(cancellationToken);
                return new { ports };
            case "list_commands":
                return new
                {
                    commands = Commands.SavedCommands.Select(c => new
                    {
                        c.Name, command = c.CommandText, txMode = "text",
                        lineEnding = (c.LineEndingMode ?? SelectedTxLineEnding).ToString().ToLowerInvariant(),
                        recommendedDurationMs = 2000
                    }).ToArray()
                };
            case "reserve":
                if (AutomationState().Busy || !_automationControl.TryReserveAutomation())
                    throw new AutomationException("busy", "GUI TX, sequence, bridge, reconnect, connection change or another AI/CLI operation is active.");
                _automationLastResult = null;
                SetAutomationStatus(UiText.Get("AutomationActive", "AI/CLI active — RX capture / control"));
                return AutomationState();
            case "release":
                _automationControl.ReleaseAutomation();
                SetAutomationStatus(_automationLastResult ?? UiText.Get("AutomationReady", "AI/CLI ready"));
                return AutomationState();
            case "capture_result":
                var captureStatus = args.String("captureStatus") ?? "unknown";
                _automationLastResult = $"AI/CLI {captureStatus}: {args.GetProperty("storedBytes").GetInt64():N0} B";
                if (args.String("error") is { } captureError)
                {
                    SetStatus("AI/CLI: " + captureError);
                    RuntimeDiagnostics.RecordError("Automation.Capture", new IOException(captureError));
                }
                return new { recorded = true };
            case "connect":
            {
                var port = args.String("port") ?? throw new AutomationException("invalid_argument", "port is required.");
                var baud = args.Integer("baudRate", 115200);
                if (!BaudRates.Contains(baud)) throw new AutomationException("invalid_argument", "Unsupported baudRate.");
                return await AutomationConnectionOperation.ConnectAsync(port, baud, AutomationState, RefreshPortsAsync,
                    () =>
                    {
                        var displayPort = PortNames.FirstOrDefault(p => string.Equals(GetActualPortName(p), port, StringComparison.OrdinalIgnoreCase));
                        if (displayPort is null) throw new AutomationException("port_not_found", "Port is not available in this GUI instance.");
                        SelectedPort = displayPort;
                        SelectedBaudRate = baud;
                    }, token => ConnectAsync(token, isAutomation: true), cancellationToken);
            }
            case "disconnect":
                await DisconnectAsync(cancellationToken);
                return AutomationState();
            case "validate_send":
                var requestedText = args.String("command") ?? throw new AutomationException("invalid_argument", "command is required.");
                if (args.String("txMode", "text") == "hex" && !HexPayloadParser.TryParse(requestedText, out _, out var hexError))
                    throw new AutomationException("invalid_argument", hexError);
                return new { valid = true };
            case "send":
            {
                if (!IsAutomationBusy) throw new AutomationException("busy", "No control reservation.");
                var command = args.String("command") ?? throw new AutomationException("invalid_argument", "command is required.");
                SetAutomationStatus("AI/CLI RX: " + (command.Length > 40 ? command[..40] + "…" : command));
                if (!Enum.TryParse<TxLineEndingMode>(args.String("lineEnding", "crlf"), true, out var ending))
                    throw new AutomationException("invalid_argument", "Invalid lineEnding.");
                var mode = args.String("txMode", "text") == "hex" ? TxSendMode.Hex : TxSendMode.Terminal;
                var sent = await SendCommandAsync(new TxCommand("AI/CLI", command) { LineEndingMode = ending },
                    addToHistory: true, modeOverride: mode, cancellationToken: cancellationToken, isAutomation: true);
                if (!sent) throw new AutomationException("send_error", "TX failed. Inspect GUI health/status. Do not automatically retry.");
                return new { sent = true };
            }
            default: throw new AutomationException("unknown_operation", "Unsupported GUI operation.");
        }
    }

    private sealed class AutomationDeviceAdapter(MainViewModel owner) : IAutomationDevice
    {
        public CaptureConnection Connection => owner.CaptureConnectionSnapshot;

        public Task<object> InvokeAsync(string operation, JsonElement args, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            async void Run()
            {
                try { completion.TrySetResult(await owner.InvokeAutomationOnUiAsync(operation, args, cancellationToken)); }
                catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
                catch (Exception ex) { completion.TrySetException(ex); }
            }
            if (owner._dispatcherQueue.HasThreadAccess) Run();
            else if (!owner._dispatcherQueue.TryEnqueue(Run))
                completion.TrySetException(new AutomationException("gui_unavailable", "GUI dispatcher is unavailable."));
            return completion.Task;
        }
    }
}
