using RJCP.IO.Ports;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

internal enum BridgeQueueEnqueueResult
{
    BridgeStopped,
    Enqueued,
    Overflow
}

public sealed class SerialBridgeService : ISerialBridgeService
{
    private const string DeviceToVirtualOverflowMessage =
        "Bridge stopped: virtual COM consumer too slow";

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _arbiterSignal = new(0, 1);
    private readonly object _stateGate = new();
    private readonly IBridgeClock _clock;
    private BoundedByteQueue<BridgeRxChunk> _deviceToVirtualQueue;
    private BoundedByteQueue<byte[]> _virtualToDeviceQueue;
    private CancellationTokenSource? _cancellation;
    private SerialPortStream? _virtualPort;
    private Task? _readerTask;
    private Task? _writerTask;
    private Task? _deviceWriterTask;
    private string _virtualPortName = string.Empty;
    private string? _lastError;
    private string? _lastFaultReason;
    private long _deviceToVirtualByteCount;
    private long _deviceToVirtualChunkCount;
    private long _virtualToDeviceByteCount;
    private long _virtualToDeviceChunkCount;
    private long _droppedDeviceToVirtualByteCount;
    private long _droppedDeviceToVirtualChunkCount;
    private long _droppedVirtualToDeviceByteCount;
    private long _droppedVirtualToDeviceChunkCount;
    private long _errorCount;
    private long _queueOverflowCount;
    private long _replayLateCount;
    private double _lastDeviceToVirtualDelayMs;
    private double _maxDeviceToVirtualDelayMs;
    private double _maxReplayLatenessMs;
    private long _lastBridgeActivityTimestamp;
    private DateTimeOffset? _lastBridgeActivityAt;
    private int _manualTxIdleGuardMs = BridgeSettings.DefaultManualTxIdleGuardMs;
    private bool _deviceToVirtualWriteActive;
    private bool _virtualToDeviceWriteActive;
    private ManualTxState _manualTxState;
    private ManualRequest? _pendingManual;
    private bool _isRunning;
    private bool _disposed;

    public SerialBridgeService()
        : this(new SystemBridgeClock())
    {
    }

    internal SerialBridgeService(IBridgeClock clock)
    {
        _clock = clock;
        _deviceToVirtualQueue = CreateDeviceQueue(
            BridgeSettings.DefaultMaxQueuedChunks,
            BridgeSettings.DefaultMaxQueuedBytes);
        _virtualToDeviceQueue = CreateByteQueue(
            BridgeSettings.DefaultMaxQueuedChunks,
            BridgeSettings.DefaultMaxQueuedBytes);
    }

    public event EventHandler<string>? Error;

    public event EventHandler? StatusChanged;

    public event EventHandler<ManualTxStateChangedEventArgs>? ManualTxStateChanged;

    public bool IsRunning { get { lock (_stateGate) return _isRunning; } }

    public string VirtualPortName { get { lock (_stateGate) return _virtualPortName; } }

    public string? LastError { get { lock (_stateGate) return _lastError; } }

    public string? LastFaultReason { get { lock (_stateGate) return _lastFaultReason; } }

    public DateTimeOffset? LastBridgeActivityAt { get { lock (_stateGate) return _lastBridgeActivityAt; } }

    public ManualTxState ManualTxState { get { lock (_stateGate) return _manualTxState; } }

    public long DeviceToVirtualByteCount => Interlocked.Read(ref _deviceToVirtualByteCount);
    public long DeviceToVirtualChunkCount => Interlocked.Read(ref _deviceToVirtualChunkCount);
    public long VirtualToDeviceByteCount => Interlocked.Read(ref _virtualToDeviceByteCount);
    public long VirtualToDeviceChunkCount => Interlocked.Read(ref _virtualToDeviceChunkCount);
    public long DroppedDeviceToVirtualByteCount => Interlocked.Read(ref _droppedDeviceToVirtualByteCount);
    public long DroppedDeviceToVirtualChunkCount => Interlocked.Read(ref _droppedDeviceToVirtualChunkCount);
    public long DroppedVirtualToDeviceByteCount => Interlocked.Read(ref _droppedVirtualToDeviceByteCount);
    public long DroppedVirtualToDeviceChunkCount => Interlocked.Read(ref _droppedVirtualToDeviceChunkCount);
    public long ErrorCount => Interlocked.Read(ref _errorCount);
    public long QueueOverflowCount => Interlocked.Read(ref _queueOverflowCount);
    public long ReplayLateCount => Interlocked.Read(ref _replayLateCount);

    public int PendingDeviceToVirtualChunkCount => _deviceToVirtualQueue.Count;
    public int PendingVirtualToDeviceChunkCount => _virtualToDeviceQueue.Count;
    public int PendingDeviceToVirtualByteCount => _deviceToVirtualQueue.ByteCount;
    public int PendingVirtualToDeviceByteCount => _virtualToDeviceQueue.ByteCount;
    public double OldestPendingChunkAgeMs => Math.Max(
        _deviceToVirtualQueue.OldestAgeMilliseconds,
        _virtualToDeviceQueue.OldestAgeMilliseconds);
    public double LastDeviceToVirtualDelayMs { get { lock (_stateGate) return _lastDeviceToVirtualDelayMs; } }
    public double MaxDeviceToVirtualDelayMs { get { lock (_stateGate) return _maxDeviceToVirtualDelayMs; } }
    public double MaxReplayLatenessMs { get { lock (_stateGate) return _maxReplayLatenessMs; } }

    public double ManualTxWaitMs
    {
        get
        {
            lock (_stateGate)
            {
                return _pendingManual is null
                    ? 0
                    : TicksToMilliseconds(Math.Max(0, _clock.GetTimestamp() - _pendingManual.QueuedTimestamp));
            }
        }
    }

    public double ManualTxIdleGuardRemainingMs
    {
        get
        {
            lock (_stateGate)
            {
                return GetIdleGuardRemainingMsLocked(_clock.GetTimestamp());
            }
        }
    }

    public async Task StartAsync(
        BridgeSettings settings,
        SerialSettings deviceSettings,
        Func<byte[], CancellationToken, Task> writeToDeviceAsync,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(deviceSettings);
        ArgumentNullException.ThrowIfNull(writeToDeviceAsync);

        var virtualPortName = settings.VirtualPortName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(virtualPortName))
        {
            throw new InvalidOperationException("Select a virtual COM port for the bridge.");
        }

        var maxChunks = Math.Clamp(settings.MaxQueuedChunks, 1, 65_536);
        var maxBytes = Math.Clamp(settings.MaxQueuedBytes, 64 * 1024, 256 * 1024 * 1024);
        var idleGuardMs = Math.Clamp(settings.ManualTxIdleGuardMs, 0, 10_000);

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCurrentAsync(CancellationToken.None);
            var virtualPort = CreateVirtualPort(virtualPortName, deviceSettings);
            try
            {
                await Task.Run(virtualPort.Open, cancellationToken);
            }
            catch
            {
                SafeCloseAndDispose(virtualPort);
                throw;
            }

            while (_arbiterSignal.Wait(0)) { }
            _deviceToVirtualQueue = CreateDeviceQueue(maxChunks, maxBytes);
            _virtualToDeviceQueue = CreateByteQueue(maxChunks, maxBytes);
            var bridgeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_stateGate)
            {
                _cancellation = bridgeCancellation;
                _virtualPort = virtualPort;
                _virtualPortName = virtualPortName;
                _lastError = null;
                _lastFaultReason = null;
                _manualTxIdleGuardMs = idleGuardMs;
                _manualTxState = ManualTxState.Idle;
                _pendingManual = null;
                _deviceToVirtualWriteActive = false;
                _virtualToDeviceWriteActive = false;
                _lastBridgeActivityTimestamp = _clock.GetTimestamp() - MillisecondsToTicks(idleGuardMs);
                _lastBridgeActivityAt = null;
                _isRunning = true;
            }

            _readerTask = Task.Run(() => RunVirtualReaderAsync(virtualPort, bridgeCancellation.Token));
            _writerTask = Task.Run(() => RunVirtualWriterAsync(virtualPort, bridgeCancellation.Token));
            _deviceWriterTask = Task.Run(() => RunDeviceSchedulerAsync(writeToDeviceAsync, bridgeCancellation.Token));
            RaiseStatusChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportError($"Bridge start failed for {virtualPortName}: {ex.Message}");
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCurrentAsync(cancellationToken);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public bool TryEnqueueDeviceChunk(BridgeRxChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Bytes.Length == 0)
        {
            return true;
        }

        lock (_stateGate)
        {
            if (!_isRunning)
            {
                return false;
            }

            if (_deviceToVirtualQueue.TryEnqueue(chunk, chunk.Bytes.Length))
            {
                MarkBridgeActivityLocked();
                SignalArbiter();
                return true;
            }
        }

        Interlocked.Increment(ref _droppedDeviceToVirtualChunkCount);
        Interlocked.Add(ref _droppedDeviceToVirtualByteCount, chunk.Bytes.Length);
        Interlocked.Increment(ref _queueOverflowCount);
        FaultBridge(DeviceToVirtualOverflowMessage);
        return false;
    }

    public async Task<ManualTransmitResult> QueueManualTransmitAsync(
        Func<CancellationToken, Task> transmitAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transmitAsync);
        ManualRequest request;
        ManualTxStateChangedEventArgs? stateChange;
        lock (_stateGate)
        {
            if (!_isRunning)
            {
                return ManualTransmitResult.BridgeNotRunning;
            }

            if (_manualTxState != ManualTxState.Idle || _pendingManual is not null)
            {
                return ManualTransmitResult.Busy;
            }

            request = new ManualRequest(transmitAsync, _clock.GetTimestamp());
            _pendingManual = request;
            stateChange = SetManualTxStateLocked(ManualTxState.WaitingForBridgeIdle);
        }

        RaiseManualTxStateChanged(stateChange);
        RaiseStatusChanged();
        SignalArbiter();
        using var registration = cancellationToken.Register(() => CancelWaitingManual(request));
        return await request.Completion.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync(CancellationToken.None);
        _lifecycleGate.Dispose();
        _arbiterSignal.Dispose();
    }

    private async Task RunVirtualReaderAsync(SerialPortStream virtualPort, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await virtualPort.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (bytesRead <= 0)
                {
                    continue;
                }

                var chunk = buffer.AsSpan(0, bytesRead).ToArray();
                BridgeQueueEnqueueResult enqueueResult;
                lock (_stateGate)
                {
                    enqueueResult = TryEnqueueVirtualToDevice(
                        _isRunning,
                        _virtualToDeviceQueue,
                        chunk);
                    if (enqueueResult == BridgeQueueEnqueueResult.Enqueued)
                    {
                        MarkBridgeActivityLocked();
                    }
                }

                if (enqueueResult == BridgeQueueEnqueueResult.BridgeStopped)
                {
                    return;
                }

                if (enqueueResult == BridgeQueueEnqueueResult.Overflow)
                {
                    Interlocked.Increment(ref _droppedVirtualToDeviceChunkCount);
                    Interlocked.Add(ref _droppedVirtualToDeviceByteCount, chunk.Length);
                    Interlocked.Increment(ref _queueOverflowCount);
                    FaultBridge("Bridge stopped: physical COM consumer too slow");
                    return;
                }

                SignalArbiter();
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            FaultBridge($"Bridge virtual-to-device failed: {ex.Message}");
        }
    }

    private async Task RunVirtualWriterAsync(SerialPortStream virtualPort, CancellationToken cancellationToken)
    {
        var replayer = new BridgeGapReplayer(_clock);
        try
        {
            while (await _deviceToVirtualQueue.WaitToReadAsync(cancellationToken))
            {
                BridgeRxChunk? chunk;
                lock (_stateGate)
                {
                    if (!_deviceToVirtualQueue.TryDequeue(out chunk) || chunk is null)
                    {
                        continue;
                    }

                    _deviceToVirtualWriteActive = true;
                }

                try
                {
                    var replay = await replayer.WaitUntilDueAsync(chunk, cancellationToken);
                    if (replay.LatenessMilliseconds > 0)
                    {
                        Interlocked.Increment(ref _replayLateCount);
                        lock (_stateGate)
                        {
                            _maxReplayLatenessMs = Math.Max(_maxReplayLatenessMs, replay.LatenessMilliseconds);
                        }
                    }

                    await virtualPort.WriteAsync(chunk.Bytes, cancellationToken);
                    var completedAt = _clock.GetTimestamp();
                    replayer.RecordWriteCompleted(chunk, completedAt);
                    var delayMs = TicksToMilliseconds(Math.Max(0, completedAt - chunk.ReceivedTimestamp));
                    lock (_stateGate)
                    {
                        _lastDeviceToVirtualDelayMs = delayMs;
                        _maxDeviceToVirtualDelayMs = Math.Max(_maxDeviceToVirtualDelayMs, delayMs);
                        MarkBridgeActivityLocked();
                    }

                    Interlocked.Add(ref _deviceToVirtualByteCount, chunk.Bytes.Length);
                    var count = Interlocked.Increment(ref _deviceToVirtualChunkCount);
                    RaiseStatusChangedPeriodically(count);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    Interlocked.Increment(ref _droppedDeviceToVirtualChunkCount);
                    Interlocked.Add(ref _droppedDeviceToVirtualByteCount, chunk.Bytes.Length);
                    throw;
                }
                finally
                {
                    lock (_stateGate)
                    {
                        _deviceToVirtualWriteActive = false;
                    }

                    SignalArbiter();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            FaultBridge($"Bridge device-to-virtual write failed: {ex.Message}");
        }
    }

    private async Task RunDeviceSchedulerAsync(
        Func<byte[], CancellationToken, Task> writeToDeviceAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[]? bridgeBytes = null;
                ManualRequest? manual = null;
                ManualTxStateChangedEventArgs? stateChange = null;
                double waitMs = Timeout.Infinite;
                lock (_stateGate)
                {
                    if (_virtualToDeviceQueue.TryDequeue(out bridgeBytes) && bridgeBytes is not null)
                    {
                        _virtualToDeviceWriteActive = true;
                    }
                    else if (_pendingManual is not null && _manualTxState == ManualTxState.WaitingForBridgeIdle)
                    {
                        waitMs = GetIdleGuardRemainingMsLocked(_clock.GetTimestamp());
                        if (waitMs <= 0 &&
                            _deviceToVirtualQueue.Count == 0 &&
                            _virtualToDeviceQueue.Count == 0 &&
                            !_deviceToVirtualWriteActive &&
                            !_virtualToDeviceWriteActive)
                        {
                            manual = _pendingManual;
                            stateChange = SetManualTxStateLocked(ManualTxState.Sending);
                            _virtualToDeviceWriteActive = true;
                        }
                        else if (waitMs <= 0)
                        {
                            waitMs = Timeout.Infinite;
                        }
                    }
                }

                RaiseManualTxStateChanged(stateChange);

                if (bridgeBytes is not null)
                {
                    try
                    {
                        await writeToDeviceAsync(bridgeBytes, cancellationToken);
                        Interlocked.Add(ref _virtualToDeviceByteCount, bridgeBytes.Length);
                        var count = Interlocked.Increment(ref _virtualToDeviceChunkCount);
                        lock (_stateGate) MarkBridgeActivityLocked();
                        RaiseStatusChangedPeriodically(count);
                    }
                    catch
                    {
                        Interlocked.Increment(ref _droppedVirtualToDeviceChunkCount);
                        Interlocked.Add(ref _droppedVirtualToDeviceByteCount, bridgeBytes.Length);
                        throw;
                    }
                    finally
                    {
                        lock (_stateGate) _virtualToDeviceWriteActive = false;
                        SignalArbiter();
                    }

                    continue;
                }

                if (manual is not null)
                {
                    var result = ManualTransmitResult.Canceled;
                    Exception? manualError = null;
                    try
                    {
                        await manual.TransmitAsync(cancellationToken);
                        result = ManualTransmitResult.Sent;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        result = ManualTransmitResult.Canceled;
                    }
                    catch (Exception ex)
                    {
                        result = ManualTransmitResult.Failed;
                        manualError = ex;
                    }
                    finally
                    {
                        ManualTxStateChangedEventArgs? completedStateChange;
                        lock (_stateGate)
                        {
                            manual.Completion.TrySetResult(result);
                            if (ReferenceEquals(_pendingManual, manual))
                            {
                                _pendingManual = null;
                            }

                            completedStateChange = SetManualTxStateLocked(ManualTxState.Idle);
                            _virtualToDeviceWriteActive = false;
                        }

                        RaiseManualTxStateChanged(completedStateChange);
                        SignalArbiter();
                        RaiseStatusChanged();
                    }

                    if (manualError is not null)
                    {
                        FaultBridge($"Bridge manual TX failed: {manualError.Message}");
                        return;
                    }

                    continue;
                }

                await WaitForArbiterSignalAsync(waitMs, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            FaultBridge($"Bridge physical write scheduler failed: {ex.Message}");
        }
    }

    private async Task WaitForArbiterSignalAsync(double waitMs, CancellationToken cancellationToken)
    {
        if (double.IsPositiveInfinity(waitMs) || waitMs == Timeout.Infinite)
        {
            await _arbiterSignal.WaitAsync(cancellationToken);
            return;
        }

        if (waitMs <= 0)
        {
            await Task.Yield();
            return;
        }

        await _arbiterSignal.WaitAsync(TimeSpan.FromMilliseconds(waitMs), cancellationToken);
    }

    private void CancelWaitingManual(ManualRequest request)
    {
        var canceled = false;
        ManualTxStateChangedEventArgs? stateChange = null;
        lock (_stateGate)
        {
            if (ReferenceEquals(_pendingManual, request) &&
                _manualTxState == ManualTxState.WaitingForBridgeIdle)
            {
                _pendingManual = null;
                stateChange = SetManualTxStateLocked(ManualTxState.Idle);
                canceled = true;
            }
        }

        if (canceled)
        {
            RaiseManualTxStateChanged(stateChange);
            request.Completion.TrySetResult(ManualTransmitResult.Canceled);
            SignalArbiter();
            RaiseStatusChanged();
        }
    }

    private void FaultBridge(string message)
    {
        CancellationTokenSource? cancellation;
        SerialPortStream? virtualPort;
        ManualRequest? manual;
        ManualTxStateChangedEventArgs? stateChange;
        lock (_stateGate)
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            _lastError = message;
            _lastFaultReason = message;
            cancellation = _cancellation;
            virtualPort = _virtualPort;
            manual = _pendingManual;
            _pendingManual = null;
            stateChange = SetManualTxStateLocked(ManualTxState.Idle);
        }

        Interlocked.Increment(ref _errorCount);
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        _deviceToVirtualQueue.TryComplete();
        _virtualToDeviceQueue.TryComplete();
        manual?.Completion.TrySetResult(ManualTransmitResult.Canceled);
        RaiseManualTxStateChanged(stateChange);
        Error?.Invoke(this, message);
        RaiseStatusChanged();
        _ = Task.Run(() => SafeCloseAndDispose(virtualPort));
    }

    private async Task StopCurrentAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? cancellation;
        SerialPortStream? virtualPort;
        Task? readerTask;
        Task? writerTask;
        Task? deviceWriterTask;
        ManualRequest? manual;
        ManualTxStateChangedEventArgs? stateChange;
        lock (_stateGate)
        {
            cancellation = _cancellation;
            virtualPort = _virtualPort;
            readerTask = _readerTask;
            writerTask = _writerTask;
            deviceWriterTask = _deviceWriterTask;
            manual = _pendingManual;
            _cancellation = null;
            _virtualPort = null;
            _readerTask = null;
            _writerTask = null;
            _deviceWriterTask = null;
            _pendingManual = null;
            stateChange = SetManualTxStateLocked(ManualTxState.Idle);
            _isRunning = false;
        }

        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        _deviceToVirtualQueue.TryComplete();
        _virtualToDeviceQueue.TryComplete();
        manual?.Completion.TrySetResult(ManualTransmitResult.Canceled);
        RaiseManualTxStateChanged(stateChange);
        SignalArbiter();
        SafeCloseAndDispose(virtualPort);

        foreach (var task in new[] { readerTask, writerTask, deviceWriterTask })
        {
            if (task is null) continue;
            try { await task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }

        cancellation?.Dispose();
        RaiseStatusChanged();
    }

    private void MarkBridgeActivityLocked()
    {
        _lastBridgeActivityTimestamp = _clock.GetTimestamp();
        _lastBridgeActivityAt = DateTimeOffset.Now;
    }

    private double GetIdleGuardRemainingMsLocked(long now)
    {
        if (_lastBridgeActivityTimestamp == 0) return 0;
        var elapsedMs = TicksToMilliseconds(Math.Max(0, now - _lastBridgeActivityTimestamp));
        return Math.Max(0, _manualTxIdleGuardMs - elapsedMs);
    }

    private long MillisecondsToTicks(double milliseconds) =>
        (long)Math.Ceiling(milliseconds * _clock.Frequency / 1000d);

    private double TicksToMilliseconds(long ticks) => ticks * 1000d / _clock.Frequency;

    private void SignalArbiter()
    {
        try { _arbiterSignal.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private void ReportError(string message)
    {
        Interlocked.Increment(ref _errorCount);
        lock (_stateGate) _lastError = message;
        Error?.Invoke(this, message);
        RaiseStatusChanged();
    }

    private void RaiseStatusChangedPeriodically(long count)
    {
        if (count == 1 || count % 16 == 0) RaiseStatusChanged();
    }

    private void RaiseStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);

    private ManualTxStateChangedEventArgs? SetManualTxStateLocked(ManualTxState state)
    {
        if (_manualTxState == state)
        {
            return null;
        }

        var previous = _manualTxState;
        _manualTxState = state;
        return new ManualTxStateChangedEventArgs(previous, state);
    }

    private void RaiseManualTxStateChanged(ManualTxStateChangedEventArgs? args)
    {
        if (args is null)
        {
            return;
        }

        try
        {
            ManualTxStateChanged?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            ReportError($"Manual TX state observer failed: {ex.Message}");
        }
    }

    internal static BridgeQueueEnqueueResult TryEnqueueVirtualToDevice(
        bool bridgeRunning,
        BoundedByteQueue<byte[]> queue,
        byte[] chunk)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(chunk);
        if (!bridgeRunning)
        {
            return BridgeQueueEnqueueResult.BridgeStopped;
        }

        return queue.TryEnqueue(chunk, chunk.Length)
            ? BridgeQueueEnqueueResult.Enqueued
            : BridgeQueueEnqueueResult.Overflow;
    }

    private static BoundedByteQueue<BridgeRxChunk> CreateDeviceQueue(int chunks, int bytes) => new(chunks, bytes);
    private static BoundedByteQueue<byte[]> CreateByteQueue(int chunks, int bytes) => new(chunks, bytes);

    private static SerialPortStream CreateVirtualPort(string portName, SerialSettings settings) =>
        new(portName, settings.BaudRate, 8, Parity.None, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadBufferSize = 1024 * 1024,
            WriteBufferSize = 1024 * 1024,
            ReadTimeout = Timeout.Infinite,
            WriteTimeout = 1000,
            DtrEnable = false,
            RtsEnable = false
        };

    private static void SafeCloseAndDispose(SerialPortStream? serialPort)
    {
        if (serialPort is null) return;
        try { serialPort.Close(); } catch { }
        try { serialPort.Dispose(); } catch { }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record ManualRequest(
        Func<CancellationToken, Task> TransmitAsync,
        long QueuedTimestamp)
    {
        public TaskCompletionSource<ManualTransmitResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
