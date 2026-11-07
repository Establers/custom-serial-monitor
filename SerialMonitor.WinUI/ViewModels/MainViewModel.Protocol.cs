using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using System.Diagnostics;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    public ProtocolViewModel Protocol { get; } = new(new ModbusRtuAnalyzer());

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
