namespace SerialMonitor.WinUI.Services;

// A run owns one control. Pausing never interrupts an in-flight TX or its delay.
public sealed class SequenceExecutionControl
{
    private readonly object _gate = new();
    private TaskCompletionSource _changed = NewSignal();
    private bool _pauseRequested;
    private bool _isPaused;

    public event Action? StateChanged;

    public bool IsPauseRequested
    {
        get { lock (_gate) return _pauseRequested; }
    }

    public bool IsPaused
    {
        get { lock (_gate) return _isPaused; }
    }

    public void Pause()
    {
        bool notify;
        lock (_gate)
        {
            notify = !_pauseRequested;
            _pauseRequested = true;
        }
        if (notify) StateChanged?.Invoke();
    }

    public void Resume()
    {
        bool notify;
        lock (_gate)
        {
            notify = _pauseRequested || _isPaused;
            _pauseRequested = false;
            _isPaused = false;
            Pulse();
        }
        if (notify) StateChanged?.Invoke();
    }

    internal Task WaitAtStepBoundaryAsync(CancellationToken token) => WaitAsync(token);

    internal Task<bool> WaitBeforeSendAsync(CancellationToken token) => WaitAsync(token);

    internal void EndRun()
    {
        bool notify;
        lock (_gate)
        {
            notify = _isPaused;
            _isPaused = false;
        }
        if (notify) StateChanged?.Invoke();
    }

    private async Task<bool> WaitAsync(CancellationToken token)
    {
        var waited = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task? wait;
            bool notify;
            lock (_gate)
            {
                notify = _isPaused != _pauseRequested;
                _isPaused = _pauseRequested;
                wait = _pauseRequested ? _changed.Task : null;
            }
            if (notify) StateChanged?.Invoke();
            if (wait is null) return waited;
            waited = true;
            await wait.WaitAsync(token);
        }
    }

    // Called under _gate. Continuations always run outside that lock.
    private void Pulse()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
