using SerialMonitor.Core;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

internal enum BridgeGroupFlushReason
{
    IdleTimeout,
    NativeIdleBoundary,
    MaximumLatency,
    MaximumSize,
    ConfigurationChanged
}

internal readonly record struct BridgeGroupWait(
    TimeSpan Delay,
    BridgeGroupFlushReason TimeoutReason);

internal sealed class BridgeDeviceChunkGrouper
{
    internal const int MaxGroupedBytes = 1024 * 1024;
    internal static readonly TimeSpan MaxGroupLatency = TimeSpan.FromMilliseconds(100);

    private readonly List<byte[]> _chunks = new();
    private int _byteCount;
    private long? _firstReceivedTimestamp;
    private long? _lastReceivedTimestamp;
    private int _groupTimeoutMs;
    private long _sourceSerialSessionGeneration;
    private bool _endsAtNativeIdleBoundary;

    public bool HasData => _byteCount > 0;

    public void Append(BridgeRxChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var flushReason = GetFlushReasonBeforeAppend(chunk);
        if (flushReason.HasValue)
        {
            throw new InvalidOperationException(
                $"Bridge RX chunk cannot be appended before a {flushReason.Value} flush.");
        }

        if (!HasData)
        {
            if (chunk.DeviceToVirtualGroupTimeoutMs <= 0)
            {
                throw new InvalidOperationException("Bridge HEX grouping requires a positive idle timeout.");
            }

            _groupTimeoutMs = chunk.DeviceToVirtualGroupTimeoutMs;
            _sourceSerialSessionGeneration = chunk.SourceSerialSessionGeneration;
            _firstReceivedTimestamp = chunk.ReceivedTimestamp;
        }

        if (chunk.Bytes.Length > 0)
        {
            _chunks.Add(chunk.Bytes);
            _byteCount += chunk.Bytes.Length;
        }

        _lastReceivedTimestamp = _lastReceivedTimestamp.HasValue
            ? Math.Max(_lastReceivedTimestamp.Value, chunk.ReceivedTimestamp)
            : chunk.ReceivedTimestamp;
        _endsAtNativeIdleBoundary = chunk.EndsAtNativeIdleBoundary;
    }

    public BridgeGroupFlushReason? GetFlushReasonBeforeAppend(BridgeRxChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (!HasData)
        {
            return null;
        }

        if (chunk.DeviceToVirtualGroupTimeoutMs != _groupTimeoutMs)
        {
            return BridgeGroupFlushReason.ConfigurationChanged;
        }

        if (chunk.SourceSerialSessionGeneration != _sourceSerialSessionGeneration)
        {
            return BridgeGroupFlushReason.ConfigurationChanged;
        }

        if (_endsAtNativeIdleBoundary)
        {
            return BridgeGroupFlushReason.NativeIdleBoundary;
        }

        if (chunk.Bytes.Length > MaxGroupedBytes - _byteCount)
        {
            return BridgeGroupFlushReason.MaximumSize;
        }

        var idleObservation = IdleGapBoundaryDetector.Observe(
            _lastReceivedTimestamp,
            chunk.ReceivedTimestamp,
            TimeSpan.FromMilliseconds(_groupTimeoutMs));
        if (idleObservation.StartsNewGroup)
        {
            return BridgeGroupFlushReason.IdleTimeout;
        }

        var latencyObservation = IdleGapBoundaryDetector.Observe(
            _firstReceivedTimestamp,
            chunk.ReceivedTimestamp,
            MaxGroupLatency);
        return latencyObservation.StartsNewGroup
            ? BridgeGroupFlushReason.MaximumLatency
            : null;
    }

    public BridgeGroupFlushReason? GetImmediateFlushReason(long nowTimestamp)
    {
        if (!HasData)
        {
            return null;
        }

        if (_endsAtNativeIdleBoundary)
        {
            return BridgeGroupFlushReason.NativeIdleBoundary;
        }

        if (_byteCount >= MaxGroupedBytes)
        {
            return BridgeGroupFlushReason.MaximumSize;
        }

        var wait = GetNextWait(nowTimestamp);
        return wait.Delay <= TimeSpan.Zero
            ? wait.TimeoutReason
            : null;
    }

    public BridgeGroupWait GetNextWait(long nowTimestamp)
    {
        if (!HasData)
        {
            throw new InvalidOperationException("Cannot wait for an empty bridge HEX group.");
        }

        var idleDelay = IdleGapBoundaryDetector.GetRemainingDelay(
            _lastReceivedTimestamp,
            nowTimestamp,
            TimeSpan.FromMilliseconds(_groupTimeoutMs));
        var latencyDelay = IdleGapBoundaryDetector.GetRemainingDelay(
            _firstReceivedTimestamp,
            nowTimestamp,
            MaxGroupLatency);

        return idleDelay <= latencyDelay
            ? new BridgeGroupWait(idleDelay, BridgeGroupFlushReason.IdleTimeout)
            : new BridgeGroupWait(latencyDelay, BridgeGroupFlushReason.MaximumLatency);
    }

    public BridgeRxChunk BuildAndReset(BridgeGroupFlushReason flushReason)
    {
        if (!HasData || !_lastReceivedTimestamp.HasValue)
        {
            throw new InvalidOperationException("Cannot build an empty bridge HEX group.");
        }

        var bytes = new byte[_byteCount];
        var offset = 0;
        foreach (var chunk in _chunks)
        {
            chunk.CopyTo(bytes, offset);
            offset += chunk.Length;
        }

        var hasRealIdleBoundary = flushReason is
            BridgeGroupFlushReason.IdleTimeout or
            BridgeGroupFlushReason.NativeIdleBoundary;
        var hasNativeIdleBoundary = flushReason == BridgeGroupFlushReason.NativeIdleBoundary;
        var grouped = new BridgeRxChunk(
            bytes,
            _lastReceivedTimestamp.Value,
            EndsAtNativeIdleBoundary: hasNativeIdleBoundary,
            AppliedIdleTimeoutMs: hasNativeIdleBoundary ? _groupTimeoutMs : 0)
        {
            DeviceToVirtualGroupTimeoutMs = _groupTimeoutMs,
            ReplayIdleGapMs = hasRealIdleBoundary ? _groupTimeoutMs : 0,
            SourceSerialSessionGeneration = _sourceSerialSessionGeneration
        };

        _chunks.Clear();
        _byteCount = 0;
        _firstReceivedTimestamp = null;
        _lastReceivedTimestamp = null;
        _groupTimeoutMs = 0;
        _sourceSerialSessionGeneration = 0;
        _endsAtNativeIdleBoundary = false;
        return grouped;
    }
}
