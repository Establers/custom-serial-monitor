using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using System.Diagnostics;
using System.ComponentModel;
using SerialMonitor.Core.Protocols.VirtualModbus;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    public ProtocolViewModel Protocol { get; } = new(new ModbusRtuAnalyzer());
    private VirtualModbusDeviceRuntime? _virtualModbus;
    private bool IsVirtualModbusActive => _virtualModbus?.IsActive == true;

    private void InitializeProtocolAnalysis()
    {
        var store = new VirtualModbusDeviceStore();
        _virtualModbus = new VirtualModbusDeviceRuntime(store, SendVirtualModbusResponseAsync);
        _virtualModbus.Error += OnBackgroundError;
        _virtualModbus.StateChanged += OnVirtualModbusRuntimeChanged;
        Protocol.VirtualDevice = new VirtualModbusDeviceViewModel(store, _virtualModbus, StartVirtualModbus);
        Protocol.VirtualDevice.PropertyChanged += OnVirtualModbusStateChanged;
        PropertyChanged += OnVirtualModbusHostStateChanged;
        _serialService.StatusChanged += OnVirtualModbusConnectionChanged;
        Protocol.Analyzer.Error += OnBackgroundError;
        _serialService.CaptureBytesReceived += OnProtocolBytesReceived;
    }

    private void OnProtocolBytesReceived(long session, ReceivedByteChunk chunk)
    {
        var settings = _lastSuccessfulSerialSettings ?? _currentSerialSettings;
        var stopBits = settings.StopBits == SerialStopBitsMode.Two ? 2d : settings.StopBits == SerialStopBitsMode.OnePointFive ? 1.5 : 1;
        var bits = 1 + settings.DataBits + (settings.Parity == SerialParityMode.None ? 0 : 1) + stopBits;
        var timing = new ProtocolInputTiming(chunk.ReceivedTimestamp, chunk.EndsAtNativeIdleBoundary,
            _serialService.AppliedReceiveIdleTimeoutMs,
            ProtocolInputTiming.FrameGapMilliseconds(_serialService.AppliedBaudRate, bits),
            IsVirtualModbusActive ? ProtocolFrameRole.Request : ProtocolFrameRole.Automatic);
        _virtualModbus?.TryEnqueue(chunk.Bytes, session, chunk.ReceivedTimestamp, timing.HasQualifiedBoundary);
        if (Protocol?.Analyzer is { Enabled: true } analyzer)
        {
            var receivedAt = DateTimeOffset.Now;
            if (chunk.ReceivedTimestamp > 0)
                receivedAt -= Stopwatch.GetElapsedTime(chunk.ReceivedTimestamp);
            analyzer.TryEnqueue(chunk.Bytes, ProtocolDirection.Rx, session, receivedAt,
                timing);
        }
    }

    private void OfferProtocolTransmit(LogLine line, long? session = null, ProtocolFrameRole role = ProtocolFrameRole.Automatic)
    {
        if (line.Direction == LogDirection.Tx && line.RawBytes is { Length: > 0 } bytes && Protocol?.Analyzer is { Enabled: true } analyzer)
            analyzer.TryEnqueue(bytes, ProtocolDirection.Tx, session ?? _serialService.ReceiveSessionGeneration, line.Timestamp,
                new ProtocolInputTiming(Role: role));
    }

    private void StartVirtualModbus()
    {
        if (!_serialService.IsConnected || IsBusy || IsShuttingDown || IsAutomationBusy || IsSequenceRunning ||
            IsBridgeActive || IsBridgeStarting || IsTerminalDirectInputEnabled || _activeGuiSends != 0)
            throw new InvalidOperationException("Connect the product and stop sequences, automation, bridge and direct input before starting Virtual Modbus Device.");
        if (IsMockPortName((_lastSuccessfulSerialSettings ?? _currentSerialSettings).PortName))
            throw new InvalidOperationException("Virtual Modbus Device needs a real or virtual COM connection; the MOCK text-echo port is not supported.");
        var settings = _lastSuccessfulSerialSettings ?? _currentSerialSettings;
        var stop = settings.StopBits == SerialStopBitsMode.Two ? 2d : settings.StopBits == SerialStopBitsMode.OnePointFive ? 1.5 : 1;
        var bits = 1 + settings.DataBits + (settings.Parity == SerialParityMode.None ? 0 : 1) + stop;
        _virtualModbus!.Start(_serialService.ReceiveSessionGeneration,
            ProtocolInputTiming.FrameGapMilliseconds(_serialService.AppliedBaudRate, bits), Protocol.VirtualDevice!.SuppressLocalEcho);
        Protocol.SelectedProtocolIndex = 1;
        NotifyVirtualModbusAdmission();
    }

    private async Task SendVirtualModbusResponseAsync(byte[] bytes, long generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Reuse the existing session-fenced byte writer, which validates the
        // connection again after acquiring the serial write gate.
        await _serialService.SendTerminalInputAsync(bytes, generation, token).ConfigureAwait(false);
        FanOutLogLine(LogLine.Tx(FormatBytesAsHex(bytes), bytes, contentMode: LogRuleMatchMode.Hex),
            fileEligible: true, detectEvent: false, protocolSession: generation, protocolRole: ProtocolFrameRole.Response);
    }

    private void OnVirtualModbusConnectionChanged(object? sender, EventArgs args)
    {
        if (_virtualModbus?.Snapshot() is { Running: true } state &&
            (!_serialService.IsConnected || _serialService.ReceiveSessionGeneration != state.Session))
            _virtualModbus.RequestStop();
    }

    private void OnVirtualModbusRuntimeChanged(object? sender, EventArgs args) => RunOnUiThread(() =>
    {
        Protocol.VirtualDevice?.Refresh();
        NotifyVirtualModbusAdmission();
    });

    private void OnVirtualModbusHostStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(IsBusy) or nameof(IsShuttingDown) or nameof(IsSequenceRunning) or
            nameof(IsAutomationBusy) or nameof(IsBridgeActive) or nameof(IsBridgeStarting) or nameof(IsTerminalDirectInputEnabled))
            if (IsBusy || IsShuttingDown || IsSequenceRunning || IsAutomationBusy || IsBridgeActive || IsBridgeStarting || IsTerminalDirectInputEnabled)
                _virtualModbus?.RequestStop();
    }

    private void OnVirtualModbusStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(VirtualModbusDeviceViewModel.IsRunning) or nameof(VirtualModbusDeviceViewModel.IsStopping))
            NotifyVirtualModbusAdmission();
    }

    private void NotifyVirtualModbusAdmission()
    {
        OnPropertyChanged(nameof(CanSendManualTx)); OnPropertyChanged(nameof(CanStartBridge));
        StartBridgeCommand.NotifyCanExecuteChanged();
        NotifyCommandStates();
    }

    private async Task DisposeProtocolAnalysisAsync()
    {
        PropertyChanged -= OnVirtualModbusHostStateChanged;
        _serialService.StatusChanged -= OnVirtualModbusConnectionChanged;
        if (Protocol.VirtualDevice is { } device)
        {
            device.CancelPendingOperations();
            device.PropertyChanged -= OnVirtualModbusStateChanged;
        }
        if (_virtualModbus is { } runtime)
        {
            runtime.Error -= OnBackgroundError;
            runtime.StateChanged -= OnVirtualModbusRuntimeChanged;
            await runtime.DisposeAsync();
        }
        if (Protocol?.Analyzer is not { } analyzer) return;
        _serialService.CaptureBytesReceived -= OnProtocolBytesReceived;
        analyzer.Error -= OnBackgroundError;
        await analyzer.DisposeAsync();
    }
}
