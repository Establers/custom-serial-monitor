namespace SerialMonitor.WinUI.Infrastructure;

internal sealed class LatestRequestAsyncOperation<T>
{
    private readonly object _gate = new();
    private readonly Func<T, CancellationToken, Task> _operation;
    private readonly Func<T, T, T>? _combinePendingRequests;
    private readonly CancellationToken _cancellationToken;
    private Task? _activeTask;
    private T? _latestRequest;
    private bool _hasPendingRequest;

    public LatestRequestAsyncOperation(
        Func<T, CancellationToken, Task> operation,
        CancellationToken cancellationToken,
        Func<T, T, T>? combinePendingRequests = null)
    {
        _operation = operation ?? throw new ArgumentNullException(nameof(operation));
        _cancellationToken = cancellationToken;
        _combinePendingRequests = combinePendingRequests;
    }

    public Task RunAsync(T request)
    {
        TaskCompletionSource<object?> completion;
        lock (_gate)
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            _latestRequest = _hasPendingRequest && _combinePendingRequests is not null
                ? _combinePendingRequests(_latestRequest!, request)
                : request;
            _hasPendingRequest = true;
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
            while (TryTakeLatestRequest(out var request))
            {
                await _operation(request, _cancellationToken);
            }

            completion.TrySetResult(null);
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
        {
            ClearPendingRequest();
            completion.TrySetResult(null);
        }
        catch (Exception ex)
        {
            ClearPendingRequest();
            completion.TrySetException(ex);
        }
    }

    private bool TryTakeLatestRequest(out T request)
    {
        lock (_gate)
        {
            if (_cancellationToken.IsCancellationRequested || !_hasPendingRequest)
            {
                _latestRequest = default;
                _hasPendingRequest = false;
                _activeTask = null;
                request = default!;
                return false;
            }

            request = _latestRequest!;
            _latestRequest = default;
            _hasPendingRequest = false;
            return true;
        }
    }

    private void ClearPendingRequest()
    {
        lock (_gate)
        {
            _latestRequest = default;
            _hasPendingRequest = false;
            _activeTask = null;
        }
    }
}
