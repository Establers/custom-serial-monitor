using System.Diagnostics;
using System.Threading.Channels;

namespace SerialMonitor.Core.Protocols;

// Independent, bounded observer of existing immutable RX/TX byte arrays. No
// serial reads, file I/O, UI dispatches or parser work occur on the producer.
public sealed class BacnetMstpAnalyzer : IProtocolAnalyzer
{
    public const int DefaultHistoryCapacity = 10_000;
    public const long DefaultHistoryByteLimit = 16 * 1024 * 1024;
    public const long DefaultQueueByteLimit = 4 * 1024 * 1024;
    public const int QueueCapacity = 512;
    private static readonly TimeSpan AssemblyIdle = TimeSpan.FromMilliseconds(50);
    private readonly object _gate = new();
    private readonly Channel<Input> _input;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly int _historyCapacity;
    private readonly long _historyByteLimit;
    private readonly long _queueByteLimit;
    private readonly Queue<BacnetMstpFrame> _history = new();
    private bool _enabled;
    private bool _disposed;
    private long _epoch, _sequence, _pendingBytes, _retainedBytes, _totalFrames, _evictedFrames;
    private long _droppedChunks, _droppedBytes, _version, _nextFrameId;
    private string? _lastError;
    private int _stopDisposed;
    private long _rx, _tx, _crcValid, _crcErrors, _incomplete, _undecoded, _exceptions, _headerCrcErrors, _dataCrcErrors;
    private DateTimeOffset _statisticsStartedAt = DateTimeOffset.Now;
    private DateTimeOffset? _lastCrcValidAt;
    private readonly record struct Input(ReadOnlyMemory<byte> Bytes, ProtocolDirection Direction,
        long Session, DateTimeOffset Timestamp, long Epoch, long Sequence, long OfferedAt, ProtocolInputTiming Timing);

    public BacnetMstpAnalyzer(int historyCapacity = DefaultHistoryCapacity,
        long historyByteLimit = DefaultHistoryByteLimit, long queueByteLimit = DefaultQueueByteLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyByteLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueByteLimit);
        _historyCapacity = historyCapacity;
        _historyByteLimit = historyByteLimit;
        _queueByteLimit = queueByteLimit;
        _input = Channel.CreateBounded<Input>(new BoundedChannelOptions(QueueCapacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
        _worker = Task.Run(ProcessAsync);
    }

    public event EventHandler<string>? Error;
    public bool Enabled => Volatile.Read(ref _enabled);

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (enabled && _lastError is not null) throw new InvalidOperationException(_lastError);
            if (_enabled == enabled) return;
            _enabled = enabled;
            _epoch++;
            _version++;
        }
    }

    public bool TryEnqueue(ReadOnlyMemory<byte> bytes, ProtocolDirection direction, long session, DateTimeOffset timestamp,
        ProtocolInputTiming timing = default)
    {
        if (!Enabled || bytes.IsEmpty) return false;
        lock (_gate)
        {
            if (_disposed || !_enabled) return false;
            var sequence = ++_sequence;
            if (bytes.Length <= _queueByteLimit - _pendingBytes && _input.Writer.TryWrite(
                new Input(bytes, direction, session, timestamp, _epoch, sequence,
                    timing.ReceivedTimestamp > 0 ? timing.ReceivedTimestamp : Stopwatch.GetTimestamp(), timing)))
            {
                _pendingBytes += bytes.Length;
                return true;
            }
            _droppedChunks++;
            _droppedBytes += bytes.Length;
            _version++;
            return false;
        }
    }

    public ProtocolAnalysisSnapshot Snapshot(long knownVersion = -1, bool includeFrames = true)
    {
        lock (_gate) return new(_version, _enabled, _history.Count, _historyCapacity, _retainedBytes, _totalFrames,
            _evictedFrames, _droppedChunks, _droppedBytes, _pendingBytes, _lastError,
            null,
            new(_rx, _tx, _crcValid, _crcErrors, _incomplete, _undecoded, _exceptions, _statisticsStartedAt, _lastCrcValidAt, _headerCrcErrors, _dataCrcErrors), _nextFrameId)
            { Packets = !includeFrames || knownVersion == _version ? null : _history.ToArray() };
    }

    public void Clear()
    {
        lock (_gate)
        {
            _history.Clear();
            _retainedBytes = 0;
            _epoch++; // Queued pre-clear data and partial frames cannot reappear.
            _version++;
        }
    }

    public void ResetStatistics()
    {
        lock (_gate)
        {
            _totalFrames = _evictedFrames = _droppedChunks = _droppedBytes = 0;
            _rx = _tx = _crcValid = _crcErrors = _incomplete = _undecoded = _exceptions = _headerCrcErrors = _dataCrcErrors = 0;
            _statisticsStartedAt = DateTimeOffset.Now;
            _lastCrcValidAt = null;
            _version++;
        }
    }

    private async Task ProcessAsync()
    {
        var rx = new BacnetMstpFramer(ProtocolDirection.Rx);
        var tx = new BacnetMstpFramer(ProtocolDirection.Tx);
        long epoch = -1, session = -1, previousSequence = -1, rxAt = 0, txAt = 0;
        void Reset() { rx.Reset(); tx.Reset(); rxAt = txAt = 0; }
        void Store(BacnetMstpFrame frame)
        {
            lock (_gate)
            {
                if (!_enabled || epoch != _epoch || _disposed) return;
                var item = frame with { Id = ++_nextFrameId, Session = session };
                _totalFrames++;
                if (item.Direction == ProtocolDirection.Rx) _rx++; else _tx++;
                if (item.CrcStatus == ProtocolCrcStatus.Valid)
                {
                    _crcValid++;
                    if (_lastCrcValidAt is null || item.Timestamp > _lastCrcValidAt) _lastCrcValidAt = item.Timestamp;
                }
                else if (item.CrcStatus == ProtocolCrcStatus.Invalid) _crcErrors++;
                if (item.Status == ProtocolFrameStatus.Incomplete) _incomplete++;
                if (item.Status == ProtocolFrameStatus.Undecoded) _undecoded++;
                if (item.HeaderCrc == ProtocolCrcStatus.Invalid) _headerCrcErrors++;
                if (item.DataCrc == ProtocolCrcStatus.Invalid) _dataCrcErrors++;
                while (_history.Count > 0 && (_history.Count >= _historyCapacity || _retainedBytes + item.RetainedBytes > _historyByteLimit))
                { _retainedBytes -= _history.Dequeue().RetainedBytes; _evictedFrames++; }
                if (item.RetainedBytes <= _historyByteLimit)
                { _history.Enqueue(item); _retainedBytes += item.RetainedBytes; }
                else _evictedFrames++;
                _version++;
            }
        }
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                if (rx.PendingBytes > 0 || tx.PendingBytes > 0) idle.CancelAfter(AssemblyIdle);
                try
                {
                    if (!await _input.Reader.WaitToReadAsync(idle.Token).ConfigureAwait(false)) break;
                }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                {
                    rx.Flush(Store); tx.Flush(Store);
                    continue;
                }
                while (_input.Reader.TryRead(out var input))
                {
                    try
                    {
                        long currentEpoch;
                        lock (_gate) currentEpoch = _epoch;
                        if (input.Epoch != currentEpoch) { Reset(); continue; }
                        if (epoch != input.Epoch || session != input.Session || previousSequence >= 0 && input.Sequence != previousSequence + 1)
                            Reset();
                        epoch = input.Epoch; session = input.Session; previousSequence = input.Sequence;
                        if (rx.PendingBytes > 0 && rxAt > 0 && Stopwatch.GetElapsedTime(rxAt, input.OfferedAt) >= AssemblyIdle) rx.Flush(Store);
                        if (tx.PendingBytes > 0 && txAt > 0 && Stopwatch.GetElapsedTime(txAt, input.OfferedAt) >= AssemblyIdle) tx.Flush(Store);
                        if (input.Direction == ProtocolDirection.Rx)
                        {
                            if (rxAt > 0 && Stopwatch.GetElapsedTime(rxAt, input.OfferedAt) >= AssemblyIdle) rx.Flush(Store);
                            rx.Feed(input.Bytes.Span, input.Timestamp, Store, _stop.Token, input.Timing.HasQualifiedBoundary); rxAt = input.OfferedAt;
                        }
                        else
                        {
                            if (txAt > 0 && Stopwatch.GetElapsedTime(txAt, input.OfferedAt) >= AssemblyIdle) tx.Flush(Store);
                            tx.Feed(input.Bytes.Span, input.Timestamp, Store, _stop.Token); txAt = input.OfferedAt;
                        }
                        _stop.Token.ThrowIfCancellationRequested();
                    }
                    finally { lock (_gate) _pendingBytes -= input.Bytes.Length; }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            var error = $"BACnet MS/TP analysis failed: {ex.Message}";
            lock (_gate) { _lastError = error; _enabled = false; _epoch++; _version++; }
            try { Error?.Invoke(this, error); } catch { /* Fault reporting cannot restart the worker. */ }
        }
        finally
        {
            _input.Writer.TryComplete();
            while (_input.Reader.TryRead(out var abandoned)) lock (_gate) _pendingBytes -= abandoned.Bytes.Length;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_disposed) { _disposed = true; _enabled = false; _epoch++; _input.Writer.TryComplete(); _stop.Cancel(); }
        }
        await _worker.ConfigureAwait(false);
        if (Interlocked.Exchange(ref _stopDisposed, 1) == 0) _stop.Dispose();
    }
}
