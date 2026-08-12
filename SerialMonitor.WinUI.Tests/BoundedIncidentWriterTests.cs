using System.Collections.Concurrent;
using System.Diagnostics;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class BoundedIncidentWriterTests
{
    [Fact]
    public async Task BlockingSink_KeepsPendingWorkBoundedAndEnqueueNonBlocking()
    {
        using var releaseSink = new ManualResetEventSlim(initialState: false);
        var firstSinkStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sinkEntries = new ConcurrentQueue<string>();
        var sinkCallCount = 0;
        await using var writer = new BoundedIncidentWriter(
            capacity: 4,
            entry =>
            {
                sinkEntries.Enqueue(entry);
                if (Interlocked.Increment(ref sinkCallCount) == 1)
                {
                    firstSinkStarted.TrySetResult(true);
                    releaseSink.Wait();
                }
            });

        Assert.True(writer.TryEnqueue("blocking first incident"));
        await firstSinkStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var enqueueStopwatch = Stopwatch.StartNew();
        for (var index = 0; index < 10_000; index++)
        {
            writer.TryEnqueue($"incident {index}");
        }

        enqueueStopwatch.Stop();
        Assert.True(enqueueStopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.InRange(writer.PendingWorkCount, 1, writer.Capacity);
        Assert.True(writer.DroppedIncidentCount > 0);

        releaseSink.Set();
        await WaitUntilAsync(() => writer.PendingWorkCount == 0, TimeSpan.FromSeconds(2));
        Assert.True(writer.TryEnqueue("incident after overflow"));
        await WaitUntilAsync(
            () => sinkEntries.Any(entry => entry.Contains("Dropped", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task SinkException_DoesNotStopPumpFromProcessingNextIncident()
    {
        var successfulEntry = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sinkCallCount = 0;
        await using var writer = new BoundedIncidentWriter(
            capacity: 4,
            entry =>
            {
                if (Interlocked.Increment(ref sinkCallCount) == 1)
                {
                    throw new IOException("Injected incident sink failure.");
                }

                successfulEntry.TrySetResult(entry);
            });

        Assert.True(writer.TryEnqueue("fails in sink"));
        Assert.True(writer.TryEnqueue("survives sink failure"));

        var processed = await successfulEntry.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("survives sink failure", processed);
        Assert.Equal(1, writer.SinkErrorCount);
    }

    [Fact]
    public async Task CompleteAndDrain_WritesLastQueuedIncidentAndRejectsLaterEnqueue()
    {
        var sinkEntries = new ConcurrentQueue<string>();
        await using var writer = new BoundedIncidentWriter(capacity: 4, sinkEntries.Enqueue);

        Assert.True(writer.TryEnqueue("first incident"));
        Assert.True(writer.TryEnqueue("last queued incident"));

        Assert.True(await writer.CompleteAndDrainAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(["first incident", "last queued incident"], sinkEntries);
        Assert.Equal(0, writer.PendingWorkCount);

        var droppedBeforeCompletedEnqueue = writer.DroppedIncidentCount;
        Assert.False(writer.TryEnqueue("after completion"));
        Assert.Equal(droppedBeforeCompletedEnqueue + 1, writer.DroppedIncidentCount);
        Assert.True(await writer.CompleteAndDrainAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task CompleteAndDrain_BlockingSinkTimesOutAndCanFinishAfterRelease()
    {
        using var sinkStarted = new ManualResetEventSlim(initialState: false);
        using var releaseSink = new ManualResetEventSlim(initialState: false);
        await using var writer = new BoundedIncidentWriter(
            capacity: 4,
            _ =>
            {
                sinkStarted.Set();
                releaseSink.Wait();
            });

        Assert.True(writer.TryEnqueue("blocking final incident"));
        Assert.True(sinkStarted.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            Assert.False(await writer.CompleteAndDrainAsync(TimeSpan.FromMilliseconds(50)));
            Assert.False(writer.TryEnqueue("rejected after bounded shutdown"));
            Assert.Equal(1, writer.PendingWorkCount);
        }
        finally
        {
            releaseSink.Set();
        }

        Assert.True(await writer.CompleteAndDrainAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, writer.PendingWorkCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not satisfied before the test timeout.");
            }

            await Task.Delay(10);
        }
    }
}
