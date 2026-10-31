using System.Text.Json;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    private RxCaptureService? _captures;
    private AutomationPipeHost? _automationHost;
    private AutomationController? _automationController;
    private Task? _automationStopTask;
    private readonly AutomationControlGate _automationControl = new();
    private int _activeGuiSends;
    private bool _isProfileSaveActive;
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
            _automationController?.CancelActiveSequence();
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
            _automationController = new AutomationController(new AutomationDeviceAdapter(this), _captures);
            _automationHost = new AutomationPipeHost(instanceId, _automationController.HandleAsync, ex =>
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

    private Task StopAutomationAsync(CancellationToken cancellationToken = default)
    {
        if (_automationStopTask is null)
        {
            _automationStopTask = StopAutomationCoreAsync();
            // A timed-out caller may already have closed the window when cleanup
            // eventually fails. Observe and report that late exception as well.
            _ = _automationStopTask.ContinueWith(task =>
                RuntimeDiagnostics.RecordError("Automation.Stop", task.Exception!), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return _automationStopTask.WaitAsync(cancellationToken);
    }

    private async Task StopAutomationCoreAsync()
    {
        _automationController?.CancelActiveSequence();
        _captures?.Cancel();
        if (_automationHost is { } host)
        {
            try { await host.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { RuntimeDiagnostics.RecordError("Automation.Host.Stop", ex); }
            finally { _automationHost = null; }
        }
        if (_automationController is { } controller)
        {
            _automationController = null;
            try { await controller.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { RuntimeDiagnostics.RecordError("Automation.Controller.Stop", ex); }
        }
        _serialService.CaptureBytesReceived -= OnCaptureBytes;
        _serialService.StatusChanged -= OnCaptureConnectionChanged;
        if (_captures is { } captures)
        {
            _captures = null;
            await captures.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnCaptureBytes(long generation, ReceivedByteChunk chunk) =>
        _captures?.Offer(generation, chunk.Bytes, chunk.ReceivedTimestamp);

    private CaptureConnection CaptureConnectionSnapshot
    {
        get
        {
            // A reconnect can occur between separate state reads. Never combine
            // the old session's disconnected flag with the new generation.
            while (true)
            {
                var generation = _serialService.ReceiveSessionGeneration;
                var connected = _serialService.IsConnected;
                var errors = _serialService.SerialFrameErrorCount + _serialService.SerialParityErrorCount +
                    _serialService.SerialOverrunErrorCount + _serialService.SerialRxOverErrorCount;
                if (generation == _serialService.ReceiveSessionGeneration) return new(connected, generation, errors);
            }
        }
    }

    private void OnCaptureConnectionChanged(object? sender, EventArgs args) =>
        _captures?.RefreshConnection(() => CaptureConnectionSnapshot);

    private AutomationDeviceState AutomationState()
    {
        var settings = _serialService.IsConnected
            ? _lastSuccessfulSerialSettings ?? _currentSerialSettings
            : _currentSerialSettings;
        var effectiveBaudRate = _serialService.IsConnected && _serialService.AppliedBaudRate > 0
            ? _serialService.AppliedBaudRate : settings.BaudRate;
        return new(_serialService.IsConnected, settings.PortName, effectiveBaudRate,
            _serialService.ReceiveSessionGeneration, _currentSerialSettings.Encoding.ToString(),
            CaptureConnectionSnapshot.SerialErrorCount, IsBusy || IsSequenceRunning || IsBridgeActive || IsBridgeStarting ||
            IsAutoReconnectRunning || IsAutomationBusy || _activeGuiSends > 0 || _isProfileSaveActive, _serialService.LastError,
            settings.DataBits, settings.Parity.ToString(), settings.StopBits.ToString(), settings.Handshake.ToString(),
            SelectedRxDisplayMode == RxDisplayMode.Hex ? "hex" : "terminal", HexGroupTimeoutMs);
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
        OnPropertyChanged(nameof(CanChangeTerminalMode));
        OnPropertyChanged(nameof(CanEditCommandSequences));
        OnPropertyChanged(nameof(CanEditSelectedCommandSequence));
        OnPropertyChanged(nameof(CanEditSelectedCommandSequenceStep));
        ConnectCommand.NotifyCanExecuteChanged();
        ToggleConnectionCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        SendSavedCommandCommand.NotifyCanExecuteChanged();
        RunCommandSequenceCommand.NotifyCanExecuteChanged();
        StartBridgeCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
        LoadProfileCommand.NotifyCanExecuteChanged();
        ResetProfileCommand.NotifyCanExecuteChanged();
        CancelAutomationCommand.NotifyCanExecuteChanged();
    }

    private async Task<object> InvokeAutomationOnUiAsync(string operation, JsonElement args, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cleanup = operation is "release" or "capture_result" or "sequence_progress";
        if (Volatile.Read(ref _shutdownStarted) != 0 && !cleanup)
            throw new AutomationException("shutting_down", "The GUI is shutting down.");
        if (!cleanup) await InitializeAsync().WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _shutdownStarted) != 0 && !cleanup)
            throw new AutomationException("shutting_down", "The GUI is shutting down.");
        switch (operation)
        {
            case "status": return AutomationState();
            case "sequence_list":
            case "sequence_get":
            case "sequence_save":
            case "sequence_delete":
            case "sequence_progress":
                return await InvokeSequenceAutomationOnUiAsync(operation, args, cancellationToken);
            case "ports":
                var ports = await _serialService.GetAvailablePortsAsync(cancellationToken);
                return new { ports };
            case "list_commands":
                return AutomationCommandPaging.Create(Commands.SavedCommands.Count, index =>
                {
                    var command = Commands.SavedCommands[index];
                    var hex = SelectedTxSendMode == TxSendMode.Hex;
                    return new(command.Name, command.CommandText, hex ? "hex" : "text",
                        hex ? "none" : (command.LineEndingMode ?? SelectedTxLineEnding).ToString().ToLowerInvariant());
                }, args);
            case "reserve":
                if (AutomationState().Busy || !_automationControl.TryReserveAutomation())
                    throw new AutomationException("busy", "GUI TX, sequence, bridge, reconnect, connection change or another AI/CLI operation is active.");
                _automationLastResult = null;
                SetAutomationStatus(UiText.Get("AutomationActive", "AI/CLI active — RX capture / control"));
                return AutomationState();
            case "release":
                try
                {
                    if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("sequenceProgress", out var finalProgress))
                        ApplyAutomationSequenceProgress(finalProgress);
                }
                finally
                {
                    _automationControl.ReleaseAutomation();
                    SetAutomationStatus(_automationLastResult ?? UiText.Get("AutomationReady", "AI/CLI ready"));
                }
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
                if (baud is < AutomationSerialConfiguration.MinBaudRate or > AutomationSerialConfiguration.MaxBaudRate)
                    throw new AutomationException("invalid_argument", "baudRate must be an integer between 1 and 10,000,000.");
                return await AutomationConnectionOperation.ConnectAsync(port, baud, AutomationState, RefreshPortsAsync,
                    () =>
                    {
                        var displayPort = PortNames.FirstOrDefault(p => string.Equals(GetActualPortName(p), port, StringComparison.OrdinalIgnoreCase));
                        if (displayPort is null) throw new AutomationException("port_not_found", "Port is not available in this GUI instance.");
                        if (!BaudRates.Contains(baud)) BaudRates.Add(baud);
                        SelectedPort = displayPort;
                        SelectedBaudRate = baud;
                    }, token => ConnectAsync(token, isAutomation: true), cancellationToken);
            }
            case "disconnect":
                await DisconnectAsync(cancellationToken);
                return AutomationState();
            case "configure":
                return await ConfigureAutomationSerialAsync(args, cancellationToken);
            case "set_mode":
            {
                if (!IsAutomationBusy) throw new AutomationException("busy", "No control reservation.");
                var requestedMode = args.String("mode");
                if (requestedMode is not ("terminal" or "hex"))
                    throw new AutomationException("invalid_argument", "mode must be terminal or hex. Changing mode clears visible logs and changes GUI manual TX interpretation.");
                cancellationToken.ThrowIfCancellationRequested();
                SelectedRxDisplayMode = requestedMode == "hex" ? RxDisplayMode.Hex : RxDisplayMode.Terminal;
                return AutomationState();
            }
            case "validate_send":
                var requestedText = args.String("command") ?? throw new AutomationException("invalid_argument", "command is required.");
                if (args.String("txMode", "text") == "hex" && !HexPayloadParser.TryParse(requestedText, out _, out var hexError))
                    throw new AutomationException("invalid_argument", hexError);
                return new { valid = true };
            case "send":
            {
                if (!IsAutomationBusy) throw new AutomationException("busy", "No control reservation.");
                AutomationSendContext.Verify(args, CaptureConnectionSnapshot);
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
            => AutomationDispatch.InvokeAsync(owner._dispatcherQueue.HasThreadAccess,
                callback => owner._dispatcherQueue.TryEnqueue(() => callback()),
                () => owner.InvokeAutomationOnUiAsync(operation, args, cancellationToken), cancellationToken);
    }
}
