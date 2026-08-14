namespace SerialMonitor.WinUI.Infrastructure;

internal readonly record struct FatalDiagnosticWork(
    string Source,
    Exception? Exception,
    DateTimeOffset Timestamp);

/// <summary>
/// Isolates a potentially blocking fatal-error sink while allowing at most one
/// unfinished operation for the lifetime of this writer.
/// </summary>
internal sealed class BoundedEmergencyDiagnosticWriter
{
    private readonly object _gate = new();
    private readonly Action<FatalDiagnosticWork> _sink;
    private Task<bool>? _outstandingTask;
    private long _rejectedWriteCount;
    private long _sinkErrorCount;

    public BoundedEmergencyDiagnosticWriter(Action<FatalDiagnosticWork> sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public int OutstandingOperationCount
    {
        get
        {
            lock (_gate)
            {
                ReclaimCompletedTaskLocked();
                return _outstandingTask is null ? 0 : 1;
            }
        }
    }

    public long RejectedWriteCount => Interlocked.Read(ref _rejectedWriteCount);

    public long SinkErrorCount => Interlocked.Read(ref _sinkErrorCount);

    public bool TryWrite(FatalDiagnosticWork work, TimeSpan waitTimeout)
    {
        if (waitTimeout <= TimeSpan.Zero)
        {
            return false;
        }

        Task<bool> operationTask;
        try
        {
            lock (_gate)
            {
                ReclaimCompletedTaskLocked();
                if (_outstandingTask is not null)
                {
                    Interlocked.Increment(ref _rejectedWriteCount);
                    return false;
                }

                operationTask = Task.Factory.StartNew(
                    () => ExecuteSink(work),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);
                _outstandingTask = operationTask;
            }

            if (!operationTask.Wait(waitTimeout))
            {
                return false;
            }

            return operationTask.GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
        finally
        {
            lock (_gate)
            {
                if (_outstandingTask?.IsCompleted == true)
                {
                    _outstandingTask = null;
                }
            }
        }
    }

    private bool ExecuteSink(FatalDiagnosticWork work)
    {
        try
        {
            _sink(work);
            return true;
        }
        catch
        {
            Interlocked.Increment(ref _sinkErrorCount);
            return false;
        }
    }

    private void ReclaimCompletedTaskLocked()
    {
        if (_outstandingTask?.IsCompleted == true)
        {
            _outstandingTask = null;
        }
    }
}
