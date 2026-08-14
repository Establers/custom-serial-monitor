using System.Diagnostics;
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
    internal const int MaximumOutstandingBridgeSessionCount = 4;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _pendingStartGate = new();
    private readonly object _sessionGate = new();
    private readonly IBridgeClock _clock;
    private readonly Func<string, SerialSettings, IBridgePortConnection> _portFactory;
    private readonly BoundedPortLifecycle<IBridgePortConnection> _portLifecycle;
    private readonly TimeSpan _stopTimeout;
    private readonly BoundedByteQueue<BridgeRxChunk> _idleDeviceToVirtualQueue;
    private readonly BoundedByteQueue<byte[]> _idleVirtualToDeviceQueue;
    private CancellationTokenSource? _pendingStartCancellation;
    private BridgeSession? _currentSession;
    private readonly HashSet<BridgeSession> _detachedSessions = [];
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
    private int _deviceToVirtualGroupTimeoutMs;
    private bool _deviceToVirtualWriteActive;
    private bool _virtualToDeviceWriteActive;
    private ManualTxState _manualTxState;
    private ManualRequest? _pendingManual;
    private long _startGeneration;
    private bool _isRunning;
    private string _lastStopMode = "not started";
    private bool _disposed;

    public SerialBridgeService()
        : this(
            new SystemBridgeClock(),
            CreateVirtualPort,
            BoundedPortLifecycle<IBridgePortConnection>.DefaultOpenTimeout,
            BoundedPortLifecycle<IBridgePortConnection>.DefaultCleanupTimeout,
            BoundedPortLifecycle<IBridgePortConnection>.DefaultMaximumOutstandingOperations)
    {
    }

    internal SerialBridgeService(IBridgeClock clock)
        : this(
            clock,
            CreateVirtualPort,
            BoundedPortLifecycle<IBridgePortConnection>.DefaultOpenTimeout,
            BoundedPortLifecycle<IBridgePortConnection>.DefaultCleanupTimeout,
            BoundedPortLifecycle<IBridgePortConnection>.DefaultMaximumOutstandingOperations)
    {
    }

    internal SerialBridgeService(
        IBridgeClock clock,
        Func<string, SerialSettings, IBridgePortConnection> portFactory,
        TimeSpan openTimeout,
        TimeSpan stopTimeout,
        int maximumOutstandingOperations = BoundedPortLifecycle<IBridgePortConnection>.DefaultMaximumOutstandingOperations)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(portFactory);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stopTimeout, TimeSpan.Zero);
        _clock = clock;
        _portFactory = portFactory;
        _stopTimeout = stopTimeout;
        _portLifecycle = new BoundedPortLifecycle<IBridgePortConnection>(
            openTimeout,
            stopTimeout,
            maximumOutstandingOperations);
        _idleDeviceToVirtualQueue = CreateDeviceQueue(
            BridgeSettings.DefaultMaxQueuedChunks,
            BridgeSettings.DefaultMaxQueuedBytes);
        _idleVirtualToDeviceQueue = CreateByteQueue(
            BridgeSettings.DefaultMaxQueuedChunks,
            BridgeSettings.DefaultMaxQueuedBytes);
    }

    internal int OutstandingPortOperationCount => _portLifecycle.OutstandingOperationCount;

    internal int OutstandingBridgeSessionCount
    {
        get { lock (_sessionGate) return _detachedSessions.Count; }
    }

    internal string LastStopMode
    {
        get { lock (_stateGate) return _lastStopMode; }
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

    public int PendingDeviceToVirtualChunkCount => CurrentDeviceQueue.Count;
    public int PendingVirtualToDeviceChunkCount => CurrentVirtualQueue.Count;
    public int PendingDeviceToVirtualByteCount => CurrentDeviceQueue.ByteCount;
    public int PendingVirtualToDeviceByteCount => CurrentVirtualQueue.ByteCount;
    public double OldestPendingChunkAgeMs => Math.Max(
        CurrentDeviceQueue.OldestAgeMilliseconds,
        CurrentVirtualQueue.OldestAgeMilliseconds);
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

    public int DeviceToVirtualGroupTimeoutMs
    {
        get { lock (_stateGate) return _deviceToVirtualGroupTimeoutMs; }
    }

    public async Task StartAsync(
        BridgeSettings settings,
        SerialSettings deviceSettings,
        Func<byte[], CancellationToken, Task> writeToDeviceAsync,
        CancellationToken cancellationToken,
        long sourceSerialSessionGeneration = 0)
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

        var (operationCancellation, generation) = BeginStart(cancellationToken);
        var operationToken = operationCancellation.Token;
        var lifecycleEntered = false;
        try
        {
            await _lifecycleGate.WaitAsync(operationToken);
            lifecycleEntered = true;
            await StopCurrentAsync(CancellationToken.None);
            EnsureSessionCapacity();
            var virtualPort = await _portLifecycle.OpenAsync(
                () => _portFactory(virtualPortName, deviceSettings.Clone()),
                operationToken);

            var session = new BridgeSession(
                generation,
                virtualPort,
                CreateDeviceQueue(maxChunks, maxBytes),
                CreateByteQueue(maxChunks, maxBytes),
                writeToDeviceAsync,
                sourceSerialSessionGeneration,
                cancellationToken);
            lock (_stateGate)
            {
                _currentSession = session;
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
            }

            session.ReaderTask = Task.Run(() => RunVirtualReaderAsync(session, virtualPort));
            session.WriterTask = Task.Run(() => RunVirtualWriterAsync(session, virtualPort));
            session.DeviceWriterTask = Task.Run(() => RunDeviceSchedulerAsync(session));
            CommitRunning(session, generation, operationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (lifecycleEntered)
            {
                await StopCurrentAsync(CancellationToken.None);
            }

            ReportError($"Bridge start failed for {virtualPortName}: {ex.Message}");
            throw;
        }
        catch
        {
            if (lifecycleEntered)
            {
                await StopCurrentAsync(CancellationToken.None);
            }

            throw;
        }
        finally
        {
            EndStart(operationCancellation);
            if (lifecycleEntered)
            {
                _lifecycleGate.Release();
            }
        }
    }

    public void CancelPendingStart()
    {
        CancellationTokenSource? cancellation;
        lock (_pendingStartGate)
        {
            _startGeneration++;
            cancellation = _pendingStartCancellation;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void BeginStop()
    {
        CancelPendingStart();

        BridgeSession? session;
        ManualRequest? manual;
        ManualTxStateChangedEventArgs? stateChange;
        lock (_stateGate)
        {
            session = _currentSession;
            if (session is null)
            {
                _isRunning = false;
                return;
            }

            _isRunning = false;
            manual = _pendingManual;
            _pendingManual = null;
            stateChange = SetManualTxStateLocked(ManualTxState.Idle);
            _deviceToVirtualWriteActive = false;
            _virtualToDeviceWriteActive = false;
        }

        session.CancelAndComplete();
        manual?.Completion.TrySetResult(ManualTransmitResult.Canceled);
        RaiseManualTxStateChanged(stateChange);

        if (!session.TrySchedulePortRetire(_portLifecycle.TryScheduleRetire))
        {
            ReportError(
                "Bridge port cleanup limit is exhausted; the stopped session retains cleanup ownership.");
        }

        RaiseStatusChanged();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        BeginStop();
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

    public void ConfigureDeviceToVirtualGrouping(int idleTimeoutMs)
    {
        var changed = false;
        lock (_stateGate)
        {
            var normalized = Math.Clamp(idleTimeoutMs, 0, 5_000);
            if (_deviceToVirtualGroupTimeoutMs != normalized)
            {
                _deviceToVirtualGroupTimeoutMs = normalized;
                changed = true;
            }
        }

        if (changed)
        {
            RaiseStatusChanged();
        }
    }

    public bool TryEnqueueDeviceChunk(BridgeRxChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Bytes.Length == 0)
        {
            return true;
        }

        BridgeSession? session;
        lock (_stateGate)
        {
            session = _currentSession;
            if (!_isRunning || session is null)
            {
                return false;
            }

            if (chunk.SourceSerialSessionGeneration != session.SourceSerialSessionGeneration)
            {
                return false;
            }

            var queuedChunk = chunk with
            {
                DeviceToVirtualGroupTimeoutMs = _deviceToVirtualGroupTimeoutMs
            };
            if (session.DeviceToVirtualQueue.TryEnqueue(queuedChunk, queuedChunk.Bytes.Length))
            {
                MarkBridgeActivityLocked();
                SignalArbiter(session);
                return true;
            }
        }

        Interlocked.Increment(ref _droppedDeviceToVirtualChunkCount);
        Interlocked.Add(ref _droppedDeviceToVirtualByteCount, chunk.Bytes.Length);
        Interlocked.Increment(ref _queueOverflowCount);
        FaultBridge(session!, DeviceToVirtualOverflowMessage);
        return false;
    }

    public async Task<ManualTransmitResult> QueueManualTransmitAsync(
        Func<CancellationToken, Task> transmitAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transmitAsync);
        ManualRequest request;
        BridgeSession session;
        ManualTxStateChangedEventArgs? stateChange;
        lock (_stateGate)
        {
            session = _currentSession!;
            if (!_isRunning || session is null)
            {
                return ManualTransmitResult.BridgeNotRunning;
            }

            if (_manualTxState != ManualTxState.Idle || _pendingManual is not null)
            {
                return ManualTransmitResult.Busy;
            }

            request = new ManualRequest(session, transmitAsync, _clock.GetTimestamp());
            _pendingManual = request;
            stateChange = SetManualTxStateLocked(ManualTxState.WaitingForBridgeIdle);
        }

        RaiseManualTxStateChanged(stateChange);
        RaiseStatusChanged();
        SignalArbiter(session);
        using var registration = cancellationToken.Register(() => CancelWaitingManual(request));
        return await request.Completion.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        CancelPendingStart();
        _disposed = true;
        await StopAsync(CancellationToken.None);
        _lifecycleGate.Dispose();
    }

    private async Task RunVirtualReaderAsync(BridgeSession session, IBridgePortConnection virtualPort)
    {
        var cancellationToken = session.Cancellation.Token;
        var buffer = new byte[8192];
        try
        {
            await session.WaitUntilCommittedAsync();
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
                        ReferenceEquals(_currentSession, session) && _isRunning,
                        session.VirtualToDeviceQueue,
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
                    FaultBridge(session, "Bridge stopped: physical COM consumer too slow");
                    return;
                }

                SignalArbiter(session);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            FaultBridge(session, $"Bridge virtual-to-device failed: {ex.Message}");
        }
    }

    private async Task RunVirtualWriterAsync(BridgeSession session, IBridgePortConnection virtualPort)
    {
        var cancellationToken = session.Cancellation.Token;
        var replayer = new BridgeGapReplayer(_clock);
        var grouper = new BridgeDeviceChunkGrouper();
        BridgeRxChunk? deferredChunk = null;
        int? replayGroupTimeoutMs = null;
        try
        {
            await session.WaitUntilCommittedAsync();
            while (deferredChunk is not null || await session.DeviceToVirtualQueue.WaitToReadAsync(cancellationToken))
            {
                BridgeRxChunk? chunk = deferredChunk;
                deferredChunk = null;
                lock (_stateGate)
                {
                    if (!ReferenceEquals(_currentSession, session))
                    {
                        return;
                    }

                    if (chunk is null &&
                        (!session.DeviceToVirtualQueue.TryDequeue(out chunk) || chunk is null))
                    {
                        continue;
                    }

                    _deviceToVirtualWriteActive = true;
                }

                try
                {
                    if (chunk.DeviceToVirtualGroupTimeoutMs > 0)
                    {
                        grouper.Append(chunk);
                        BridgeGroupFlushReason? flushReason = null;
                        while (!flushReason.HasValue)
                        {
                            flushReason = grouper.GetImmediateFlushReason(_clock.GetTimestamp());
                            if (flushReason.HasValue)
                            {
                                break;
                            }

                            BridgeRxChunk? nextChunk;
                            lock (_stateGate)
                            {
                                session.DeviceToVirtualQueue.TryDequeue(out nextChunk);
                            }

                            if (nextChunk is not null)
                            {
                                flushReason = grouper.GetFlushReasonBeforeAppend(nextChunk);
                                if (!flushReason.HasValue)
                                {
                                    grouper.Append(nextChunk);
                                    continue;
                                }

                                deferredChunk = nextChunk;
                                break;
                            }

                            var wait = grouper.GetNextWait(_clock.GetTimestamp());
                            if (wait.Delay <= TimeSpan.Zero)
                            {
                                flushReason = wait.TimeoutReason;
                                break;
                            }

                            if (!await WaitForDeviceChunkAsync(session, wait.Delay, cancellationToken))
                            {
                                flushReason = wait.TimeoutReason;
                            }
                        }

                        chunk = grouper.BuildAndReset(flushReason!.Value);
                    }

                    if (replayGroupTimeoutMs.HasValue &&
                        replayGroupTimeoutMs.Value != chunk.DeviceToVirtualGroupTimeoutMs)
                    {
                        replayer.Reset();
                    }

                    replayGroupTimeoutMs = chunk.DeviceToVirtualGroupTimeoutMs;

                    await WriteDeviceChunkToVirtualAsync(
                        session,
                        virtualPort,
                        replayer,
                        chunk,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
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
                        if (ReferenceEquals(_currentSession, session))
                        {
                            _deviceToVirtualWriteActive = false;
                        }
                    }

                    SignalArbiter(session);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            FaultBridge(session, $"Bridge device-to-virtual write failed: {ex.Message}");
        }
    }

    private async Task<bool> WaitForDeviceChunkAsync(
        BridgeSession session,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            return await session.DeviceToVirtualQueue.WaitToReadAsync(timeoutCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task WriteDeviceChunkToVirtualAsync(
        BridgeSession session,
        IBridgePortConnection virtualPort,
        BridgeGapReplayer replayer,
        BridgeRxChunk chunk,
        CancellationToken cancellationToken)
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

        await virtualPort.WriteAsync(chunk.Bytes.AsMemory(), cancellationToken);
        var completedAt = _clock.GetTimestamp();
        replayer.RecordWriteCompleted(chunk, completedAt);
        var delayMs = TicksToMilliseconds(Math.Max(0, completedAt - chunk.ReceivedTimestamp));
        lock (_stateGate)
        {
            if (ReferenceEquals(_currentSession, session))
            {
                _lastDeviceToVirtualDelayMs = delayMs;
                _maxDeviceToVirtualDelayMs = Math.Max(_maxDeviceToVirtualDelayMs, delayMs);
                MarkBridgeActivityLocked();
            }
        }

        Interlocked.Add(ref _deviceToVirtualByteCount, chunk.Bytes.Length);
        var count = Interlocked.Increment(ref _deviceToVirtualChunkCount);
        RaiseStatusChangedPeriodically(count);
    }

    private async Task RunDeviceSchedulerAsync(BridgeSession session)
    {
        var cancellationToken = session.Cancellation.Token;
        try
        {
            await session.WaitUntilCommittedAsync();
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[]? bridgeBytes = null;
                ManualRequest? manual = null;
                ManualTxStateChangedEventArgs? stateChange = null;
                double waitMs = Timeout.Infinite;
                lock (_stateGate)
                {
                    if (!ReferenceEquals(_currentSession, session))
                    {
                        return;
                    }

                    if (session.VirtualToDeviceQueue.TryDequeue(out bridgeBytes) && bridgeBytes is not null)
                    {
                        _virtualToDeviceWriteActive = true;
                    }
                    else if (_pendingManual is not null && _manualTxState == ManualTxState.WaitingForBridgeIdle)
                    {
                        waitMs = GetIdleGuardRemainingMsLocked(_clock.GetTimestamp());
                        if (waitMs <= 0 &&
                            session.DeviceToVirtualQueue.Count == 0 &&
                            session.VirtualToDeviceQueue.Count == 0 &&
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
                        await session.WriteToDeviceAsync(bridgeBytes, cancellationToken);
                        Interlocked.Add(ref _virtualToDeviceByteCount, bridgeBytes.Length);
                        var count = Interlocked.Increment(ref _virtualToDeviceChunkCount);
                        lock (_stateGate)
                        {
                            if (ReferenceEquals(_currentSession, session))
                            {
                                MarkBridgeActivityLocked();
                            }
                        }
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
                        lock (_stateGate)
                        {
                            if (ReferenceEquals(_currentSession, session))
                            {
                                _virtualToDeviceWriteActive = false;
                            }
                        }
                        SignalArbiter(session);
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
                            if (ReferenceEquals(_currentSession, session) &&
                                ReferenceEquals(_pendingManual, manual))
                            {
                                _pendingManual = null;
                                completedStateChange = SetManualTxStateLocked(ManualTxState.Idle);
                                _virtualToDeviceWriteActive = false;
                            }
                            else
                            {
                                completedStateChange = null;
                            }
                        }

                        RaiseManualTxStateChanged(completedStateChange);
                        SignalArbiter(session);
                        RaiseStatusChanged();
                    }

                    if (manualError is not null)
                    {
                        FaultBridge(session, $"Bridge manual TX failed: {manualError.Message}");
                        return;
                    }

                    continue;
                }

                await WaitForArbiterSignalAsync(session, waitMs, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            FaultBridge(session, $"Bridge physical write scheduler failed: {ex.Message}");
        }
    }

    private async Task WaitForArbiterSignalAsync(
        BridgeSession session,
        double waitMs,
        CancellationToken cancellationToken)
    {
        if (double.IsPositiveInfinity(waitMs) || waitMs == Timeout.Infinite)
        {
            await session.ArbiterSignal.WaitAsync(cancellationToken);
            return;
        }

        if (waitMs <= 0)
        {
            await Task.Yield();
            return;
        }

        await session.ArbiterSignal.WaitAsync(TimeSpan.FromMilliseconds(waitMs), cancellationToken);
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
            SignalArbiter(request.Session);
            RaiseStatusChanged();
        }
    }

    private void FaultBridge(BridgeSession session, string message)
    {
        ManualRequest? manual;
        ManualTxStateChangedEventArgs? stateChange;
        lock (_stateGate)
        {
            if (!_isRunning || !ReferenceEquals(_currentSession, session))
            {
                return;
            }

            _isRunning = false;
            _lastError = message;
            _lastFaultReason = message;
            _currentSession = null;
            manual = _pendingManual;
            _pendingManual = null;
            stateChange = SetManualTxStateLocked(ManualTxState.Idle);
        }

        Interlocked.Increment(ref _errorCount);
        session.CancelAndComplete();
        manual?.Completion.TrySetResult(ManualTransmitResult.Canceled);
        RaiseManualTxStateChanged(stateChange);
        Error?.Invoke(this, message);
        RaiseStatusChanged();
        var virtualPort = session.DetachPort();
        if (virtualPort is not null && !_portLifecycle.TryScheduleRetire(virtualPort))
        {
            ReportError("Bridge port cleanup limit is exhausted; restart is disabled until an earlier OS operation finishes.");
        }

        TrackDetachedSession(session);
    }

    private async Task StopCurrentAsync(CancellationToken cancellationToken)
    {
        BridgeSession? session;
        ManualRequest? manual;
        ManualTxStateChangedEventArgs? stateChange;
        lock (_stateGate)
        {
            session = _currentSession;
            if (session is null)
            {
                _isRunning = false;
                return;
            }

            manual = _pendingManual;
            _currentSession = null;
            _pendingManual = null;
            stateChange = SetManualTxStateLocked(ManualTxState.Idle);
            _isRunning = false;
        }

        session.CancelAndComplete();
        manual?.Completion.TrySetResult(ManualTransmitResult.Canceled);
        RaiseManualTxStateChanged(stateChange);
        SignalArbiter(session);
        var stopStartedAt = Stopwatch.GetTimestamp();
        var callerCanceled = false;
        var virtualPort = session.DetachPort();
        if (virtualPort is not null)
        {
            var remaining = RemainingStopTime(stopStartedAt);
            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    var retiredWithinDeadline = await _portLifecycle.RetireAsync(
                        virtualPort,
                        remaining,
                        cancellationToken).ConfigureAwait(false);
                    if (!retiredWithinDeadline)
                    {
                        ReportError(
                            "Bridge port cleanup exceeded its bounded deadline; cleanup remains tracked in the background.");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    callerCanceled = true;
                }
            }
            else if (!_portLifecycle.TryScheduleRetire(virtualPort))
            {
                ReportError("Bridge port cleanup limit is exhausted.");
            }
        }

        var workers = session.Workers
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        var graceful = workers.All(task => task.IsCompleted);
        var workerRemaining = RemainingStopTime(stopStartedAt);
        if (!graceful && workers.Length > 0 && workerRemaining > TimeSpan.Zero)
        {
            try
            {
                await Task.WhenAll(workers).WaitAsync(workerRemaining, cancellationToken);
                graceful = true;
            }
            catch (TimeoutException) { }
            catch (OperationCanceledException)
            {
                callerCanceled = cancellationToken.IsCancellationRequested;
            }
            catch (Exception) { }
        }

        graceful |= workers.All(task => task.IsCompleted);
        if (graceful)
        {
            foreach (var worker in workers)
            {
                _ = worker.Exception;
            }

            session.Dispose();
            SetLastStopMode("graceful stop");
        }
        else
        {
            TrackDetachedSession(session);
            SetLastStopMode("forced stop after bounded worker timeout");
            ReportError(
                "Bridge worker stop exceeded its bounded deadline; the old session remains isolated and tracked.");
        }

        RaiseStatusChanged();
        if (callerCanceled)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
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

    private void SignalArbiter(BridgeSession session)
    {
        try { session.ArbiterSignal.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private BoundedByteQueue<BridgeRxChunk> CurrentDeviceQueue =>
        Volatile.Read(ref _currentSession)?.DeviceToVirtualQueue ?? _idleDeviceToVirtualQueue;

    private BoundedByteQueue<byte[]> CurrentVirtualQueue =>
        Volatile.Read(ref _currentSession)?.VirtualToDeviceQueue ?? _idleVirtualToDeviceQueue;

    private void EnsureSessionCapacity()
    {
        lock (_sessionGate)
        {
            if (_detachedSessions.Count >= MaximumOutstandingBridgeSessionCount)
            {
                throw new InvalidOperationException(
                    $"Bridge worker cleanup capacity is exhausted " +
                    $"({_detachedSessions.Count}/{MaximumOutstandingBridgeSessionCount}); " +
                    "wait for an earlier bridge session to exit before starting another.");
            }
        }
    }

    private void TrackDetachedSession(BridgeSession session)
    {
        var workers = session.Workers.Where(task => task is not null).Cast<Task>().ToArray();
        if (workers.Length == 0 || workers.All(task => task.IsCompleted))
        {
            foreach (var worker in workers)
            {
                _ = worker.Exception;
            }

            session.Dispose();
            return;
        }

        lock (_sessionGate)
        {
            _detachedSessions.Add(session);
        }

        _ = Task.WhenAll(workers).ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                lock (_sessionGate)
                {
                    _detachedSessions.Remove(session);
                }

                session.Dispose();
                RaiseStatusChanged();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void SetLastStopMode(string mode)
    {
        lock (_stateGate)
        {
            _lastStopMode = mode;
        }
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

    private static IBridgePortConnection CreateVirtualPort(string portName, SerialSettings settings)
    {
        SerialPortStream? serialPort = null;
        try
        {
            serialPort = new SerialPortStream(
                portName,
                settings.BaudRate,
                8,
                Parity.None,
                StopBits.One);
            serialPort.Handshake = Handshake.None;
            serialPort.ReadBufferSize = 1024 * 1024;
            serialPort.WriteBufferSize = 1024 * 1024;
            serialPort.ReadTimeout = Timeout.Infinite;
            serialPort.WriteTimeout = 1000;
            serialPort.DtrEnable = false;
            serialPort.RtsEnable = false;
            return new BridgePortConnectionAdapter(serialPort);
        }
        catch
        {
            try { serialPort?.Close(); } catch { }
            try { serialPort?.Dispose(); } catch { }
            throw;
        }
    }

    private (CancellationTokenSource Cancellation, long Generation) BeginStart(
        CancellationToken cancellationToken)
    {
        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_pendingStartGate)
        {
            if (_pendingStartCancellation is not null)
            {
                operationCancellation.Dispose();
                throw new InvalidOperationException("A bridge start attempt is already in progress.");
            }

            _pendingStartCancellation = operationCancellation;
            return (operationCancellation, ++_startGeneration);
        }
    }

    private void EndStart(CancellationTokenSource operationCancellation)
    {
        lock (_pendingStartGate)
        {
            if (ReferenceEquals(_pendingStartCancellation, operationCancellation))
            {
                _pendingStartCancellation = null;
            }
        }

        operationCancellation.Dispose();
    }

    private void CommitRunning(
        BridgeSession session,
        long generation,
        CancellationToken cancellationToken)
    {
        lock (_pendingStartGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _startGeneration || _pendingStartCancellation is null)
            {
                throw new OperationCanceledException("The bridge start attempt was superseded.", cancellationToken);
            }

            lock (_stateGate)
            {
                if (!ReferenceEquals(_currentSession, session))
                {
                    throw new OperationCanceledException(
                        "The bridge session was replaced before commit.",
                        cancellationToken);
                }

                _isRunning = true;
            }

            session.CommitStarted();
            RaiseStatusChanged();
        }
    }

    private TimeSpan RemainingStopTime(long startedAt) =>
        _stopTimeout - Stopwatch.GetElapsedTime(startedAt);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record ManualRequest(
        BridgeSession Session,
        Func<CancellationToken, Task> TransmitAsync,
        long QueuedTimestamp)
    {
        public TaskCompletionSource<ManualTransmitResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BridgeSession : IDisposable
    {
        private readonly object _portGate = new();
        private IBridgePortConnection? _port;
        private int _disposed;

        public BridgeSession(
            long generation,
            IBridgePortConnection port,
            BoundedByteQueue<BridgeRxChunk> deviceToVirtualQueue,
            BoundedByteQueue<byte[]> virtualToDeviceQueue,
            Func<byte[], CancellationToken, Task> writeToDeviceAsync,
            long sourceSerialSessionGeneration,
            CancellationToken lifetimeCancellationToken)
        {
            Generation = generation;
            _port = port;
            DeviceToVirtualQueue = deviceToVirtualQueue;
            VirtualToDeviceQueue = virtualToDeviceQueue;
            WriteToDeviceAsync = writeToDeviceAsync;
            SourceSerialSessionGeneration = sourceSerialSessionGeneration;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellationToken);
        }

        public long Generation { get; }

        public IBridgePortConnection? Port
        {
            get { lock (_portGate) return _port; }
        }

        public BoundedByteQueue<BridgeRxChunk> DeviceToVirtualQueue { get; }

        public BoundedByteQueue<byte[]> VirtualToDeviceQueue { get; }

        public Func<byte[], CancellationToken, Task> WriteToDeviceAsync { get; }

        public long SourceSerialSessionGeneration { get; }

        public CancellationTokenSource Cancellation { get; }

        public SemaphoreSlim ArbiterSignal { get; } = new(0, 1);

        private TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? ReaderTask { get; set; }

        public Task? WriterTask { get; set; }

        public Task? DeviceWriterTask { get; set; }

        public IEnumerable<Task?> Workers
        {
            get
            {
                yield return ReaderTask;
                yield return WriterTask;
                yield return DeviceWriterTask;
            }
        }

        public IBridgePortConnection? DetachPort()
        {
            lock (_portGate)
            {
                var port = _port;
                _port = null;
                return port;
            }
        }

        public bool TrySchedulePortRetire(Func<IBridgePortConnection, bool> scheduleRetire)
        {
            lock (_portGate)
            {
                if (_port is null)
                {
                    return true;
                }

                if (!scheduleRetire(_port))
                {
                    return false;
                }

                _port = null;
                return true;
            }
        }

        public Task WaitUntilCommittedAsync() => Started.Task.WaitAsync(Cancellation.Token);

        public void CommitStarted() => Started.TrySetResult();

        public void CancelAndComplete()
        {
            try { Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            DeviceToVirtualQueue.TryComplete();
            VirtualToDeviceQueue.TryComplete();
            try { ArbiterSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Cancellation.Dispose();
            ArbiterSignal.Dispose();
        }
    }
}
