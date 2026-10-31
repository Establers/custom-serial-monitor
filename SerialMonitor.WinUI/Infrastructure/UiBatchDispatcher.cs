using Microsoft.UI.Dispatching;

namespace SerialMonitor.WinUI.Infrastructure;

public sealed class UiBatchDispatcher<T> : IDisposable
{
    private readonly MemoryBoundedQueue<T> _queue;
    private readonly DispatcherQueueTimer _timer;
    private readonly Action<IReadOnlyList<T>> _onBatch;
    private readonly int _maxPendingItems;
    private readonly int _maxItemsPerTick;
    private readonly int _catchUpMaxItemsPerTick;
    private readonly int _catchUpPendingThreshold;
    private readonly bool _dropOldestWhenFull;
    private readonly long _maxBatchCost;
    private readonly Func<T, long>? _getItemCost;
    private readonly SemaphoreSlim? _pendingSlots;
    private readonly List<T> _batch = new();
    private int _isPaused;
    private long _flushCount;
    private int _maxBatchSize;
    private bool _disposed;

    public UiBatchDispatcher(
        DispatcherQueue dispatcherQueue,
        TimeSpan interval,
        Action<IReadOnlyList<T>> onBatch,
        int maxPendingItems = 10_000,
        int maxItemsPerTick = 0,
        bool dropOldestWhenFull = true,
        int catchUpMaxItemsPerTick = 0,
        int catchUpPendingThreshold = 0,
        long maxBatchCost = 0,
        Func<T, long>? getItemCost = null,
        long maxPendingBytes = 0,
        Func<T, long>? getRetainedBytes = null)
    {
        _onBatch = onBatch;
        _maxBatchCost = maxBatchCost;
        _getItemCost = getItemCost;
        _maxPendingItems = maxPendingItems <= 0 ? 1 : maxPendingItems;
        _queue = new MemoryBoundedQueue<T>(_maxPendingItems, maxPendingBytes, getRetainedBytes ?? (_ => 0));
        _maxItemsPerTick = maxItemsPerTick <= 0 ? int.MaxValue : maxItemsPerTick;
        _catchUpMaxItemsPerTick = catchUpMaxItemsPerTick <= 0
            ? _maxItemsPerTick
            : Math.Max(_maxItemsPerTick, catchUpMaxItemsPerTick);
        _catchUpPendingThreshold = catchUpPendingThreshold <= 0
            ? int.MaxValue
            : catchUpPendingThreshold;
        _dropOldestWhenFull = dropOldestWhenFull;
        if (!_dropOldestWhenFull)
        {
            _pendingSlots = new SemaphoreSlim(_maxPendingItems, _maxPendingItems);
        }
        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = interval;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public bool IsPaused
    {
        get => Volatile.Read(ref _isPaused) != 0;
        set => Volatile.Write(ref _isPaused, value ? 1 : 0);
    }

    public int PendingItemCount => _queue.Count;

    public int MaxItemsPerTick => _maxItemsPerTick == int.MaxValue ? 0 : _maxItemsPerTick;

    public long FlushCount => Interlocked.Read(ref _flushCount);

    public int MaxBatchSize => Volatile.Read(ref _maxBatchSize);

    public int Post(T item)
    {
        if (Volatile.Read(ref _disposed))
        {
            return 1;
        }

        if (!_dropOldestWhenFull)
        {
            throw new InvalidOperationException("Use PostAsync for a lossless UI dispatcher.");
        }

        return _queue.EnqueueDroppingOldest(item);
    }

    public async ValueTask PostAsync(T item, CancellationToken cancellationToken = default)
    {
        if (_dropOldestWhenFull)
        {
            Post(item);
            return;
        }

        var pendingSlots = _pendingSlots
            ?? throw new InvalidOperationException("Lossless dispatcher slots are not initialized.");
        await pendingSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref _disposed))
        {
            pendingSlots.Release();
            throw new ObjectDisposedException(nameof(UiBatchDispatcher<T>));
        }

        _queue.Enqueue(item);
    }

    public int ClearPending()
    {
        var cleared = 0;
        while (_queue.TryDequeue(out _))
        {
            _pendingSlots?.Release();
            cleared++;
        }

        return cleared;
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        Volatile.Write(ref _disposed, true);
        _timer.Stop();
        _timer.Tick -= OnTick;
        ClearPending();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (Volatile.Read(ref _disposed) || IsPaused || _queue.IsEmpty)
        {
            return;
        }

        _batch.Clear();
        var pendingCount = PendingItemCount;
        var batchLimit = pendingCount >= _catchUpPendingThreshold
            ? _catchUpMaxItemsPerTick
            : _maxItemsPerTick;
        var budget = new UiBatchCostBudget(_maxBatchCost);
        while (_batch.Count < batchLimit && budget.HasRoom && _queue.TryDequeue(out var item))
        {
            _pendingSlots?.Release();
            _batch.Add(item);
            budget.Add(_getItemCost?.Invoke(item) ?? 1);
        }

        if (_batch.Count > 0)
        {
            Interlocked.Increment(ref _flushCount);
            UpdateMaxBatchSize(_batch.Count);
            try { _onBatch(_batch); }
            finally { _batch.Clear(); }
        }
    }

    private void UpdateMaxBatchSize(int candidate)
    {
        var current = Volatile.Read(ref _maxBatchSize);
        while (candidate > current)
        {
            var previous = Interlocked.CompareExchange(ref _maxBatchSize, candidate, current);
            if (previous == current)
            {
                break;
            }

            current = previous;
        }
    }
}
