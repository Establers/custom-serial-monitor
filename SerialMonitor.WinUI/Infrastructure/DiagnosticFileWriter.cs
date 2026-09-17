namespace SerialMonitor.WinUI.Infrastructure;

internal enum DiagnosticFile { Error, Startup, Shutdown }

// Only the latest pending value for each of the three diagnostic files is kept.
// A stalled disk cannot grow the queue or hold a caller/UI lock.
internal sealed class DiagnosticFileWriter(Func<DiagnosticFile, string?, Task> write)
{
    private readonly object _gate = new();
    private readonly Dictionary<DiagnosticFile, string?> _pending = new();
    private TaskCompletionSource? _idle;

    public void Enqueue(DiagnosticFile file, string? text)
    {
        lock (_gate)
        {
            _pending[file] = text;
            if (_idle is not null) return;
            _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(DrainAsync);
        }
    }

    public async Task FlushAsync(TimeSpan timeout)
    {
        Task idle;
        lock (_gate) idle = _idle?.Task ?? Task.CompletedTask;
        try { await idle.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { /* Diagnostics must not prevent shutdown. */ }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            KeyValuePair<DiagnosticFile, string?> next;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _idle!.TrySetResult();
                    _idle = null;
                    return;
                }
                next = _pending.First();
                _pending.Remove(next.Key);
            }

            try { await write(next.Key, next.Value).ConfigureAwait(false); }
            catch (Exception) { /* Best effort; never recursively log a diagnostic failure. */ }
        }
    }
}
