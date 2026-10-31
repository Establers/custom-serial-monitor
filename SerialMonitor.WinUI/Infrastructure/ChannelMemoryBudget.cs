using System.Threading.Channels;

namespace SerialMonitor.WinUI.Infrastructure;

// Admission and reader release share the same immutable-item estimator. Wrap
// the reader so every public read path (including ReadAllAsync) returns credit.
internal sealed class ChannelMemoryBudget<T>(long capacity, Func<T, long> estimate)
{
    private long _bytes;
    internal long RetainedBytes => Interlocked.Read(ref _bytes);
    internal bool TryReserve(T item)
    {
        var bytes = estimate(item);
        var current = Interlocked.Read(ref _bytes);
        while (bytes <= capacity - current)
        {
            var seen = Interlocked.CompareExchange(ref _bytes, current + bytes, current);
            if (seen == current) return true;
            current = seen;
        }
        return false;
    }
    internal void Release(T item) => Interlocked.Add(ref _bytes, -estimate(item));
    internal ChannelReader<T> Wrap(ChannelReader<T> reader) => new BudgetReader(reader, this);

    private sealed class BudgetReader(ChannelReader<T> reader, ChannelMemoryBudget<T> budget) : ChannelReader<T>
    {
        public override Task Completion => reader.Completion;
        public override bool CanCount => reader.CanCount;
        public override int Count => reader.Count;
        public override bool TryRead(out T item)
        {
            if (!reader.TryRead(out item!)) return false;
            budget.Release(item);
            return true;
        }
        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            reader.WaitToReadAsync(cancellationToken);
    }
}
