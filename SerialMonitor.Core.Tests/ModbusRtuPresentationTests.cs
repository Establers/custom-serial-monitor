using SerialMonitor.Core.Protocols;

namespace SerialMonitor.Core.Tests;

public sealed class ModbusRtuPresentationTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    public void InvalidCrc_HasItsOwnState_AndDoesNotDecodeValues(byte function)
    {
        var frame = new ModbusRtuFrame(1, 1, DateTimeOffset.Now, ProtocolDirection.Tx,
            new byte[] { 1, function, 0, 0, 0, 2, 0, 0 }, ModbusFrameKind.Incomplete, ModbusCrcStatus.Invalid);
        Assert.Equal(ModbusFrameStatus.CrcError, frame.Status);
        var detail = frame.Describe();
        Assert.Contains("CRC error", detail);
        Assert.DoesNotContain("Incomplete", detail);
        Assert.DoesNotContain("Start address", detail);
        Assert.DoesNotContain("Address (base 0)", detail);
        Assert.Contains("candidate length is complete", detail);
    }

    [Fact]
    public void Exception_IsNotPresentedAsAnOrdinaryCrcValidPacket()
    {
        var frame = new ModbusRtuFrame(1, 1, DateTimeOffset.Now, ProtocolDirection.Rx,
            new byte[] { 1, 0x83, 2, 0, 0 }, ModbusFrameKind.Exception, ModbusCrcStatus.Valid);
        Assert.Equal(ModbusFrameStatus.Exception, frame.Status);
        Assert.Contains("Exception response", frame.Describe());
        Assert.Contains("Illegal data address", frame.Describe());
        Assert.Equal(ModbusFrameStatus.Incomplete, (frame with { CrcStatus = ModbusCrcStatus.Unavailable }).Status);
        Assert.Equal(ModbusFrameStatus.Undecoded, (frame with { Kind = ModbusFrameKind.Undecoded }).Status);
    }

    [Fact]
    public async Task MetadataOnlySnapshots_ReportMonotonicIdsAcrossStatisticsResetAndClear()
    {
        await using var analyzer = new ModbusRtuAnalyzer(historyCapacity: 2);
        analyzer.SetEnabled(true);
        var request = Convert.FromHexString("010300000002C40B");
        analyzer.TryEnqueue(request, ProtocolDirection.Tx, 1, DateTimeOffset.Now);
        await WaitForId(analyzer, 1);
        var metadata = analyzer.Snapshot(includeFrames: false);
        Assert.Null(metadata.Frames);
        Assert.Equal(1, metadata.RetainedCount);
        Assert.Equal(1, metadata.Statistics.Tx);
        analyzer.ResetStatistics();
        Assert.Equal(1, analyzer.Snapshot(includeFrames: false).LastFrameId);
        Assert.Equal(0, analyzer.Snapshot(includeFrames: false).TotalFrames);
        analyzer.Clear();
        analyzer.TryEnqueue(request, ProtocolDirection.Tx, 1, DateTimeOffset.Now);
        await WaitForId(analyzer, 2);
        var current = analyzer.Snapshot(includeFrames: false);
        Assert.Null(current.Frames);
        Assert.Equal(2, current.LastFrameId);
        Assert.Equal(1, current.TotalFrames);
        Assert.Single(analyzer.Snapshot().Frames!);
    }

    private static async Task WaitForId(ModbusRtuAnalyzer analyzer, long id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (analyzer.Snapshot(includeFrames: false).LastFrameId < id) await Task.Delay(5, timeout.Token);
    }
}
