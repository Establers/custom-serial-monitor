using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialBusActivityTimelineTests
{
    [Fact]
    public void AddBusy_LateIntervalFillsEarlierGapsAndCountsExistingOverlapOnlyOnce()
    {
        var timeline = new SerialBusActivityTimeline(0d);
        timeline.AddBusy(0.0001d, 0.00012d, 0d);
        timeline.AddBusy(0.00018d, 0.0002d, 0d);
        timeline.AddBusy(0.000085d, 0.00019d, 0d);
        timeline.AddBusy(0.000085d, 0.00019d, 0d);

        Assert.Equal(0.000115d, timeline.Read(0d, 0.001d, 0.001d).BusySeconds, 9);
        Assert.Equal(0.00005d, timeline.Read(0.00011d, 0.00016d, 0.001d).BusySeconds, 9);
    }

    [Fact]
    public void TrimBefore_RemovesExpiredSpansAndPreservesThePartCrossingTheWindow()
    {
        var timeline = new SerialBusActivityTimeline(0d);
        timeline.AddBusy(0d, 0.1d, 0d);
        timeline.AddBusy(0.2d, 0.3d, 0d);
        timeline.TrimBefore(0.25d);
        timeline.AddBusy(0.22d, 0.35d, 0.25d);

        Assert.Equal(0.1d, timeline.Read(0.25d, 1d, 1d).BusySeconds, 9);
    }

    [Theory]
    [InlineData(0.00015d)]
    [InlineData(0.0003d)]
    public void AddBytes_LateOldestPartialBucketMatchesHistoryCollectedBeforeTheWindowMoved(double wireEnd)
    {
        var collectedEarlier = new SerialBusActivityTimeline(0d);
        collectedEarlier.AddBytes(0.0001d, wireEnd, 2d, 0d);
        var collectedLate = new SerialBusActivityTimeline(0d);
        collectedLate.AddBytes(0.0001d, wireEnd, 2d, 0.0002d);

        var expected = collectedEarlier.Read(0.0002d, 60.0002d, 60.0002d);
        var actual = collectedLate.Read(0.0002d, 60.0002d, 60.0002d);

        Assert.Equal(1.6d, expected.ReceivedBytes, 6);
        Assert.Equal(expected.ReceivedBytes, actual.ReceivedBytes, 6);
    }

    [Fact]
    public void AddBusy_HistorySaturationIsBoundedConservativeAndReported()
    {
        var timeline = new SerialBusActivityTimeline(0d);
        var count = SerialBusActivityTimeline.MaximumBusyIntervals + 100;
        for (var index = 0; index < count; index++)
        {
            timeline.AddBusy(index * 0.0001d, index * 0.0001d + 0.00002d, 0d);
        }

        var totals = timeline.Read(0d, 10d, 10d);
        Assert.True(double.IsFinite(timeline.LastCoalescedSeconds));
        Assert.InRange(totals.BusySeconds, count * 0.00002d, 10d);
        timeline.TrimBefore(11d);
        Assert.Equal(0d, timeline.Read(11d, 12d, 12d).BusySeconds);
        timeline.AddBusy(11d, 11.25d, 11d);
        Assert.Equal(0.25d, timeline.Read(11d, 12d, 12d).BusySeconds, 9);
    }
}
