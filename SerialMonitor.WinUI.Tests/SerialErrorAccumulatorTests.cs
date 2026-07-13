using RJCP.IO.Ports;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialErrorAccumulatorTests
{
    [Fact]
    public void CompositeDriverSignal_CountsEachReportedFlagAndCapturesRxPosition()
    {
        var accumulator = new SerialErrorAccumulator();
        var observedAt = new DateTimeOffset(2026, 7, 14, 1, 2, 3, 456, TimeSpan.FromHours(9));

        accumulator.Record(
            SerialError.Frame | SerialError.RXParity | SerialError.Overrun | SerialError.RXOver,
            observedAt,
            receivedByteCount: 12_345,
            receivedChunkCount: 678);

        Assert.Equal(1, accumulator.FrameCount);
        Assert.Equal(1, accumulator.ParityCount);
        Assert.Equal(1, accumulator.OverrunCount);
        Assert.Equal(1, accumulator.RxOverCount);
        Assert.Contains("Driver-reported", accumulator.LastSummary);
        Assert.Contains("12,345 bytes / 678 chunks", accumulator.LastSummary);
    }

    [Fact]
    public void Reset_ClearsCountersAndSummary()
    {
        var accumulator = new SerialErrorAccumulator();
        accumulator.Record(SerialError.Frame, DateTimeOffset.Now, 11, 1);

        accumulator.Reset();

        Assert.Equal(0, accumulator.FrameCount);
        Assert.Equal(0, accumulator.ParityCount);
        Assert.Equal(0, accumulator.OverrunCount);
        Assert.Equal(0, accumulator.RxOverCount);
        Assert.Equal("(none)", accumulator.LastSummary);
    }
}
