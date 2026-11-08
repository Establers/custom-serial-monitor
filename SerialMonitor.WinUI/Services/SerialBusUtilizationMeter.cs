using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

internal readonly record struct SerialBusUtilizationSnapshot(
    bool IsAvailable,
    double BusyPercent,
    double IdlePercent,
    double PeakBusyPercent,
    bool IsPeakAvailable,
    double ObservationSeconds,
    double ReceivedBytes,
    int BaudRate,
    double BitsPerCharacter,
    bool IsFullWindow,
    bool HasCoalescedTiming);

internal sealed class SerialBusUtilizationMeter
{
    internal const double WindowSeconds = 60d;
    internal const int MaximumPendingIntervals = 4_096;

    private Measurement? _measurement;

    public bool IsActive => Volatile.Read(ref _measurement) is not null;

    // Start, Stop and Sample belong to the UI snapshot consumer. Offer is the
    // RX producer: it only queues timing metadata, never bytes or UI work.
    public void Start(
        double observedAtSeconds,
        long receiveSessionGeneration,
        int baudRate,
        int dataBits,
        SerialParityMode parity,
        SerialStopBitsMode stopBits)
    {
        if (!double.IsFinite(observedAtSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(observedAtSeconds));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baudRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dataBits);
        Volatile.Write(ref _measurement, new Measurement(
            observedAtSeconds, receiveSessionGeneration, baudRate,
            CalculateBitsPerCharacter(dataBits, parity, stopBits)));
    }

    public void Stop() => Volatile.Write(ref _measurement, null);

    public void Offer(
        long receiveSessionGeneration,
        double receivedAtSeconds,
        int byteCount,
        bool endsAtNativeIdleBoundary = false,
        int appliedIdleTimeoutMs = 0)
    {
        var measurement = Volatile.Read(ref _measurement);
        if (measurement is null || measurement.Generation != receiveSessionGeneration ||
            byteCount <= 0 || !double.IsFinite(receivedAtSeconds) ||
            receivedAtSeconds < measurement.StartSeconds)
        {
            return;
        }

        // A native idle-terminated completion is timestamped AFTER its timeout.
        // That known silence must not be mistaken for the end of wire activity.
        var wireEnd = receivedAtSeconds - (endsAtNativeIdleBoundary
            ? Math.Max(0, appliedIdleTimeoutMs) / 1_000d
            : 0d);
        var wireStart = wireEnd - byteCount * measurement.CharacterSeconds;
        if (wireEnd <= measurement.StartSeconds)
        {
            return;
        }

        var visibleFraction = (wireEnd - Math.Max(wireStart, measurement.StartSeconds)) /
            (wireEnd - wireStart);
        var interval = new ReceiveInterval(
            Math.Max(wireStart, measurement.StartSeconds), wireEnd,
            byteCount * visibleFraction, false);

        lock (measurement.PendingGate)
        {
            EnqueueInterval(measurement.Pending, interval);
        }
    }

    public SerialBusUtilizationSnapshot Sample(double observedAtSeconds)
    {
        var measurement = Volatile.Read(ref _measurement);
        if (measurement is null || !double.IsFinite(observedAtSeconds) ||
            observedAtSeconds <= measurement.StartSeconds ||
            observedAtSeconds < measurement.LastSampleSeconds)
        {
            return default;
        }

        lock (measurement.PendingGate)
        {
            (measurement.Pending, measurement.Processing) = (measurement.Processing, measurement.Pending);
        }

        // Bounded work once per UI status batch. The producer can immediately
        // keep filling the other queue while the timeline is updated.
        var windowStart = Math.Max(measurement.StartSeconds, observedAtSeconds - WindowSeconds);
        measurement.Timeline.TrimBefore(windowStart);
        var retainedCount = 0;
        for (var index = 0; index < measurement.Deferred.Count; index++)
        {
            var remaining = ConsumeInterval(measurement, measurement.Deferred[index], observedAtSeconds, windowStart);
            if (remaining.HasValue)
            {
                measurement.Deferred[retainedCount++] = remaining.Value;
            }
        }
        measurement.Deferred.RemoveRange(retainedCount, measurement.Deferred.Count - retainedCount);
        foreach (var interval in measurement.Processing)
        {
            var remaining = ConsumeInterval(measurement, interval, observedAtSeconds, windowStart);
            if (remaining.HasValue)
            {
                EnqueueInterval(measurement.Deferred, remaining.Value);
            }
        }
        measurement.Processing.Clear();

        // Complete a qualification interval that crossed the previous sample.
        if (measurement.LastWireEndSeconds > double.NegativeInfinity)
        {
            measurement.Timeline.AddBusy(measurement.LastWireEndSeconds,
                Math.Min(observedAtSeconds, measurement.LastWireEndSeconds + measurement.CharacterSeconds),
                windowStart);
        }
        measurement.LastSampleSeconds = observedAtSeconds;

        var observationSeconds = observedAtSeconds - windowStart;
        var totals = measurement.Timeline.Read(windowStart, observedAtSeconds, observedAtSeconds);
        var busyPercent = Math.Clamp(totals.BusySeconds / observationSeconds * 100d, 0d, 100d);
        var isFullWindow = observationSeconds >= WindowSeconds;
        var peakBusyPercent = 0d;
        if (isFullWindow)
        {
            for (var second = 0; second < (int)WindowSeconds; second++)
            {
                var bucket = measurement.Timeline.Read(windowStart + second, windowStart + second + 1d, observedAtSeconds);
                peakBusyPercent = Math.Max(peakBusyPercent, Math.Clamp(bucket.BusySeconds * 100d, 0d, 100d));
            }
        }

        return new SerialBusUtilizationSnapshot(
            true, busyPercent, 100d - busyPercent, peakBusyPercent, isFullWindow,
            observationSeconds, totals.ReceivedBytes, measurement.BaudRate,
            measurement.BitsPerCharacter, isFullWindow,
            Math.Max(measurement.LastCoalescedSeconds, measurement.Timeline.LastCoalescedSeconds) >= windowStart);
    }

    internal static double CalculateBitsPerCharacter(
        int dataBits,
        SerialParityMode parity,
        SerialStopBitsMode stopBits)
    {
        var parityBits = parity == SerialParityMode.None ? 0d : 1d;
        var stopBitCount = stopBits switch
        {
            SerialStopBitsMode.OnePointFive => 1.5d,
            SerialStopBitsMode.Two => 2d,
            _ => 1d
        };

        return 1d + dataBits + parityBits + stopBitCount;
    }

    private static ReceiveInterval? ConsumeInterval(
        Measurement measurement, ReceiveInterval interval, double observedAtSeconds, double windowStart)
    {
        if (interval.StartSeconds >= observedAtSeconds)
        {
            return interval;
        }

        var wireEnd = Math.Min(interval.WireEndSeconds, observedAtSeconds);
        var receivedBytes = interval.ByteCount *
            ((wireEnd - interval.StartSeconds) / (interval.WireEndSeconds - interval.StartSeconds));
        measurement.Timeline.AddBytes(interval.StartSeconds, wireEnd, receivedBytes, windowStart);
        var isComplete = wireEnd == interval.WireEndSeconds;
        measurement.Timeline.AddBusy(interval.StartSeconds,
            isComplete ? Math.Min(wireEnd + measurement.CharacterSeconds, observedAtSeconds) : wireEnd,
            windowStart);
        if (isComplete)
        {
            measurement.LastWireEndSeconds = Math.Max(measurement.LastWireEndSeconds, wireEnd);
        }
        if (interval.Coalesced)
        {
            measurement.LastCoalescedSeconds = Math.Max(measurement.LastCoalescedSeconds, wireEnd);
        }

        // A producer can enqueue after the UI has read its sample timestamp.
        // Preserve both the future wire span and its proportional byte count.
        // Idle qualification starts only after the real end, never the cutoff.
        return isComplete ? null : new ReceiveInterval(
            wireEnd, interval.WireEndSeconds, interval.ByteCount - receivedBytes, interval.Coalesced);
    }

    private static void EnqueueInterval(List<ReceiveInterval> queue, ReceiveInterval interval)
    {
        if (queue.Count > 0)
        {
            var previous = queue[^1];
            // Only truly contiguous wire spans have the same byte density as
            // their envelope. Merging a short gap or overlapping spans would
            // redistribute bytes differently depending on UI sample cadence.
            var contiguous = previous.WireEndSeconds == interval.StartSeconds;
            if (contiguous || queue.Count == MaximumPendingIntervals)
            {
                queue[^1] = new ReceiveInterval(
                    Math.Min(previous.StartSeconds, interval.StartSeconds),
                    Math.Max(previous.WireEndSeconds, interval.WireEndSeconds),
                    previous.ByteCount + interval.ByteCount,
                    previous.Coalesced || interval.Coalesced || !contiguous);
                return;
            }
        }

        queue.Add(interval);
    }

    private readonly record struct ReceiveInterval(
        double StartSeconds, double WireEndSeconds, double ByteCount, bool Coalesced);

    private sealed class Measurement(double startSeconds, long generation, int baudRate, double bitsPerCharacter)
    {
        public double StartSeconds { get; } = startSeconds;
        public long Generation { get; } = generation;
        public int BaudRate { get; } = baudRate;
        public double BitsPerCharacter { get; } = bitsPerCharacter;
        public double CharacterSeconds { get; } = bitsPerCharacter / baudRate;
        public SerialBusActivityTimeline Timeline { get; } = new(startSeconds);
        public object PendingGate { get; } = new();
        public List<ReceiveInterval> Pending { get; set; } = new(64);
        public List<ReceiveInterval> Processing { get; set; } = new(64);
        public List<ReceiveInterval> Deferred { get; } = new(64);
        public double LastSampleSeconds { get; set; } = startSeconds;
        public double LastWireEndSeconds { get; set; } = double.NegativeInfinity;
        public double LastCoalescedSeconds { get; set; } = double.NegativeInfinity;
    }
}
