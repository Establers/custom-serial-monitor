using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace SerialMonitor.WinUI.Infrastructure;

public readonly record struct BoundedLogBufferResult(int AcceptedCount, int DroppedCount, int CurrentCount);

public sealed class BatchedObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        RaiseReset();
    }

    public void RemoveFromStartAndAddRange(int removeCount, IReadOnlyList<T> items)
    {
        if (removeCount <= 0 && items.Count == 0)
        {
            return;
        }

        CheckReentrancy();

        removeCount = Math.Min(removeCount, Items.Count);
        for (var index = 0; index < removeCount; index++)
        {
            RemoveAt(0);
        }

        foreach (var item in items)
        {
            Add(item);
        }
    }

    public void ClearBatch()
    {
        if (Items.Count == 0)
        {
            return;
        }

        CheckReentrancy();
        Items.Clear();
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public sealed class BoundedLogBuffer<T>
{
    private readonly long _maximumBytes;
    private readonly Func<T, long>? _estimate;
    public long RetainedBytes { get; private set; }

    public BoundedLogBuffer(int capacity, long maximumBytes = 0, Func<T, long>? estimate = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        }

        Capacity = capacity;
        _maximumBytes = maximumBytes;
        _estimate = estimate;
    }

    public int Capacity { get; private set; }

    public BatchedObservableCollection<T> Items { get; } = new();

    public BoundedLogBufferResult AddRange(IReadOnlyList<T> items)
    {
        if (_maximumBytes > 0 && _estimate is not null) return AddWithinByteLimit(items);
        if (items.Count == 0)
        {
            return new BoundedLogBufferResult(0, 0, Items.Count);
        }

        if (items.Count >= Capacity)
        {
            var accepted = items.TakeLast(Capacity).ToArray();
            var dropped = Items.Count + items.Count - accepted.Length;
            Items.ReplaceAll(accepted);
            return new BoundedLogBufferResult(accepted.Length, dropped, Items.Count);
        }

        var overflow = Math.Max(0, Items.Count + items.Count - Capacity);
        Items.RemoveFromStartAndAddRange(overflow, items);
        return new BoundedLogBufferResult(items.Count, overflow, Items.Count);
    }

    public void Clear()
    {
        Items.ClearBatch();
        RetainedBytes = 0;
    }

    private BoundedLogBufferResult AddWithinByteLimit(IReadOnlyList<T> items)
    {
        var accepted = 0;
        var first = Math.Max(0, items.Count - Capacity);
        var dropped = first;
        for (var index = first; index < items.Count; index++)
        {
            var item = items[index];
            var bytes = _estimate!(item);
            if (bytes > _maximumBytes) { dropped++; continue; }
            while (Items.Count > 0 && (Items.Count >= Capacity || RetainedBytes + bytes > _maximumBytes))
            {
                RetainedBytes -= _estimate(Items[0]);
                Items.RemoveAt(0);
                dropped++;
            }
            Items.Add(item);
            RetainedBytes += bytes;
            accepted++;
        }
        return new(accepted, dropped, Items.Count);
    }

    public int SetCapacity(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        }

        if (Capacity == capacity)
        {
            return 0;
        }

        Capacity = capacity;
        var overflow = Math.Max(0, Items.Count - Capacity);
        if (overflow > 0)
        {
            if (_estimate is not null)
                for (var index = 0; index < overflow; index++) RetainedBytes -= _estimate(Items[index]);
            Items.RemoveFromStartAndAddRange(overflow, Array.Empty<T>());
        }

        return overflow;
    }
}
