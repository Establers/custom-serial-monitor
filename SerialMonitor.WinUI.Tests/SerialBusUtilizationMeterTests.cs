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
        int dataBits,
        SerialParityMode parity,
        SerialStopBitsMode stopBits,
        double expected)
    {
        var actual = SerialBusUtilizationMeter.CalculateBitsPerCharacter(dataBits, parity, stopBits);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Sample_UsesElapsedWarmupWindowBeforeOneMinute()
    {
        var meter = Create8N1Meter(baudRate: 9_600);

        var snapshot = meter.Sample(observedAtSeconds: 10d, cumulativeReceivedBytes: 4_800);

        Assert.True(snapshot.IsAvailable);
        Assert.False(snapshot.IsFullWindow);
        Assert.Equal(10d, snapshot.ObservationSeconds, 6);
        Assert.Equal(50d, snapshot.BusyPercent, 6);
        Assert.Equal(50d, snapshot.IdlePercent, 6);
        Assert.False(snapshot.IsPeakAvailable);
        Assert.Equal(0d, snapshot.PeakBusyPercent, 6);
    }

    [Fact]
    public void Sample_UsesRollingSixtySecondWindow()
    {
        var meter = Create8N1Meter(baudRate: 9_600);
        long cumulativeBytes = 0;

        for (var second = 1; second <= 70; second++)
        {
            cumulativeBytes += 480;
            meter.Sample(second, cumulativeBytes);
        }

        var snapshot = meter.Sample(observedAtSeconds: 70d, cumulativeReceivedBytes: cumulativeBytes);

        Assert.True(snapshot.IsFullWindow);
        Assert.Equal(60d, snapshot.ObservationSeconds, 6);
        Assert.Equal(50d, snapshot.BusyPercent, 6);
        Assert.Equal(50d, snapshot.IdlePercent, 6);
        Assert.True(snapshot.IsPeakAvailable);
        Assert.Equal(50d, snapshot.PeakBusyPercent, 6);
    }

    [Fact]
    public void Sample_ClampsImpossibleObservedRateToFullyBusy()
    {
        var meter = Create8N1Meter(baudRate: 9_600);

        var snapshot = meter.Sample(observedAtSeconds: 1d, cumulativeReceivedBytes: 2_000);

        Assert.Equal(100d, snapshot.BusyPercent, 6);
        Assert.Equal(0d, snapshot.IdlePercent, 6);
        Assert.False(snapshot.IsPeakAvailable);
        Assert.Equal(0d, snapshot.PeakBusyPercent, 6);
    }

    [Fact]
    public void Sample_DropsTrafficOlderThanRollingWindow()
    {
        var meter = Create8N1Meter(baudRate: 9_600);
        meter.Sample(observedAtSeconds: 1d, cumulativeReceivedBytes: 960);

        SerialBusUtilizationSnapshot snapshot = default;
        for (var second = 2; second <= 61; second++)
        {
            snapshot = meter.Sample(second, cumulativeReceivedBytes: 960);
        }

        Assert.True(snapshot.IsFullWindow);
        Assert.Equal(0d, snapshot.BusyPercent, 6);
        Assert.Equal(100d, snapshot.IdlePercent, 6);
        Assert.True(snapshot.IsPeakAvailable);
        Assert.Equal(0d, snapshot.PeakBusyPercent, 6);
    }

    [Fact]
    public void Sample_PeakIsHighestFixedOneSecondBucketInsideFullRollingWindow()
    {
        var meter = Create8N1Meter(baudRate: 9_600);
        long cumulativeBytes = 0;

        SerialBusUtilizationSnapshot snapshot = default;
        for (var second = 1; second <= 60; second++)
        {
            cumulativeBytes += second == 2 ? 960 : 240;
            snapshot = meter.Sample(second, cumulativeBytes);
        }

        Assert.True(snapshot.IsPeakAvailable);
        Assert.Equal(100d, snapshot.PeakBusyPercent, 6);
    }

    [Fact]
    public void Sample_ShortFirstSampleCannotBecomeOneSecondPeak()
    {
        var meter = Create8N1Meter(baudRate: 9_600);
        meter.Sample(observedAtSeconds: 0.1d, cumulativeReceivedBytes: 96);

        SerialBusUtilizationSnapshot snapshot = default;
        for (var second = 1; second <= 60; second++)
        {
            snapshot = meter.Sample(second, cumulativeReceivedBytes: 96);
        }

        Assert.True(snapshot.IsPeakAvailable);
        Assert.Equal(10d, snapshot.PeakBusyPercent, 6);
    }

    [Fact]
    public void Sample_PeakRollsOutAfterSixtySeconds()
    {
        var meter = Create8N1Meter(baudRate: 9_600);
        meter.Sample(observedAtSeconds: 1d, cumulativeReceivedBytes: 960);

        SerialBusUtilizationSnapshot snapshot = default;
        for (var second = 2; second <= 61; second++)
        {
            snapshot = meter.Sample(second, cumulativeReceivedBytes: 960);
        }

        Assert.Equal(0d, snapshot.PeakBusyPercent, 6);
    }

    [Fact]
    public void Sample_CounterResetStartsANewWindowWithoutChangingFraming()
    {
        var meter = new SerialBusUtilizationMeter();
        meter.Start(
            observedAtSeconds: 0d,
            cumulativeReceivedBytes: 1_000,
            baudRate: 9_600,
            dataBits: 8,
            SerialParityMode.Even,
            SerialStopBitsMode.One);
        meter.Sample(observedAtSeconds: 1d, cumulativeReceivedBytes: 1_480);

        var resetSnapshot = meter.Sample(observedAtSeconds: 2d, cumulativeReceivedBytes: 0);
        var recoveredSnapshot = meter.Sample(observedAtSeconds: 3d, cumulativeReceivedBytes: 480);

        Assert.False(resetSnapshot.IsAvailable);
        Assert.True(recoveredSnapshot.IsAvailable);
        Assert.Equal(11d, recoveredSnapshot.BitsPerCharacter, 6);
        Assert.Equal(55d, recoveredSnapshot.BusyPercent, 6);
    }

    [Fact]
    public void Stop_MakesSnapshotUnavailable()
    {
        var meter = Create8N1Meter(baudRate: 115_200);
        meter.Stop();

        var snapshot = meter.Sample(observedAtSeconds: 1d, cumulativeReceivedBytes: 100);

        Assert.False(snapshot.IsAvailable);
    }

    private static SerialBusUtilizationMeter Create8N1Meter(int baudRate)
    {
        var meter = new SerialBusUtilizationMeter();
        meter.Start(
            observedAtSeconds: 0d,
            cumulativeReceivedBytes: 0,
            baudRate,
            dataBits: 8,
            SerialParityMode.None,
            SerialStopBitsMode.One);
        return meter;
    }
}
