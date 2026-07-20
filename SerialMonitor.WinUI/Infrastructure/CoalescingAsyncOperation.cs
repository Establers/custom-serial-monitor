namespace SerialMonitor.WinUI.Infrastructure;

internal sealed class CoalescingAsyncOperation
{
    private readonly object _gate = new();
    private readonly Func<Task> _operation;
    private Task? _activeTask;
    private bool _runAgain;

    public CoalescingAsyncOperation(Func<Task> operation)
    {
        _operation = operation ?? throw new ArgumentNullException(nameof(operation));
    }

    public Task RunAsync()
    {
        TaskCompletionSource<object?> completion;
        lock (_gate)
        {
            _runAgain = true;
            if (_activeTask is not null)
            {
                return _activeTask;
            }

            completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeTask = completion.Task;
        }

        _ = RunLoopAsync(completion);
        return completion.Task;
    }

    private async Task RunLoopAsync(TaskCompletionSource<object?> completion)
    {
        try
        {
            while (true)
            {
                lock (_gate)
                {
                    _runAgain = false;
                }

                await _operation().ConfigureAwait(false);

                lock (_gate)
                {
                    if (_runAgain)
                    {
                        continue;
                    }

                    _activeTask = null;
                    break;
                }
            }

            completion.TrySetResult(null);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _activeTask = null;
                _runAgain = false;
            }

            completion.TrySetException(ex);
        }
    }
}
