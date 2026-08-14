using System.Diagnostics;

namespace SerialMonitor.WinUI.Services;

internal interface IBoundedPort : IDisposable
{
    void Open();

    void Close();
}

internal sealed class PortOperationCapacityException : InvalidOperationException
{
    public PortOperationCapacityException(int capacity)
        : base($"Port lifecycle operation limit ({capacity}) is exhausted; wait for an earlier OS operation to finish.")
    {
    }
}

/// <summary>
/// Owns synchronous port entry points on the default scheduler. The total count
/// includes blocked open/close/dispose calls. One slot is reserved so an active
/// port can always be retired; new opens use only the remaining slots.
/// </summary>
internal sealed class BoundedPortLifecycle<TPort>
    where TPort : class, IBoundedPort
{
    public static readonly TimeSpan DefaultOpenTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultCleanupTimeout = TimeSpan.FromSeconds(2);
    public const int DefaultMaximumOutstandingOperations = 4;

    private readonly object _gate = new();
    private readonly TimeSpan _openTimeout;
    private readonly TimeSpan _cleanupTimeout;
    private readonly int _maximumOutstandingOperations;
    private int _outstandingOperationCount;

    public BoundedPortLifecycle(
        TimeSpan? openTimeout = null,
        TimeSpan? cleanupTimeout = null,
        int maximumOutstandingOperations = DefaultMaximumOutstandingOperations)
    {
        _openTimeout = openTimeout ?? DefaultOpenTimeout;
        _cleanupTimeout = cleanupTimeout ?? DefaultCleanupTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_openTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_cleanupTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumOutstandingOperations, 2);
        _maximumOutstandingOperations = maximumOutstandingOperations;
    }

    public int MaximumOutstandingOperations => _maximumOutstandingOperations;

    public int OutstandingOperationCount
    {
        get
        {
            lock (_gate)
            {
                return _outstandingOperationCount;
            }
        }
    }

    public async Task<TPort> OpenAsync(
        Func<TPort> factory,
        CancellationToken cancellationToken)
    {
        return await OpenAsync(factory, beforeOpen: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TPort> OpenAsync(
        Func<TPort> factory,
        Action<TPort>? beforeOpen,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        cancellationToken.ThrowIfCancellationRequested();
        ReserveOpenOperation();

        var disposition = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedAt = Stopwatch.GetTimestamp();
        Task<TPort> openTask;
        try
        {
            openTask = Task.Factory.StartNew(
                () =>
                {
                    var port = factory();
                    try
                    {
                        beforeOpen?.Invoke(port);
                        port.Open();
                        return port;
                    }
                    catch
                    {
                        CleanupPort(port);
                        throw;
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }
        catch
        {
            ReleaseOperation();
            throw;
        }

        _ = ObserveOpenOwnershipAsync(openTask, disposition.Task);
        try
        {
            var remaining = Remaining(startedAt, _openTimeout);
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException($"Port open did not complete within {_openTimeout.TotalMilliseconds:N0} ms.");
            }

            var port = await openTask.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested || Remaining(startedAt, _openTimeout) <= TimeSpan.Zero)
            {
                throw cancellationToken.IsCancellationRequested
                    ? new OperationCanceledException(cancellationToken)
                    : new TimeoutException($"Port open did not complete within {_openTimeout.TotalMilliseconds:N0} ms.");
            }

            disposition.TrySetResult(true);
            ReleaseOperation();
            return port;
        }
        catch
        {
            disposition.TrySetResult(false);
            throw;
        }
    }

    public async Task<bool> RetireAsync(TPort port, CancellationToken cancellationToken)
    {
        return await RetireAsync(port, _cleanupTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RetireAsync(
        TPort port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        if (!TryScheduleCleanup(port, out var cleanupTask))
        {
            return false;
        }

        try
        {
            await cleanupTask.WaitAsync(
                timeout < _cleanupTimeout ? timeout : _cleanupTimeout,
                cancellationToken).ConfigureAwait(false);
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
    }

    public bool TryScheduleRetire(TPort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        return TryScheduleCleanup(port, out _);
    }

    private async Task ObserveOpenOwnershipAsync(Task<TPort> openTask, Task<bool> dispositionTask)
    {
        try
        {
            var port = await openTask.ConfigureAwait(false);
            if (!await dispositionTask.ConfigureAwait(false))
            {
                CleanupPort(port);
            }
        }
        catch
        {
            // The caller observes a prompt failure when possible. This owner
            // must also observe late faults after timeout/cancellation.
        }
        finally
        {
            if (!dispositionTask.IsCompletedSuccessfully || !dispositionTask.Result)
            {
                ReleaseOperation();
            }
        }
    }

    private bool TryScheduleCleanup(TPort port, out Task cleanupTask)
    {
        lock (_gate)
        {
            if (_outstandingOperationCount >= _maximumOutstandingOperations)
            {
                cleanupTask = Task.CompletedTask;
                return false;
            }

            _outstandingOperationCount++;
        }

        try
        {
            cleanupTask = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        CleanupPort(port);
                    }
                    finally
                    {
                        ReleaseOperation();
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
            return true;
        }
        catch
        {
            ReleaseOperation();
            throw;
        }
    }

    private void ReserveOpenOperation()
    {
        lock (_gate)
        {
            var limit = _maximumOutstandingOperations - 1;
            if (_outstandingOperationCount >= limit)
            {
                throw new PortOperationCapacityException(_maximumOutstandingOperations);
            }

            _outstandingOperationCount++;
        }
    }

    private void ReleaseOperation()
    {
        lock (_gate)
        {
            if (_outstandingOperationCount > 0)
            {
                _outstandingOperationCount--;
            }
        }
    }

    private static void CleanupPort(TPort port)
    {
        try
        {
            port.Close();
        }
        catch
        {
        }

        try
        {
            port.Dispose();
        }
        catch
        {
        }
    }

    private static TimeSpan Remaining(long startedAt, TimeSpan timeout)
    {
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        return timeout - elapsed;
    }
}
