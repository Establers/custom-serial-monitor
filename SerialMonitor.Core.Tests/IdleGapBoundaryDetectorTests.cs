using System.Diagnostics;
using SerialMonitor.Core;

namespace SerialMonitor.Core.Tests;

public sealed class IdleGapBoundaryDetectorTests
{
    public static TheoryData<int[], double[], double, int> VariablePacketScenarios => new()
    {
        { new[] { 1, 7, 11, 23, 255 }, new[] { 0.1, 0.5, 1.0 }, 4.0, 2 },
        { new[] { 3, 64, 2, 511, 19 }, new[] { 1.0, 3.0, 8.0 }, 25.0, 10 },
        { new[] { 4, 37, 128, 9 }, new[] { 5.0, 20.0, 60.0 }, 150.0, 100 },
        { new[] { 1024, 5, 77 }, new[] { 20.0, 100.0, 250.0 }, 700.0, 500 },
        { new[] { 200_000, 1, 65_537 }, new[] { 0.2, 1.0, 4.0 }, 25.0, 10 }
    };

    [Theory]
    [MemberData(nameof(VariablePacketScenarios))]
    public void VariableLengthPackets_AreGroupedOnlyByConfiguredIdleGap(
        int[] packetLengths,
        double[] internalChunkGapsMs,
        double packetGapMs,
        int timeoutMs)
    {
        var observedGroups = GroupScenario(packetLengths, internalChunkGapsMs, packetGapMs, timeoutMs);

        Assert.Equal(packetLengths, observedGroups.Select(group => group.Sum()).ToArray());
    }

    [Fact]
    public void GapEqualToTimeout_StartsNewGroup()
    {
        var previous = Timestamp(TimeSpan.FromMilliseconds(10));
        var current = previous + Timestamp(TimeSpan.FromMilliseconds(2));

        var observation = IdleGapBoundaryDetector.Observe(
            previous,
            current,
            TimeSpan.FromMilliseconds(2));

        Assert.True(observation.StartsNewGroup);
    }

    [Fact]
    public void OutOfOrderTimestamp_IsClampedWithoutCreatingBoundary()
    {
        var previous = Timestamp(TimeSpan.FromMilliseconds(10));
        var observation = IdleGapBoundaryDetector.Observe(
            previous,
            previous - Timestamp(TimeSpan.FromMilliseconds(5)),
            TimeSpan.FromMilliseconds(2));

        Assert.False(observation.StartsNewGroup);
        Assert.Equal(previous, observation.NormalizedTimestamp);
        Assert.Equal(TimeSpan.Zero, observation.ObservedGap);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(5000)]
    public void ConfiguredTimeout_IsUsedWithoutBaudSpecificSubstitution(int timeoutMs)
    {
        var previous = Timestamp(TimeSpan.FromMilliseconds(1));
        var justBefore = previous + Timestamp(TimeSpan.FromMilliseconds(timeoutMs - 0.01));
        var atBoundary = previous + Timestamp(TimeSpan.FromMilliseconds(timeoutMs));
        var timeout = TimeSpan.FromMilliseconds(timeoutMs);

        Assert.False(IdleGapBoundaryDetector.Observe(previous, justBefore, timeout).StartsNewGroup);
        Assert.True(IdleGapBoundaryDetector.Observe(previous, atBoundary, timeout).StartsNewGroup);
    }

    [Fact]
    public void EmptyOrInvalidTimeout_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IdleGapBoundaryDetector.Observe(null, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(1, 1001)]
    [InlineData(2, 2002)]
    [InlineData(10, 3010)]
    [InlineData(100, 4100)]
    [InlineData(500, 5500)]
    public void ThousandDeterministicVariablePackets_PreserveEveryLength(int timeoutMs, int seed)
    {
        var random = new Random(seed);
        var expectedLengths = Enumerable.Range(0, 1_000)
            .Select(_ => random.Next(1, 4_097))
            .ToArray();
        var observedLengths = new List<int>();
        var currentLength = 0;
        long? previousTimestamp = null;
        var currentTimestamp = Timestamp(TimeSpan.FromMilliseconds(1));
        random = new Random(seed);

        foreach (var expectedLength in expectedLengths)
        {
            var remaining = expectedLength;
            while (remaining > 0)
            {
                var chunkLength = Math.Min(remaining, random.Next(1, Math.Min(257, remaining + 1)));
                var observation = IdleGapBoundaryDetector.Observe(
                    previousTimestamp,
                    currentTimestamp,
                    TimeSpan.FromMilliseconds(timeoutMs));
                if (observation.StartsNewGroup)
                {
                    observedLengths.Add(currentLength);
                    currentLength = 0;
                }

                currentLength += chunkLength;
                remaining -= chunkLength;
                previousTimestamp = observation.NormalizedTimestamp;

                // Exercise delayed application transport chunks below the
                // selected timeout. These are not physical idle periods
                // inserted between UART bytes inside a device packet.
                var transportChunkGapMs = timeoutMs == 1
                    ? random.NextDouble() * 0.9
                    : random.NextDouble() * (timeoutMs - 0.1);
                currentTimestamp += Timestamp(TimeSpan.FromMilliseconds(transportChunkGapMs));
            }

            var packetGapMs = timeoutMs + Math.Max(0.1, timeoutMs * random.NextDouble() * 2.0);
            currentTimestamp += Timestamp(TimeSpan.FromMilliseconds(packetGapMs));
        }

        if (currentLength > 0)
        {
            observedLengths.Add(currentLength);
        }

        Assert.Equal(expectedLengths, observedLengths);
        Assert.Equal(expectedLengths.Sum(length => (long)length), observedLengths.Sum(length => (long)length));
    }

    [Fact]
    public void RemainingDelay_UsesSameConfiguredTimeoutBoundary()
    {
        var last = Timestamp(TimeSpan.FromMilliseconds(10));
        var now = last + Timestamp(TimeSpan.FromMilliseconds(37));

        var remaining = IdleGapBoundaryDetector.GetRemainingDelay(
            last,
            now,
            TimeSpan.FromMilliseconds(100));

        Assert.InRange(remaining.TotalMilliseconds, 62.999, 63.001);
    }

    private static IReadOnlyList<IReadOnlyList<int>> GroupScenario(
        IReadOnlyList<int> packetLengths,
        IReadOnlyList<double> internalChunkGapsMs,
        double packetGapMs,
        int timeoutMs)
    {
        var result = new List<IReadOnlyList<int>>();
        var currentGroup = new List<int>();
        long? previousTimestamp = null;
        var currentTimestamp = Timestamp(TimeSpan.FromMilliseconds(1));

        for (var packetIndex = 0; packetIndex < packetLengths.Count; packetIndex++)
        {
            foreach (var chunkLength in SplitIntoVariableChunks(packetLengths[packetIndex], packetIndex))
            {
                var observation = IdleGapBoundaryDetector.Observe(
                    previousTimestamp,
                    currentTimestamp,
                    TimeSpan.FromMilliseconds(timeoutMs));
                if (observation.StartsNewGroup)
                {
                    result.Add(currentGroup.ToArray());
                    currentGroup.Clear();
                }

                currentGroup.Add(chunkLength);
                previousTimestamp = observation.NormalizedTimestamp;
                var internalGap = internalChunkGapsMs[(packetIndex + currentGroup.Count) % internalChunkGapsMs.Count];
                currentTimestamp += Timestamp(TimeSpan.FromMilliseconds(internalGap));
            }

            currentTimestamp += Timestamp(TimeSpan.FromMilliseconds(packetGapMs));
        }

        if (currentGroup.Count > 0)
        {
            result.Add(currentGroup.ToArray());
        }

        return result;
    }

    private static IEnumerable<int> SplitIntoVariableChunks(int length, int seed)
    {
        var remaining = length;
        var next = Math.Max(1, (seed % 7) + 1);
        while (remaining > 0)
        {
            var chunkLength = Math.Min(remaining, next);
            yield return chunkLength;
            remaining -= chunkLength;
            next = next % 31 + 1;
        }
    }

    private static long Timestamp(TimeSpan duration) =>
        (long)Math.Round(duration.TotalSeconds * Stopwatch.Frequency, MidpointRounding.AwayFromZero);
}
