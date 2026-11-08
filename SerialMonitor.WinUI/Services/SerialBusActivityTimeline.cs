namespace SerialMonitor.WinUI.Services;

// Busy spans retain their exact union, including overlaps delivered after a
// previous UI sample. Byte counts use 1 ms buckets with fractional durations.
// Both histories are bounded independently of RX rate and session length.
internal sealed class SerialBusActivityTimeline(double originSeconds)
{
    internal const int MaximumBusyIntervals = 65_536;
    private const int BucketsPerSecond = 1_000;
    private const int Capacity = (int)SerialBusUtilizationMeter.WindowSeconds * BucketsPerSecond + 2;
    private readonly Bucket[] _buckets = new Bucket[Capacity];
    private readonly List<BusyInterval> _busyIntervals = new(64);

    public double LastCoalescedSeconds { get; private set; } = double.NegativeInfinity;

    public void TrimBefore(double windowStart)
    {
        var removeCount = FindFirstInterval(windowStart);
        if (removeCount < _busyIntervals.Count && _busyIntervals[removeCount].EndSeconds == windowStart)
        {
            removeCount++;
        }
        _busyIntervals.RemoveRange(0, removeCount);
    }

    public void AddBusy(double start, double end, double windowStart)
    {
        start = Math.Max(start, windowStart);
        if (end <= start)
        {
            return;
        }

        var first = FindFirstInterval(start);
        var after = first;
        while (after < _busyIntervals.Count && _busyIntervals[after].StartSeconds <= end)
        {
            start = Math.Min(start, _busyIntervals[after].StartSeconds);
            end = Math.Max(end, _busyIntervals[after].EndSeconds);
            after++;
        }

        var merged = new BusyInterval(start, end);
        if (after > first)
        {
            _busyIntervals[first] = merged;
            _busyIntervals.RemoveRange(first + 1, after - first - 1);
        }
        else
        {
            _busyIntervals.Insert(first, merged);
        }

        if (_busyIntervals.Count > MaximumBusyIntervals)
        {
            // At extreme sustained burst rates, retain a conservative envelope
            // for the oldest quarter. Freeing a batch avoids an O(capacity)
            // shift for every subsequent RX span while keeping memory bounded.
            var mergeCount = _busyIntervals.Count / 4;
            var mergedEnd = _busyIntervals[mergeCount - 1].EndSeconds;
            _busyIntervals[0] = new BusyInterval(_busyIntervals[0].StartSeconds, mergedEnd);
            _busyIntervals.RemoveRange(1, mergeCount - 1);
            LastCoalescedSeconds = Math.Max(LastCoalescedSeconds, mergedEnd);
        }
    }

    public (double BusySeconds, double ReceivedBytes) Read(double start, double end, double observedThrough)
    {
        var busySeconds = 0d;
        var receivedBytes = 0d;
        for (var index = FindFirstInterval(start);
            index < _busyIntervals.Count && _busyIntervals[index].StartSeconds < end;
            index++)
        {
            var interval = _busyIntervals[index];
            busySeconds += Math.Max(0d, Math.Min(end, interval.EndSeconds) - Math.Max(start, interval.StartSeconds));
        }
        var relativeStart = start - originSeconds;
        var relativeEnd = end - originSeconds;
        var relativeObservedThrough = observedThrough - originSeconds;
        var first = IndexAtRelative(relativeStart);
        var last = IndexAtRelative(relativeEnd);
        for (var index = first; index <= last; index++)
        {
            ref var bucket = ref _buckets[index % Capacity];
            if (bucket.IndexPlusOne != index + 1)
            {
                continue;
            }

            var bucketStart = index / (double)BucketsPerSecond;
            var representedEnd = Math.Min(relativeObservedThrough, (index + 1) / (double)BucketsPerSecond);
            var overlap = Math.Max(0d, Math.Min(relativeEnd, representedEnd) - Math.Max(relativeStart, bucketStart));
            var representedSeconds = representedEnd - bucketStart;
            var fraction = representedSeconds > 0d ? Math.Clamp(overlap / representedSeconds, 0d, 1d) : 0d;
            receivedBytes += bucket.ReceivedBytes * fraction;
        }

        return (busySeconds, receivedBytes);
    }

    public void AddBytes(double start, double end, double bytes, double windowStart)
    {
        if (end <= start)
        {
            return;
        }

        // Work relative to the measurement origin. Constructing adjacent bucket
        // edges from large absolute Stopwatch times can introduce tiny gaps
        // between them and lose part of a short character's byte count.
        var relativeStart = start - originSeconds;
        var relativeEnd = end - originSeconds;
        // Retain the whole oldest bucket: Read interpolates that bucket at the
        // rolling cutoff. Clipping it here as well would count only a fraction
        // twice and make late byte totals depend on the sampling schedule.
        var earliestRetained = IndexAtRelative(windowStart - originSeconds) / (double)BucketsPerSecond;
        if (relativeEnd <= earliestRetained)
        {
            return;
        }
        var bytesPerSecond = bytes / (relativeEnd - relativeStart);
        relativeStart = Math.Max(relativeStart, earliestRetained);
        var first = IndexAtRelative(relativeStart);
        var last = IndexAtRelative(relativeEnd);
        for (var index = first; index <= last; index++)
        {
            var bucketStart = index / (double)BucketsPerSecond;
            var bucketEnd = (index + 1) / (double)BucketsPerSecond;
            var overlap = Math.Max(0d, Math.Min(relativeEnd, bucketEnd) - Math.Max(relativeStart, bucketStart));
            if (overlap <= 0d)
            {
                continue;
            }

            ref var bucket = ref _buckets[index % Capacity];
            if (bucket.IndexPlusOne != index + 1)
            {
                bucket = new Bucket { IndexPlusOne = index + 1 };
            }

            bucket.ReceivedBytes += overlap * bytesPerSecond;
        }
    }

    private int FindFirstInterval(double seconds)
    {
        var low = 0;
        var high = _busyIntervals.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_busyIntervals[middle].EndSeconds < seconds)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    private static long IndexAtRelative(double seconds) => (long)Math.Floor(Math.Max(0d, seconds) * BucketsPerSecond);

    private readonly record struct BusyInterval(double StartSeconds, double EndSeconds);

    private struct Bucket
    {
        public long IndexPlusOne;
        public double ReceivedBytes;
    }
}
