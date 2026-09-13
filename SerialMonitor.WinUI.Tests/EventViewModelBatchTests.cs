using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class EventViewModelBatchTests
{
    [Theory]
    [InlineData(250)]
    [InlineData(500)]
    [InlineData(750)]
    public void Burst_NotifiesOnceAfterEvictionsAndCountersAreApplied(int batchSize)
    {
        var viewModel = new EventViewModel(500);
        viewModel.AddRange(CreateEvents(500));
        var batch = CreateEvents(batchSize);
        var notifications = 0;
        viewModel.BatchApplied += (_, _) =>
        {
            notifications++;
            Assert.Equal(500, viewModel.CurrentVisibleEventCount);
            Assert.Equal(500 + Math.Min(batchSize, 500), viewModel.DisplayedEventCount);
            Assert.Equal(batchSize, viewModel.DroppedVisibleEventCount);
            Assert.Same(batch[^1], viewModel.Events[^1]);
        };

        viewModel.AddRange(batch);

        Assert.Equal(1, notifications);
        Assert.Equal(batch.TakeLast(500), viewModel.Events.TakeLast(Math.Min(batchSize, 500)));
    }

    [Fact]
    public void EmptyBatchClearAndCapacityChange_DoNotRequestAutoScroll()
    {
        var viewModel = new EventViewModel(500);
        viewModel.AddRange(CreateEvents(500));
        var notifications = 0;
        viewModel.BatchApplied += (_, _) => notifications++;

        viewModel.AddRange(Array.Empty<DetectedEvent>());
        viewModel.SetCapacity(100);
        viewModel.Clear();

        Assert.Equal(0, notifications);
    }

    private static DetectedEvent[] CreateEvents(int count) => Enumerable.Range(0, count)
        .Select(index => new DetectedEvent(DateTimeOffset.UtcNow, "burst", "event",
            LogDirection.Rx, $"event {index}", buildMessageSegments: false))
        .ToArray();

    [Fact]
    public void SustainedBursts_KeepBoundedOrderedHistoryWithoutResets()
    {
        const int capacity = 500;
        const int batchSize = 250;
        const int batchCount = 400;
        var viewModel = new EventViewModel(capacity);
        var notifications = 0;
        var resets = 0;
        viewModel.BatchApplied += (_, _) => notifications++;
        viewModel.Events.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                resets++;
            }
        };

        DetectedEvent[] previousBatch = [];
        for (var index = 0; index < batchCount; index++)
        {
            var batch = CreateEvents(batchSize);
            viewModel.AddRange(batch);

            Assert.Equal(previousBatch.Concat(batch), viewModel.Events);
            Assert.InRange(viewModel.CurrentVisibleEventCount, 0, capacity);
            Assert.Equal(index + 1, notifications);
            previousBatch = batch;
        }

        Assert.Equal(0, resets);
        Assert.Equal(batchCount * batchSize, viewModel.DisplayedEventCount);
        Assert.Equal(batchCount * batchSize - capacity, viewModel.DroppedVisibleEventCount);
    }

    [Fact]
    public void RemovedSubscriber_IsNotCalledByLaterBatches()
    {
        var viewModel = new EventViewModel(500);
        var notifications = 0;
        EventHandler handler = (_, _) => notifications++;
        viewModel.BatchApplied += handler;
        viewModel.AddRange(CreateEvents(250));
        viewModel.BatchApplied -= handler;

        viewModel.AddRange(CreateEvents(250));

        Assert.Equal(1, notifications);
        Assert.Equal(500, viewModel.CurrentVisibleEventCount);
    }
}
