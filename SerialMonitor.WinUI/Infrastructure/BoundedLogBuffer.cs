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
    public BoundedLogBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        }

        Capacity = capacity;
    }

    public int Capacity { get; private set; }

    public BatchedObservableCollection<T> Items { get; } = new();

    public BoundedLogBufferResult AddRange(IReadOnlyList<T> items)
    {
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
            Items.RemoveFromStartAndAddRange(overflow, Array.Empty<T>());
        }

        return overflow;
    }
}
