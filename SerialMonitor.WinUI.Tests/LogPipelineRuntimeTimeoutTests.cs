using System.Diagnostics;
using System.Threading.Channels;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogPipelineRuntimeTimeoutTests
{
    [Fact]
    public async Task AdjustingTimeoutOnSameTransportChangesGroupingWithoutLosingBytes()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var input = Channel.CreateUnbounded<ReceivedByteChunk>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, hexGroupTimeoutMs: 10);
        await pipeline.StartAsync(input.Reader, new SerialSettings(), cancellation.Token);
        try
        {
            var gap = (long)Math.Round(0.030 * Stopwatch.Frequency);
            var timestamp = Stopwatch.GetTimestamp();
            await input.Writer.WriteAsync(new ReceivedByteChunk([0xAA], timestamp), cancellation.Token);
            await input.Writer.WriteAsync(new ReceivedByteChunk([0xBB], timestamp + gap, endsAtNativeIdleBoundary: true), cancellation.Token);
            Assert.Equal(new byte[] { 0xAA }, await ReadGroupAsync(pipeline.Logs, cancellation.Token));
            Assert.Equal(new byte[] { 0xBB }, await ReadGroupAsync(pipeline.Logs, cancellation.Token));

            pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, hexGroupTimeoutMs: 100);
            timestamp = Stopwatch.GetTimestamp();
            await input.Writer.WriteAsync(new ReceivedByteChunk([0xCC], timestamp), cancellation.Token);
            await input.Writer.WriteAsync(new ReceivedByteChunk([0xDD], timestamp + gap, endsAtNativeIdleBoundary: true), cancellation.Token);
            Assert.Equal(new byte[] { 0xCC, 0xDD }, await ReadGroupAsync(pipeline.Logs, cancellation.Token));
            Assert.Equal(100, pipeline.HexGroupTimeoutMs);
            Assert.Equal(4, pipeline.ProcessedRxByteCount);
            Assert.Equal(4, pipeline.HexAcceptedByteCount);
            Assert.Equal(4, pipeline.HexEmittedByteCount);
            Assert.Equal(0, pipeline.HexPendingByteCount);
        }
        finally
        {
            input.Writer.TryComplete();
            await pipeline.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<byte[]> ReadGroupAsync(ChannelReader<LogLine> logs, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        await foreach (var line in logs.ReadAllAsync(cancellationToken))
        {
            if (line.RawBytes is { Length: > 0 } rawBytes) bytes.AddRange(rawBytes);
            if (line.IsPartialRxTerminator) return bytes.ToArray();
        }
        throw new InvalidOperationException("HEX group did not finish.");
    }
}
