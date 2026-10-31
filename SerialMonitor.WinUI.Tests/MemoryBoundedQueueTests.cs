using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class MemoryBoundedQueueTests
{
    [Fact]
    public void AStalledUiKeepsTheNewestRecordsWithinBothLimits()
    {
        var queue = new MemoryBoundedQueue<byte[]>(1000, 100, item => item.Length);
        var first = new byte[40]; var second = new byte[40]; var third = new byte[40];
        Assert.Equal(0, queue.EnqueueDroppingOldest(first));
        Assert.Equal(0, queue.EnqueueDroppingOldest(second));
        Assert.Equal(1, queue.EnqueueDroppingOldest(third));
        Assert.Equal(80, queue.RetainedBytes);
        Assert.Equal(1, queue.EnqueueDroppingOldest(new byte[101]));
        Assert.Equal(2, queue.Count);
        Assert.True(queue.TryDequeue(out var item)); Assert.Same(second, item);
        Assert.True(queue.TryDequeue(out item)); Assert.Same(third, item);
        Assert.Equal(0, queue.RetainedBytes);
        var countBound = new MemoryBoundedQueue<int>(2, 0, _ => 1);
        Assert.Equal(0, countBound.EnqueueDroppingOldest(1));
        Assert.Equal(0, countBound.EnqueueDroppingOldest(2));
        Assert.Equal(1, countBound.EnqueueDroppingOldest(3));
        Assert.Equal(2, countBound.Count);
    }

    [Fact]
    public async Task ConcurrentAdmissionAndConsumptionCannotEscapeTheByteBudget()
    {
        var queue = new MemoryBoundedQueue<byte[]>(100, 4096, item => item.Length);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                queue.EnqueueDroppingOldest(new byte[128]);
                Assert.InRange(queue.RetainedBytes, 0, 4096);
                if ((i & 1) == 0) queue.TryDequeue(out _);
            }
        })));
        while (queue.TryDequeue(out _)) { }
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.RetainedBytes);
    }
}
