namespace SerialMonitor.WinUI.Infrastructure;

internal sealed class FileLogAutoRestartCoordinator : IAsyncDisposable
{
    internal const int DefaultMaximumConsecutiveAttempts = 12;
    internal static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan DefaultMaximumDelay = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Func<bool> _shouldRetry;
    private readonly Func<CancellationToken, Task<bool>> _restart;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maximumDelay;
    private readonly int _maximumConsecutiveAttempts;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private int _attemptCount;
    private int _loopStartCount;
    private bool _isRetrying;
    private bool _isExhausted;
    private bool _rerunRequested;
    private int _cancelRequestCount;
    private bool _disposed;

    public FileLogAutoRestartCoordinator(
        Func<bool> shouldRetry,
        Func<CancellationToken, Task<bool>> restart,
        TimeSpan? initialDelay = null,
        TimeSpan? maximumDelay = null,
        int maximumConsecutiveAttempts = DefaultMaximumConsecutiveAttempts,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(shouldRetry);
        ArgumentNullException.ThrowIfNull(restart);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConsecutiveAttempts, 1);

        _shouldRetry = shouldRetry;
        _restart = restart;
        _initialDelay = EnsurePositive(initialDelay ?? DefaultInitialDelay, nameof(initialDelay));
        _maximumDelay = EnsurePositive(maximumDelay ?? DefaultMaximumDelay, nameof(maximumDelay));
        if (_maximumDelay < _initialDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDelay),
                _maximumDelay,
                "The maximum delay must not be shorter than the initial delay.");
        }

        _maximumConsecutiveAttempts = maximumConsecutiveAttempts;
        _delay = delay ?? ((value, token) => Task.Delay(value, token));
    }

    public event EventHandler? StatusChanged;

    public bool IsRetrying
    {
        get
        {
            lock (_gate)
            {
                return _isRetrying;
            }
        }
    }

    public bool IsExhausted
    {
        get
        {
            lock (_gate)
            {
                return _isExhausted;
            }
        }
    }

    public int AttemptCount
    {
        get
        {
            lock (_gate)
            {
                return _attemptCount;
            }
        }
    }

    internal int LoopStartCount
    {
        get
        {
            lock (_gate)
            {
                return _loopStartCount;
            }
        }
    }

    public bool RequestRetry()
    {
        if (!ShouldRetry())
        {
            return false;
        }

        var exhausted = false;
        lock (_gate)
        {
            if (_disposed || _cancelRequestCount != 0 || _isExhausted)
            {
                return false;
            }

            if (_runTask is not null && !_runTask.IsCompleted)
            {
                _rerunRequested = true;
                return false;
            }

            if (_attemptCount >= _maximumConsecutiveAttempts)
            {
                _isExhausted = true;
                exhausted = true;
            }
            else
            {
                var cancellation = new CancellationTokenSource();
                _runCancellation = cancellation;
                _isRetrying = true;
                _loopStartCount++;
                _runTask = Task.Run(() => RunAsync(cancellation), CancellationToken.None);
            }
        }

        RaiseStatusChanged();
        return !exhausted;
    }

    public void MarkDurableProgress()
    {
        var changed = false;
        lock (_gate)
        {
            if (_attemptCount != 0 || _isExhausted)
            {
                _attemptCount = 0;
                _isExhausted = false;
                changed = true;
            }
        }

        if (changed)
        {
            RaiseStatusChanged();
        }
    }

    public Task CancelAsync(bool resetAttempts = true) => CancelCoreAsync(resetAttempts);

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await CancelCoreAsync(resetAttempts: true).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationTokenSource runCancellation)
    {
        try
        {
            while (ShouldRetry())
            {
                int attempt;
                TimeSpan delay;
                lock (_gate)
                {
                    if (_attemptCount >= _maximumConsecutiveAttempts)
                    {
                        _isExhausted = true;
                        break;
                    }

                    attempt = ++_attemptCount;
                    delay = GetDelay(attempt);
                }

                RaiseStatusChanged();
                await _delay(delay, runCancellation.Token).ConfigureAwait(false);
                runCancellation.Token.ThrowIfCancellationRequested();
                if (!ShouldRetry())
                {
                    break;
                }

                if (await _restart(runCancellation.Token).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            var rerunRequested = false;
            lock (_gate)
            {
                if (ReferenceEquals(_runCancellation, runCancellation))
                {
                    _runCancellation = null;
                    _runTask = null;
                    _isRetrying = false;
                    if (_attemptCount >= _maximumConsecutiveAttempts && !_isExhausted)
                    {
                        _isExhausted = true;
                    }

                    rerunRequested = _rerunRequested;
                    _rerunRequested = false;
                }
            }

            runCancellation.Dispose();
            RaiseStatusChanged();
            if (rerunRequested && ShouldRetry())
            {
                RequestRetry();
            }
        }
    }

    private async Task CancelCoreAsync(bool resetAttempts)
    {
        Task? runTask;
        lock (_gate)
        {
            _cancelRequestCount++;
            _rerunRequested = false;
            _runCancellation?.Cancel();
            runTask = _runTask;
        }

        if (runTask is not null)
        {
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                _ = runTask.Exception;
            }
        }

        lock (_gate)
        {
            if (ReferenceEquals(_runTask, runTask))
            {
                _runTask = null;
            }

            if (_runTask is null)
            {
                _isRetrying = false;
            }

            _rerunRequested = false;
            if (resetAttempts)
            {
                _attemptCount = 0;
                _isExhausted = false;
            }

            _cancelRequestCount--;
        }

        RaiseStatusChanged();
    }

    private bool ShouldRetry()
    {
        try
        {
            return _shouldRetry();
        }
        catch
        {
            return false;
        }
    }

    private TimeSpan GetDelay(int attempt)
    {
        var multiplier = Math.Pow(2, Math.Min(attempt - 1, 30));
        var ticks = Math.Min(_maximumDelay.Ticks, _initialDelay.Ticks * multiplier);
        return TimeSpan.FromTicks((long)ticks);
    }

    private void RaiseStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
        }
    }

    private static TimeSpan EnsurePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The delay must be positive.");
        }

        return value;
    }
}
