using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialBusUtilizationMeterTests
{
    [Theory]
    [InlineData(8, SerialParityMode.None, SerialStopBitsMode.One, 10d)]
    [InlineData(8, SerialParityMode.Even, SerialStopBitsMode.One, 11d)]
    [InlineData(7, SerialParityMode.Odd, SerialStopBitsMode.OnePointFive, 10.5d)]
    [InlineData(8, SerialParityMode.None, SerialStopBitsMode.Two, 11d)]
    public void CalculateBitsPerCharacter_IncludesSerialFraming(
        int dataBits, SerialParityMode parity, SerialStopBitsMode stopBits, double expected)
    {
        Assert.Equal(expected, SerialBusUtilizationMeter.CalculateBitsPerCharacter(dataBits, parity, stopBits));
    }

    [Theory]
    [InlineData(9_600)]
    [InlineData(115_200)]
    [InlineData(1_000_000)]
    public void Sample_SubCharacterGapsStayBusyAtRealisticBaudRates(int baudRate)
    {
        var meter = CreateMeter(baudRate);
        var characterSeconds = 10d / baudRate;
        var end = 0d;
        for (var index = 0; index < 1_000; index++)
        {
            end = characterSeconds + index * characterSeconds * 1.5d;
            meter.Offer(1, end, 1);
        }

        // The byte-only calculation would report about 67% despite every gap
        // being too short to fit another complete UART character.
        var snapshot = meter.Sample(end);

        Assert.Equal(100d, snapshot.BusyPercent, 5);
        Assert.Equal(0d, snapshot.IdlePercent, 5);
        Assert.Equal(1_000d, snapshot.ReceivedBytes, 5);
        Assert.False(snapshot.HasCoalescedTiming);
    }

    [Fact]
    public void Sample_OneCharacterGapsDoNotExposeIdleBeforeQualificationCompletes()
    {
        var meter = CreateMeter();
        for (var index = 0; index < 50; index++)
        {
            meter.Offer(1, 0.01d + index * 0.02d, 1);
        }

        var snapshot = meter.Sample(1d);

        Assert.Equal(100d, snapshot.BusyPercent, 6);
        Assert.Equal(50d, snapshot.ReceivedBytes, 6);
    }

    [Fact]
    public void Sample_LongerGapsOnlyCountSilenceAfterOneCharacterAsIdle()
    {
        var meter = CreateMeter();
        for (var index = 0; index < 25; index++)
        {
            // 10 ms on the wire + 10 ms qualification + 20 ms usable idle.
            meter.Offer(1, 0.01d + index * 0.04d, 1);
        }

        var snapshot = meter.Sample(1d);

        Assert.Equal(50d, snapshot.BusyPercent, 6);
        Assert.Equal(50d, snapshot.IdlePercent, 6);
        Assert.Equal(25d, snapshot.ReceivedBytes, 6);
    }

    [Fact]
    public void Sample_SameByteCountHasDifferentBusyWhenGapsHaveDifferentUsableSpace()
    {
        var burst = CreateMeter();
        burst.Offer(1, 0.5d, 50);
        var interleaved = CreateMeter();
        for (var index = 0; index < 50; index++)
        {
            interleaved.Offer(1, 0.01d + index * 0.02d, 1);
        }

        var burstSnapshot = burst.Sample(1d);
        var interleavedSnapshot = interleaved.Sample(1d);

        Assert.Equal(burstSnapshot.ReceivedBytes, interleavedSnapshot.ReceivedBytes, 6);
        Assert.Equal(51d, burstSnapshot.BusyPercent, 6);
        Assert.Equal(100d, interleavedSnapshot.BusyPercent, 6);
    }

    [Theory]
    [InlineData(SerialParityMode.None, SerialStopBitsMode.One, 2d)]
    [InlineData(SerialParityMode.Even, SerialStopBitsMode.One, 2.2d)]
    [InlineData(SerialParityMode.None, SerialStopBitsMode.OnePointFive, 2.1d)]
    [InlineData(SerialParityMode.None, SerialStopBitsMode.Two, 2.2d)]
    public void Sample_UsesAppliedFramingForWireTimeAndIdleQualification(
        SerialParityMode parity, SerialStopBitsMode stopBits, double expectedBusyPercent)
    {
        var meter = new SerialBusUtilizationMeter();
        meter.Start(0d, 1, 1_000, 8, parity, stopBits);
        meter.Offer(1, 0.1d, 1);

        Assert.Equal(expectedBusyPercent, meter.Sample(1d).BusyPercent, 6);
    }

    [Fact]
    public void Sample_ContiguousDriverChunksOnlyAddOneTrailingQualification()
    {
        var combined = CreateMeter();
        combined.Offer(1, 0.5d, 50);
        var split = CreateMeter();
        for (var index = 1; index <= 50; index++)
        {
            split.Offer(1, index * 0.01d, 1);
            split.Sample(index * 0.01d);
        }

        Assert.Equal(combined.Sample(1d).BusyPercent, split.Sample(1d).BusyPercent, 6);
        Assert.Equal(51d, split.Sample(1d).BusyPercent, 6);
    }

    [Fact]
    public void Sample_QualificationCrossingUiSampleIsNotLostOrRepeated()
    {
        var meter = CreateMeter();
        meter.Offer(1, 0.995d, 1);

        Assert.Equal(1.5d, meter.Sample(1d).BusyPercent, 6);
        Assert.Equal(1d, meter.Sample(2d).BusyPercent, 6);
        Assert.Equal(1d, meter.Sample(2d).BusyPercent, 6);
    }

    [Fact]
    public void Sample_RxEnqueuedAfterCutoffIsPreservedForTheNextSample()
    {
        var racing = CreateMeter();
        racing.Offer(1, 1.1d, 1);

        var early = racing.Sample(1d);
        var later = racing.Sample(2d);
        var uninterrupted = CreateMeter();
        uninterrupted.Offer(1, 1.1d, 1);
        var expected = uninterrupted.Sample(2d);

        Assert.Equal(0d, early.ReceivedBytes);
        Assert.Equal(0d, early.BusyPercent);
        Assert.Equal(1d, later.ReceivedBytes, 6);
        Assert.Equal(1d, later.BusyPercent, 6);
        Assert.Equal(expected.ReceivedBytes, later.ReceivedBytes, 6);
        Assert.Equal(expected.BusyPercent, later.BusyPercent, 6);
    }

    [Fact]
    public void Sample_IntervalCrossingSeveralCutoffsPreservesByteProportionsAndTrueGuardEnd()
    {
        var meter = CreateMeter();
        meter.Offer(1, 1.1d, 50); // Wire: 0.6..1.1; guard: 1.1..1.11.
        foreach (var cutoff in new[] { 0.7d, 0.8d, 0.9d, 1d, 1.05d })
        {
            var snapshot = meter.Sample(cutoff);
            Assert.Equal((cutoff - 0.6d) * 100d, snapshot.ReceivedBytes, 6);
            Assert.Equal((cutoff - 0.6d) / cutoff * 100d, snapshot.BusyPercent, 6);
            Assert.Equal(snapshot, meter.Sample(cutoff));
        }

        var completed = meter.Sample(2d);
        Assert.Equal(50d, completed.ReceivedBytes, 6);
        Assert.Equal(25.5d, completed.BusyPercent, 6);
    }

    [Fact]
    public void Sample_RepeatedFutureBatchesKeepTheirBytesWhenDeferredStorageSaturates()
    {
        var meter = CreateMeter(1_000_000);
        var batchSize = SerialBusUtilizationMeter.MaximumPendingIntervals;
        for (var batch = 0; batch < 3; batch++)
        {
            for (var index = 0; index < batchSize; index++)
            {
                meter.Offer(1, 10d + (batch * batchSize + index) * 0.0001d, 1);
            }
            var before = meter.Sample(batch + 1d);
            Assert.Equal(0d, before.ReceivedBytes);
            Assert.Equal(0d, before.BusyPercent);
        }

        var completed = meter.Sample(20d);
        Assert.Equal(3d * batchSize, completed.ReceivedBytes, 6);
        Assert.True(completed.HasCoalescedTiming);
        Assert.InRange(completed.BusyPercent, 3d * batchSize * 0.00002d / 20d * 100d, 100d);
    }

    [Theory]
    [InlineData(1_000, 0d)]
    [InlineData(115_200, 2_000_000.123456d)]
    public void Sample_LateOverlappingChunkCountsItsWholeUnionRegardlessOfSampleCadence(int baudRate, double origin)
    {
        var characterSeconds = 10d / baudRate;
        var sampled = new SerialBusUtilizationMeter();
        sampled.Start(origin, 1, baudRate, 8, SerialParityMode.None, SerialStopBitsMode.One);
        sampled.Offer(1, origin + 100d * characterSeconds, 1);
        sampled.Sample(origin + 105d * characterSeconds);
        sampled.Offer(1, origin + 110d * characterSeconds, 50);

        var batched = new SerialBusUtilizationMeter();
        batched.Start(origin, 1, baudRate, 8, SerialParityMode.None, SerialStopBitsMode.One);
        batched.Offer(1, origin + 100d * characterSeconds, 1);
        batched.Offer(1, origin + 110d * characterSeconds, 50);
        var result = sampled.Sample(origin + 200d * characterSeconds);
        var expected = batched.Sample(origin + 200d * characterSeconds);

        Assert.Equal(25.5d, result.BusyPercent, 5);
        Assert.Equal(expected.BusyPercent, result.BusyPercent, 6);
        Assert.Equal(51d, result.ReceivedBytes, 5);
        Assert.Equal(expected.ReceivedBytes, result.ReceivedBytes, 6);
    }

    [Fact]
    public void Sample_SubMillisecondOverlapsAndDisjointSpansShareABucketWithoutDoubleCounting()
    {
        var meter = CreateMeter(1_000_000);
        meter.Offer(1, 0.00011d, 1); // Busy: 100..120 us.
        meter.Sample(0.0002d);
        meter.Offer(1, 0.000105d, 2); // Late busy span: 85..115 us.
        meter.Offer(1, 0.00019d, 1); // Separate busy span: 180..200 us.

        var snapshot = meter.Sample(0.001d);

        Assert.Equal(5.5d, snapshot.BusyPercent, 6); // Union is 35 + 20 us.
        Assert.Equal(4d, snapshot.ReceivedBytes, 6);
    }

    [Fact]
    public void Sample_LateOverlapKeepsRollingAverageAndPeakIndependentOfIntermediateSamples()
    {
        var sampled = CreateMeter();
        var batched = CreateMeter();
        foreach (var meter in new[] { sampled, batched })
        {
            meter.Offer(1, 60.99d, 1);
        }
        sampled.Sample(61.04d);
        foreach (var meter in new[] { sampled, batched })
        {
            meter.Offer(1, 61.1d, 50); // Busy: 60.6..61.11.
        }

        var snapshot = sampled.Sample(120.75d);
        var expected = batched.Sample(120.75d);

        Assert.Equal(0.6d, snapshot.BusyPercent, 6); // 0.36 s / 60 s.
        Assert.Equal(36d, snapshot.PeakBusyPercent, 6);
        Assert.Equal(expected.BusyPercent, snapshot.BusyPercent, 6);
        Assert.Equal(expected.PeakBusyPercent, snapshot.PeakBusyPercent, 6);
        Assert.Equal(expected.ReceivedBytes, snapshot.ReceivedBytes, 6);
    }

    [Fact]
    public void Sample_NativeTimeoutSilenceIsSubtractedFromCompletionTimestamp()
    {
        var direct = CreateMeter();
        direct.Offer(1, 0.99d, 1);
        var delayed = CreateMeter();
        delayed.Offer(1, 1.09d, 1, endsAtNativeIdleBoundary: true, appliedIdleTimeoutMs: 100);

        Assert.Equal(direct.Sample(1.1d).BusyPercent, delayed.Sample(1.1d).BusyPercent, 6);
        // All busy time ends at 1.0 s, so the shifted window has no residual peak.
        Assert.Equal(0d, delayed.Sample(61d).BusyPercent, 6);
    }

    [Fact]
    public void Sample_UsesElapsedWarmupAndRollingSixtySecondWindow()
    {
        var meter = CreateMeter();
        SerialBusUtilizationSnapshot snapshot = default;
        for (var second = 1; second <= 70; second++)
        {
            meter.Offer(1, second - 0.5d, 50);
            snapshot = meter.Sample(second);
            if (second == 10)
            {
                Assert.False(snapshot.IsFullWindow);
                Assert.False(snapshot.IsPeakAvailable);
                Assert.Equal(10d, snapshot.ObservationSeconds, 6);
                Assert.Equal(51d, snapshot.BusyPercent, 6);
            }
        }

        Assert.True(snapshot.IsFullWindow);
        Assert.True(snapshot.IsPeakAvailable);
        Assert.Equal(60d, snapshot.ObservationSeconds, 6);
        Assert.Equal(51d, snapshot.BusyPercent, 6);
        Assert.Equal(51d, snapshot.PeakBusyPercent, 6);
        Assert.Equal(3_000d, snapshot.ReceivedBytes, 6);
    }

    [Fact]
    public void Sample_OldTrafficAndItsQualificationRollOutOfWindow()
    {
        var meter = CreateMeter();
        meter.Offer(1, 1d, 100);
        meter.Sample(1d);
        var snapshot = meter.Sample(61.01d);

        Assert.Equal(0d, snapshot.BusyPercent, 6);
        Assert.Equal(100d, snapshot.IdlePercent, 6);
        Assert.Equal(0d, snapshot.ReceivedBytes, 6);
        Assert.Equal(0d, snapshot.PeakBusyPercent, 6);
    }

    [Fact]
    public void Sample_PeakUsesTimingInsideOneSecondRatherThanTimerAverages()
    {
        var meter = CreateMeter();
        meter.Offer(1, 2d, 100);

        var snapshot = meter.Sample(60d);

        Assert.Equal(100d, snapshot.PeakBusyPercent, 6);
        Assert.Equal(101d / 60d, snapshot.BusyPercent, 6);
    }

    [Fact]
    public void Sample_ShortFirstSampleDoesNotBecomeAFullSecondPeak()
    {
        var meter = CreateMeter();
        meter.Offer(1, 0.1d, 10);
        meter.Sample(0.1d);

        var snapshot = meter.Sample(60d);

        Assert.True(snapshot.IsPeakAvailable);
        Assert.Equal(11d, snapshot.PeakBusyPercent, 6);
    }

    [Fact]
    public void Sample_WindowBoundaryClipsGuardPreciselyWithoutDroppingWholeBuckets()
    {
        var meter = CreateMeter();
        meter.Offer(1, 1d, 100);
        meter.Sample(1d);

        var snapshot = meter.Sample(61.005d);

        Assert.InRange(snapshot.BusyPercent, 0.00832d, 0.00835d);
        Assert.Equal(0.5d, snapshot.PeakBusyPercent, 5);
    }

    [Fact]
    public void Sample_BoundedQueueOverloadIsConservativeAndVisible()
    {
        var meter = CreateMeter(1_000_000);
        var count = SerialBusUtilizationMeter.MaximumPendingIntervals + 500;
        for (var index = 0; index < count; index++)
        {
            meter.Offer(1, 0.00001d + index * 0.0001d, 1);
        }

        var snapshot = meter.Sample(1d);

        Assert.True(snapshot.HasCoalescedTiming);
        Assert.Equal(count, snapshot.ReceivedBytes, 4);
        Assert.InRange(snapshot.BusyPercent, count * 0.002d, 100d);
        Assert.False(meter.Sample(62d).HasCoalescedTiming);
    }

    [Fact]
    public void Start_NewSessionDiscardsQueuedTimingAndIgnoresStaleSession()
    {
        var meter = CreateMeter();
        meter.Offer(1, 0.5d, 50);
        meter.Start(1d, 2, 1_000, 8, SerialParityMode.Even, SerialStopBitsMode.One);
        meter.Offer(1, 1.5d, 50);
        meter.Offer(2, 1.1d, 1);

        var snapshot = meter.Sample(2d);

        Assert.Equal(1d, snapshot.ReceivedBytes, 6);
        Assert.Equal(11d, snapshot.BitsPerCharacter, 6);
        Assert.Equal(2.2d, snapshot.BusyPercent, 6);
    }

    [Fact]
    public void Start_ClearWithinSameSessionExcludesPreResetWireTime()
    {
        var meter = CreateMeter();
        meter.Start(1d, 1, 1_000, 8, SerialParityMode.None, SerialStopBitsMode.One);
        meter.Offer(1, 0.9d, 100);
        // This delayed chunk spans 0.6..1.1, of which only 0.1 s is in this run.
        meter.Offer(1, 1.1d, 50);

        var snapshot = meter.Sample(2d);

        Assert.Equal(10d, snapshot.ReceivedBytes, 6);
        Assert.Equal(11d, snapshot.BusyPercent, 6);
    }

    [Fact]
    public void Stop_MakesSnapshotUnavailableAndIgnoresFurtherRx()
    {
        var meter = CreateMeter();
        meter.Stop();
        meter.Offer(1, 0.5d, 50);

        Assert.False(meter.Sample(1d).IsAvailable);
    }

    [Fact]
    public void Sample_InvalidClockDoesNotConsumePendingRxOrCorruptMeasurement()
    {
        var meter = CreateMeter();
        meter.Sample(0.2d);
        meter.Offer(1, 0.5d, 50);

        Assert.False(meter.Sample(double.NaN).IsAvailable);
        Assert.False(meter.Sample(0.1d).IsAvailable);
        Assert.Equal(51d, meter.Sample(1d).BusyPercent, 6);
    }

    [Fact]
    public async Task Offer_RacingResetAndSamplingRemainsBoundedAndSessionIsolated()
    {
        var meter = CreateMeter(115_200);
        var producer = Task.Run(() =>
        {
            for (var index = 0; index < 100_000; index++)
            {
                meter.Offer(1, 1d, 1);
            }
        });
        for (var index = 0; index < 20; index++)
        {
            meter.Sample(1d);
            meter.Stop();
            meter.Start(0d, 2, 115_200, 8, SerialParityMode.None, SerialStopBitsMode.One);
        }
        await producer.WaitAsync(TimeSpan.FromSeconds(5));

        var snapshot = meter.Sample(1d);
        Assert.Equal(0d, snapshot.BusyPercent);
        Assert.Equal(0d, snapshot.ReceivedBytes);
    }

    [Theory]
    [InlineData(9_600)]
    [InlineData(115_200)]
    [InlineData(1_000_000)]
    public void Sample_MixedTrafficAcrossSeveralRingWrapsMatchesIndependentIntervalUnion(int baudRate)
    {
        // Exercise real-sized monotonic timestamps, sub-ms characters, partial
        // window boundaries, UI jitter and several minutes without wall waits.
        const double origin = 2_000_000.123456d;
        var random = new Random(817);
        var meter = new SerialBusUtilizationMeter();
        meter.Start(origin, 1, baudRate, 8, SerialParityMode.None, SerialStopBitsMode.One);
        var characterSeconds = 10d / baudRate;
        var intervals = new List<(double Start, double End)>();
        var wireEnd = origin;
        var nextSample = origin + 1.00037d;
        while (wireEnd < origin + 185d)
        {
            var gap = random.Next(4) * characterSeconds * 0.5d + random.NextDouble() * 0.015d;
            var bytes = random.Next(1, 97);
            var start = wireEnd + gap;
            wireEnd = start + bytes * characterSeconds;
            while (nextSample < wireEnd)
            {
                CheckAgainstReference(nextSample);
                nextSample += 0.9d + random.NextDouble() * 0.2d;
            }
            meter.Offer(1, wireEnd, bytes);
            intervals.Add((start, wireEnd + characterSeconds));
        }
        CheckAgainstReference(nextSample);

        void CheckAgainstReference(double now)
        {
            var windowStart = Math.Max(origin, now - 60d);
            var duration = now - windowStart;
            var snapshot = meter.Sample(now);
            var expectedBusy = UnionDuration(windowStart, now) / duration * 100d;
            Assert.InRange(Math.Abs(snapshot.BusyPercent - expectedBusy), 0d, 0.002d / duration * 100d);
            Assert.False(snapshot.HasCoalescedTiming);
            if (snapshot.IsPeakAvailable)
            {
                var expectedPeak = Enumerable.Range(0, 60)
                    .Max(second => UnionDuration(windowStart + second, windowStart + second + 1d) * 100d);
                Assert.InRange(Math.Abs(snapshot.PeakBusyPercent - expectedPeak), 0d, 0.201d);
            }
        }

        double UnionDuration(double from, double to)
        {
            var duration = 0d;
            var occupiedUntil = from;
            foreach (var interval in intervals)
            {
                var start = Math.Max(occupiedUntil, interval.Start);
                var end = Math.Min(to, interval.End);
                if (end > start)
                {
                    duration += end - start;
                    occupiedUntil = end;
                }
            }
            return duration;
        }
    }

    private static SerialBusUtilizationMeter CreateMeter(int baudRate = 1_000)
    {
        var meter = new SerialBusUtilizationMeter();
        meter.Start(0d, 1, baudRate, 8, SerialParityMode.None, SerialStopBitsMode.One);
        return meter;
    }
}
