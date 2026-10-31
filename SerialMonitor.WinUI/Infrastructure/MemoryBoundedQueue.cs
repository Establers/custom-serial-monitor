namespace SerialMonitor.WinUI.Infrastructure;

// Admission and eviction share one lock, including byte accounting. A stalled
// UI cannot retain an arbitrary number of large records through a count limit.
internal sealed class MemoryBoundedQueue<T>(int capacity, long maxBytes, Func<T, long> retainedBytes)
{
    private readonly object _gate = new();
    private readonly Queue<(T Item, long Bytes)> _items = new();
    private long _bytes;
    public int Count { get { lock (_gate) return _items.Count; } }
    public bool IsEmpty => Count == 0;
    internal long RetainedBytes { get { lock (_gate) return _bytes; } }

    public int EnqueueDroppingOldest(T item)
    {
        var bytes = Math.Max(0, retainedBytes(item));
        lock (_gate)
        {
            if (maxBytes > 0 && bytes > maxBytes) return 1;
            var dropped = 0;
            while (_items.Count >= capacity || (maxBytes > 0 && _bytes + bytes > maxBytes))
            {
                var removed = _items.Dequeue();
                _bytes -= removed.Bytes;
                dropped++;
            }
            _items.Enqueue((item, bytes));
            _bytes += bytes;
            return dropped;
        }
    }

    public void Enqueue(T item)
    {
        var bytes = Math.Max(0, retainedBytes(item));
        lock (_gate) { _items.Enqueue((item, bytes)); _bytes += bytes; }
    }

    public bool TryDequeue(out T item)
    {
        lock (_gate)
        {
            if (!_items.TryDequeue(out var entry)) { item = default!; return false; }
            _bytes -= entry.Bytes;
            item = entry.Item;
            return true;
        }
    }
}
