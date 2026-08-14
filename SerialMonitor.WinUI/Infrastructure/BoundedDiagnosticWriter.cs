using System.Diagnostics;
using System.Threading.Channels;

namespace SerialMonitor.WinUI.Infrastructure;

internal readonly record struct GeneralDiagnosticWork(
    GeneralDiagnosticWorkKind Kind,
    string Text,
    DateTimeOffset Timestamp,
    string Source = "",
    Exception? Exception = null,
    TaskCompletionSource<bool>? Completion = null);

internal readonly record struct CriticalDiagnosticWriteResult(
    bool WasEnqueued,
    bool CompletedSuccessfully);

internal enum GeneralDiagnosticWorkKind
{
    LoadExisting,
    Startup,
    Error,
    Shutdown,
    ClearLastError,
    FlushBarrier
}

/// <summary>
/// A single-consumer diagnostic queue whose capacity includes the item currently
/// executing in the sink. Newest work is rejected when that total is full.
/// </summary>
internal sealed class BoundedDiagnosticWriter : IAsyncDisposable
{
    private readonly object _enqueueGate = new();
    private readonly int _capacity;
    private readonly Action<GeneralDiagnosticWork, long> _sink;
    private readonly Channel<GeneralDiagnosticWork> _channel;
    private readonly SemaphoreSlim _availableSlots;
    private readonly CancellationTokenSource _completionCancellation = new();
    private readonly Task _pumpTask;
    private int _pendingWorkCount;
    private long _droppedWorkCount;
    private long _droppedSinceLastAccepted;
    private long _sinkErrorCount;
    private bool _completed;
    private GeneralDiagnosticWork? _stagedCriticalWork;
    private bool _criticalWorkInFlight;
    private bool _criticalWorkAcceptedForSession;

    public BoundedDiagnosticWriter(
        int capacity,
        Action<GeneralDiagnosticWork, long> sink)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentNullException.ThrowIfNull(sink);

        _capacity = capacity;
        _sink = sink;
        _availableSlots = new SemaphoreSlim(capacity, capacity);
        _channel = Channel.CreateBounded<GeneralDiagnosticWork>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _pumpTask = Task.Run(PumpAsync, CancellationToken.None);
    }

    public int Capacity => _capacity;

    public int PendingWorkCount => Volatile.Read(ref _pendingWorkCount);

    public long DroppedWorkCount => Interlocked.Read(ref _droppedWorkCount);

    public long SinkErrorCount => Interlocked.Read(ref _sinkErrorCount);

    internal bool IsCompleted
    {
        get
        {
            lock (_enqueueGate)
            {
                return _completed;
            }
        }
    }

    internal Task Completion => _pumpTask;

    public bool TryEnqueue(GeneralDiagnosticWork work)
    {
        return TryEnqueueCore(work, countRejectionAsDrop: true);
    }

    /// <summary>
    /// Stages the one shutdown-class record allowed for this writer session.
    /// The staging slot is separate from queue capacity so a full hot-path
    /// queue cannot discard shutdown. Before a flush starts, a newer staged
    /// value replaces the older value. Once an attempt is in flight or has
    /// entered the FIFO, additional critical records are rejected to prevent
    /// duplicate durability writes after a caller-side timeout.
    /// </summary>
    public bool TryStageCriticalWork(GeneralDiagnosticWork work)
    {
        if (work.Kind != GeneralDiagnosticWorkKind.Shutdown)
        {
            return false;
        }

        lock (_enqueueGate)
        {
            if (_completed ||
                _criticalWorkInFlight ||
                _criticalWorkAcceptedForSession)
            {
                return false;
            }

            _stagedCriticalWork = work with { Completion = null };
            return true;
        }
    }

    internal bool HasStagedCriticalWork
    {
        get
        {
            lock (_enqueueGate)
            {
                return _stagedCriticalWork.HasValue;
            }
        }
    }

    public async Task<bool> FlushAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return false;
        }

        try
        {
            var startedAt = Stopwatch.GetTimestamp();
            GeneralDiagnosticWork? criticalWork = null;
            lock (_enqueueGate)
            {
                if (!_completed &&
                    !_criticalWorkInFlight &&
                    !_criticalWorkAcceptedForSession &&
                    _stagedCriticalWork is { } staged)
                {
                    criticalWork = staged;
                    _stagedCriticalWork = null;
                    _criticalWorkInFlight = true;
                }
            }

            if (criticalWork is { } critical)
            {
                CriticalDiagnosticWriteResult result;
                try
                {
                    result = await EnqueueAndWaitAsync(
                            critical,
                            timeout,
                            startedAt,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    result = new CriticalDiagnosticWriteResult(false, false);
                }

                lock (_enqueueGate)
                {
                    _criticalWorkInFlight = false;
                    if (result.WasEnqueued)
                    {
                        _criticalWorkAcceptedForSession = true;
                    }
                    else if (!_completed &&
                             !_criticalWorkAcceptedForSession &&
                             !_stagedCriticalWork.HasValue)
                    {
                        // A timeout before capacity became available never put
                        // the record in the FIFO, so one later bounded flush may
                        // retry the same single staged record safely.
                        _stagedCriticalWork = critical with { Completion = null };
                    }
                }

                return result.CompletedSuccessfully;
            }

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var barrier = new GeneralDiagnosticWork(
                GeneralDiagnosticWorkKind.FlushBarrier,
                string.Empty,
                DateTimeOffset.UtcNow,
                Completion: completion);
            var barrierResult = await EnqueueAndWaitAsync(
                    barrier,
                    timeout,
                    startedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            return barrierResult.CompletedSuccessfully;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private async Task<CriticalDiagnosticWriteResult> EnqueueAndWaitAsync(
        GeneralDiagnosticWork work,
        TimeSpan timeout,
        long startedAt,
        CancellationToken cancellationToken)
    {
        var completion = work.Completion ?? new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        work = work with { Completion = completion };

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _completionCancellation.Token);
        var remaining = GetRemainingTimeout(timeout, startedAt);
        if (remaining <= TimeSpan.Zero ||
            !await _availableSlots
                .WaitAsync(remaining, linkedCancellation.Token)
                .ConfigureAwait(false))
        {
            return new CriticalDiagnosticWriteResult(false, false);
        }

        var enqueued = false;
        try
        {
            // Acquiring a capacity slot at the deadline must not create an
            // orphaned FIFO item that the caller no longer has budget to await.
            remaining = GetRemainingTimeout(timeout, startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                return new CriticalDiagnosticWriteResult(false, false);
            }

            linkedCancellation.Token.ThrowIfCancellationRequested();
            lock (_enqueueGate)
            {
                if (_completed)
                {
                    return new CriticalDiagnosticWriteResult(false, false);
                }

                Interlocked.Increment(ref _pendingWorkCount);
                if (!_channel.Writer.TryWrite(work))
                {
                    Interlocked.Decrement(ref _pendingWorkCount);
                    return new CriticalDiagnosticWriteResult(false, false);
                }

                enqueued = true;
            }
        }
        finally
        {
            if (!enqueued)
            {
                _availableSlots.Release();
            }
        }

        remaining = GetRemainingTimeout(timeout, startedAt);
        if (remaining <= TimeSpan.Zero)
        {
            return new CriticalDiagnosticWriteResult(true, false);
        }

        try
        {
            var completedSuccessfully = await completion.Task
                .WaitAsync(remaining, cancellationToken)
                .ConfigureAwait(false);
            return new CriticalDiagnosticWriteResult(true, completedSuccessfully);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Ownership remains with the bounded FIFO. The result tells the
            // caller not to enqueue a duplicate even though durability was not
            // observed within its deadline.
            return new CriticalDiagnosticWriteResult(true, false);
        }
    }

    private bool TryEnqueueCore(GeneralDiagnosticWork work, bool countRejectionAsDrop)
    {
        if (!_availableSlots.Wait(0))
        {
            RecordRejectedWork(countRejectionAsDrop);
            return false;
        }

        var enqueued = false;
        try
        {
            lock (_enqueueGate)
            {
                if (_completed)
                {
                    if (countRejectionAsDrop)
                    {
                        _droppedWorkCount++;
                        _droppedSinceLastAccepted++;
                    }

                    return false;
                }

                Interlocked.Increment(ref _pendingWorkCount);
                if (!_channel.Writer.TryWrite(work))
                {
                    Interlocked.Decrement(ref _pendingWorkCount);
                    if (countRejectionAsDrop)
                    {
                        _droppedWorkCount++;
                        _droppedSinceLastAccepted++;
                    }

                    return false;
                }

                enqueued = true;
                return true;
            }
        }
        finally
        {
            if (!enqueued)
            {
                _availableSlots.Release();
            }
        }
    }

    public async Task<bool> CompleteAndDrainAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        Complete();
        try
        {
            await _pumpTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Complete();
        await _pumpTask.ConfigureAwait(false);
    }

    private void Complete()
    {
        var shouldCancelWaiters = false;
        lock (_enqueueGate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _stagedCriticalWork = null;
            _channel.Writer.TryComplete();
            shouldCancelWaiters = true;
        }

        if (shouldCancelWaiters)
        {
            try
            {
                _completionCancellation.Cancel();
            }
            catch
            {
            }
        }
    }

    private async Task PumpAsync()
    {
        await foreach (var work in _channel.Reader.ReadAllAsync())
        {
            var droppedBefore = 0L;
            if (work.Kind != GeneralDiagnosticWorkKind.FlushBarrier)
            {
                lock (_enqueueGate)
                {
                    droppedBefore = _droppedSinceLastAccepted;
                    _droppedSinceLastAccepted = 0;
                }
            }

            var barrierResult = true;
            try
            {
                if (work.Kind != GeneralDiagnosticWorkKind.FlushBarrier)
                {
                    _sink(work, droppedBefore);
                }
            }
            catch
            {
                Interlocked.Increment(ref _sinkErrorCount);
                barrierResult = false;
            }
            finally
            {
                Interlocked.Decrement(ref _pendingWorkCount);
                _availableSlots.Release();
                work.Completion?.TrySetResult(barrierResult);
            }
        }
    }

    private void RecordRejectedWork(bool countRejectionAsDrop)
    {
        if (!countRejectionAsDrop)
        {
            return;
        }

        lock (_enqueueGate)
        {
            _droppedWorkCount++;
            _droppedSinceLastAccepted++;
        }
    }

    private static TimeSpan GetRemainingTimeout(TimeSpan timeout, long startedAt)
    {
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        return elapsed >= timeout ? TimeSpan.Zero : timeout - elapsed;
    }
}
