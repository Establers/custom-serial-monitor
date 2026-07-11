using System.Collections.Concurrent;
using Microsoft.UI.Dispatching;

namespace SerialMonitor.WinUI.Infrastructure;

public sealed class UiBatchDispatcher<T> : IDisposable
{
    private readonly ConcurrentQueue<T> _queue = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly Action<IReadOnlyList<T>> _onBatch;
    private readonly int _maxPendingItems;
    private readonly int _maxItemsPerTick;
    private readonly int _catchUpMaxItemsPerTick;
    private readonly int _catchUpPendingThreshold;
    private readonly bool _dropOldestWhenFull;
    private readonly SemaphoreSlim? _pendingSlots;
    private readonly List<T> _batch = new();
    private int _pendingItemCount;
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
        int catchUpPendingThreshold = 0)
    {
        _onBatch = onBatch;
        _maxPendingItems = maxPendingItems <= 0 ? 1 : maxPendingItems;
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

    public int PendingItemCount => Volatile.Read(ref _pendingItemCount);

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

        var dropped = 0;
        while (Volatile.Read(ref _pendingItemCount) >= _maxPendingItems && _queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingItemCount);
            dropped++;
        }

        _queue.Enqueue(item);
        Interlocked.Increment(ref _pendingItemCount);
        return dropped;
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
        Interlocked.Increment(ref _pendingItemCount);
    }

    public int ClearPending()
    {
        var cleared = 0;
        while (_queue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingItemCount);
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
        while (_batch.Count < batchLimit && _queue.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _pendingItemCount);
            _pendingSlots?.Release();
            _batch.Add(item);
        }

        if (_batch.Count > 0)
        {
            Interlocked.Increment(ref _flushCount);
            UpdateMaxBatchSize(_batch.Count);
            _onBatch(_batch);
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
