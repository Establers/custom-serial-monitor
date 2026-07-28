namespace SerialMonitor.WinUI.Infrastructure;

internal sealed class XtermClearBarrier
{
    private long _pendingGeneration;

    public bool IsPending => Volatile.Read(ref _pendingGeneration) != 0;

    public void Begin(long generation)
    {
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        Volatile.Write(ref _pendingGeneration, generation);
    }

    public bool TryComplete(long generation)
    {
        return generation > 0 &&
            Interlocked.CompareExchange(ref _pendingGeneration, 0, generation) == generation;
    }

    public bool ShouldStartPump(bool pumpRunning, bool recoveryPending, int queuedBatchCount)
    {
        return !IsPending &&
            !pumpRunning &&
            !recoveryPending &&
            queuedBatchCount > 0;
    }
}
