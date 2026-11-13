using System.Diagnostics;
using System.Threading.Channels;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

internal sealed class ModbusReceiveInbox : IDisposable
{
    private const int MaximumQueuedBytes = 64 * 1024;
    private readonly ISerialService _serial;
    private readonly long _generation;
    private readonly Channel<ReceivedByteChunk> _chunks = Channel.CreateBounded<ReceivedByteChunk>(
        new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private int _queuedBytes;
    private int _overflow;

    public ModbusReceiveInbox(ISerialService serial, long generation)
    {
        _serial = serial;
        _generation = generation;
        serial.CaptureBytesReceived += Offer;
        serial.StatusChanged += ConnectionChanged;
    }

    private void Offer(long generation, ReceivedByteChunk chunk)
    {
        if (generation != _generation || chunk.Bytes.Length == 0) return;
        if (Interlocked.Add(ref _queuedBytes, chunk.Bytes.Length) > MaximumQueuedBytes)
        {
            Interlocked.Add(ref _queuedBytes, -chunk.Bytes.Length);
            Interlocked.Exchange(ref _overflow, 1);
            _chunks.Writer.TryComplete(new IOException("Modbus receive queue overflow."));
            return;
        }
        if (!_chunks.Writer.TryWrite(chunk))
        {
            Interlocked.Add(ref _queuedBytes, -chunk.Bytes.Length);
            Interlocked.Exchange(ref _overflow, 1);
            _chunks.Writer.TryComplete(new IOException("Modbus receive queue overflow."));
        }
    }

    private void ConnectionChanged(object? sender, EventArgs args)
    {
        if (!_serial.IsConnected || _serial.ReceiveSessionGeneration != _generation)
            _chunks.Writer.TryComplete(new IOException("The Modbus serial connection changed."));
    }

    public void EnsureConnection()
    {
        if (Volatile.Read(ref _overflow) != 0) throw new ModbusExchangeException("Modbus receive queue overflow; values are inconclusive.", true);
        if (!_serial.IsConnected || _serial.ReceiveSessionGeneration != _generation)
            throw new IOException("The Modbus serial connection changed.");
    }

    public async Task WaitForQuietAsync(int gapMs, int maximumMs, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var quietSince = started;
        while (true)
        {
            EnsureConnection();
            while (_chunks.Reader.TryRead(out var chunk))
            {
                Interlocked.Add(ref _queuedBytes, -chunk.Bytes.Length);
                quietSince = Stopwatch.GetTimestamp();
                cancellationToken.ThrowIfCancellationRequested();
                EnsureConnection();
                if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= maximumMs)
                    throw new ModbusExchangeException("The Modbus bus did not become idle.", true);
            }
            if (Stopwatch.GetElapsedTime(quietSince).TotalMilliseconds >= gapMs) return;
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= maximumMs)
                throw new ModbusExchangeException("The Modbus bus did not become idle.", true);
            await Task.Delay(Math.Min(5, gapMs), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<byte[]> ReadFrameAsync(int normalLength, long requestStarted, CancellationToken cancellationToken)
    {
        var frame = new byte[256];
        var count = 0;
        var expected = normalLength;
        while (true)
        {
            EnsureConnection();
            var chunk = await _chunks.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _queuedBytes, -chunk.Bytes.Length);
            // A delayed observer must not attach an earlier read to this request.
            if (chunk.ReceivedTimestamp > 0 && chunk.ReceivedTimestamp < requestStarted) continue;
            foreach (var value in chunk.Bytes)
            {
                if (count >= frame.Length) throw new ModbusExchangeException("Modbus response is too long.", true);
                frame[count++] = value;
                if (count == 2 && (value & 0x80) != 0) expected = 5;
                if (count > expected) throw new ModbusExchangeException("Unexpected trailing Modbus data.", true);
            }
            if (count < expected && HasQualifiedNativeBoundary(chunk))
                throw new ModbusExchangeException("An incomplete Modbus response ended at a confirmed RTU idle boundary.", true);
            if (count == expected)
            {
                EnsureConnection();
                if (_chunks.Reader.TryPeek(out _)) throw new ModbusExchangeException("Unexpected additional Modbus data.", true);
                return frame.AsSpan(0, count).ToArray();
            }
        }
    }

    private bool HasQualifiedNativeBoundary(ReceivedByteChunk chunk)
    {
        if (!chunk.EndsAtNativeIdleBoundary) return false;
        var configuration = _serial.AppliedConfiguration;
        var stopBits = configuration?.StopBits switch
        {
            SerialStopBitsMode.Two => 2d,
            SerialStopBitsMode.OnePointFive => 1.5d,
            _ => 1d
        };
        var bits = configuration is null ? 12d
            : 1 + configuration.DataBits + (configuration.Parity == SerialParityMode.None ? 0 : 1) + stopBits;
        return new ProtocolInputTiming(chunk.ReceivedTimestamp, true, _serial.AppliedReceiveIdleTimeoutMs,
            ProtocolInputTiming.FrameGapMilliseconds(_serial.AppliedBaudRate, bits)).HasQualifiedBoundary;
    }

    public void Dispose()
    {
        _serial.CaptureBytesReceived -= Offer;
        _serial.StatusChanged -= ConnectionChanged;
        _chunks.Writer.TryComplete();
    }
}
