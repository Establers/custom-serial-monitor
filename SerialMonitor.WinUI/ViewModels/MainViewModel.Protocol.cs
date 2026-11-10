using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Infrastructure;
using System.Diagnostics;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    public ProtocolViewModel Protocol { get; } = new(new ProtocolAnalyzer());

    public bool TryPrepareModbusPacket(ReadOnlyMemory<byte> packet)
    {
        // Preparing a request uses the existing HEX input and never sends bytes.
        // Respect the same mode lock as the Terminal/HEX selector.
        if (!CanChangeTerminalMode)
        {
            SetStatus(UiText.Get("ModbusBuilderBusy", "TX is busy. Copy the packet or try again after the current operation finishes."));
            return false;
        }
        if (packet.Length is < 4 or > ModbusRtuFrame.MaximumBytes || ModbusRtuCrc.Compute(packet.Span) != 0)
        {
            SetStatus(UiText.Get("ModbusBuilderInvalidPacket", "The Modbus RTU packet has an invalid length or CRC."));
            return false;
        }
        SelectedTxSendMode = TxSendMode.Hex;
        Commands.CurrentCommandText = FormatBytesAsHex(packet.ToArray());
        SetStatus(UiText.Get("ModbusBuilderPrepared", "Modbus RTU packet loaded into HEX TX. Press Send when ready."));
        return true;
    }

    private void InitializeProtocolAnalysis()
    {
        Protocol.Analyzer.Error += OnBackgroundError;
        _serialService.CaptureBytesReceived += OnProtocolBytesReceived;
    }

    private void OnProtocolBytesReceived(long session, ReceivedByteChunk chunk)
    {
        if (Protocol?.Analyzer is { Enabled: true } analyzer)
        {
            var settings = _lastSuccessfulSerialSettings ?? _currentSerialSettings;
            var stopBits = settings.StopBits == SerialStopBitsMode.Two ? 2d : settings.StopBits == SerialStopBitsMode.OnePointFive ? 1.5 : 1;
            var bits = 1 + settings.DataBits + (settings.Parity == SerialParityMode.None ? 0 : 1) + stopBits;
            var receivedAt = DateTimeOffset.Now;
            if (chunk.ReceivedTimestamp > 0)
                receivedAt -= Stopwatch.GetElapsedTime(chunk.ReceivedTimestamp);
            analyzer.TryEnqueue(chunk.Bytes, ProtocolDirection.Rx, session, receivedAt,
                new ProtocolInputTiming(chunk.ReceivedTimestamp, chunk.EndsAtNativeIdleBoundary,
                    _serialService.AppliedReceiveIdleTimeoutMs,
                    ProtocolInputTiming.FrameGapMilliseconds(_serialService.AppliedBaudRate, bits)));
        }
    }

    private void OfferProtocolTransmit(LogLine line, long? session = null)
    {
        if (line.Direction == LogDirection.Tx && line.RawBytes is { Length: > 0 } bytes && Protocol?.Analyzer is { Enabled: true } analyzer)
            analyzer.TryEnqueue(bytes, ProtocolDirection.Tx, session ?? _serialService.ReceiveSessionGeneration, line.Timestamp);
    }

    private async Task DisposeProtocolAnalysisAsync()
    {
        if (Protocol?.Analyzer is not { } analyzer) return;
        _serialService.CaptureBytesReceived -= OnProtocolBytesReceived;
        analyzer.Error -= OnBackgroundError;
        await analyzer.DisposeAsync();
    }
}
