namespace SerialMonitor.WinUI.Infrastructure;

// Soft limit: always admit the current item, then stop if the budget is spent.
// This keeps oversized log segments progressing without splitting RX packets.
internal struct UiBatchCostBudget(long limit)
{
    private long _remaining = limit > 0 ? limit : long.MaxValue;
    public bool HasRoom => _remaining > 0;

    public void Add(long cost)
    {
        _remaining -= Math.Min(_remaining, Math.Max(1, cost));
    }
}
