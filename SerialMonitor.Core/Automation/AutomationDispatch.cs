namespace SerialMonitor.Core.Automation;

// Cancel only work that has not started on the UI thread. Once an operation
// begins, its result must be observed: reserve/send may already have committed.
public static class AutomationDispatch
{
    public static async Task<object> InvokeAsync(bool hasThreadAccess, Func<Action, bool> enqueue,
        Func<Task<object>> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = 0; // 0 queued, 1 started, 2 canceled/rejected
        using var registration = token.Register(() =>
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0) completion.TrySetCanceled(token);
        });
        async void Run()
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
            try { completion.TrySetResult(await operation()); }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
        if (hasThreadAccess) Run();
        else if (!enqueue(Run) && Interlocked.CompareExchange(ref state, 2, 0) == 0)
            completion.TrySetException(new AutomationException("gui_unavailable", "GUI dispatcher is unavailable."));
        return await completion.Task.ConfigureAwait(false);
    }
}
