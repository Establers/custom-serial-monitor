using System.Diagnostics;
using System.Threading.Channels;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogPipelineLiveTimeoutTests
{
    [Theory]
    [InlineData(20)]
    [InlineData(100)]
    [InlineData(200)]
    public async Task IdleTimeout_FlushesWithoutSourceCompletion(int timeoutMs)
    {
        await WithPipelineAsync(timeoutMs, async (pipeline, input, token) =>
        {
            // Millisecond-scale timer rounding is acceptable; missing or stale
            // deadlines are not. Keep repeated samples with a small tolerance.
            for (var sample = 0; sample < 20; sample++)
            {
                var receivedAt = Stopwatch.GetTimestamp();
                await input.WriteAsync(new ReceivedByteChunk([0x01, 0xFE], receivedAt), token);
                Assert.Equal(new byte[] { 0x01, 0xFE }, await ReadGroupAsync(pipeline, token));
                var elapsed = Stopwatch.GetElapsedTime(receivedAt);
                Assert.True(elapsed >= TimeSpan.FromMilliseconds(timeoutMs - 2),
                    $"HEX group closed after {elapsed.TotalMilliseconds:F4}ms; configured timeout={timeoutMs}ms; sample={sample}.");
            }
        });
    }

    [Fact]
    public async Task ShorteningTimeout_WakesExistingWaitAndUsesLastReceiveTime()
    {
        await WithPipelineAsync(5000, async (pipeline, input, token) =>
        {
            await input.WriteAsync(new ReceivedByteChunk([0x02], Stopwatch.GetTimestamp()), token);
            await WaitForAcceptedAsync(pipeline, token);
            await Task.Delay(1300, token);
            pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, 1000);
            // The old five-second wait must be interrupted with the source still open.
            Assert.Equal(new byte[] { 0x02 },
                await ReadGroupAsync(pipeline, token).WaitAsync(TimeSpan.FromMilliseconds(750), token));
        });
    }

    [Fact]
    public async Task LengtheningTimeout_CancelsOldDeadlineAndKeepsPendingBytes()
    {
        await WithPipelineAsync(1000, async (pipeline, input, token) =>
        {
            var receivedAt = Stopwatch.GetTimestamp();
            await input.WriteAsync(new ReceivedByteChunk([0x03, 0x04], receivedAt), token);
            await WaitForAcceptedAsync(pipeline, token);
            pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, 2500);
            await Task.Delay(1200, token);
            Assert.False(pipeline.Logs.TryPeek(out _));
            Assert.Equal(2, pipeline.HexPendingByteCount);
            Assert.Equal(new byte[] { 0x03, 0x04 }, await ReadGroupAsync(pipeline, token));
            Assert.True(Stopwatch.GetElapsedTime(receivedAt) >= TimeSpan.FromMilliseconds(2498));
        });
    }

    [Fact]
    public async Task RapidRepeatedChanges_LastTimeoutWinsForEveryPacket()
    {
        await WithPipelineAsync(5000, async (pipeline, input, token) =>
        {
            for (var sample = 0; sample < 12; sample++)
            {
                pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, 5000);
                await input.WriteAsync(new ReceivedByteChunk([(byte)sample], Stopwatch.GetTimestamp()), token);
                await WaitForAcceptedAsync(pipeline, token);
                for (var change = 0; change < 100; change++)
                    pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, change % 2 == 0 ? 4000 : 3000);
                pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, 40);
                Assert.Equal(new byte[] { (byte)sample },
                    await ReadGroupAsync(pipeline, token).WaitAsync(TimeSpan.FromSeconds(1), token));
                Assert.Equal(40, pipeline.HexGroupTimeoutMs);
            }
        });
    }

    [Fact]
    public async Task TimeoutChangedWhileEmpty_IsUsedByNextPacket()
    {
        await WithPipelineAsync(5000, async (pipeline, input, token) =>
        {
            await Task.Delay(50, token);
            pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, 40);
            await input.WriteAsync(new ReceivedByteChunk([0xA1], Stopwatch.GetTimestamp()), token);
            Assert.Equal(new byte[] { 0xA1 },
                await ReadGroupAsync(pipeline, token).WaitAsync(TimeSpan.FromSeconds(1), token));
        });
    }

    [Fact]
    public async Task NewBytesBeforeDeadline_RestartIdleWaitWithoutSplittingPacket()
    {
        await WithPipelineAsync(1000, async (pipeline, input, token) =>
        {
            await input.WriteAsync(new ReceivedByteChunk([0xA1], Stopwatch.GetTimestamp()), token);
            await WaitForAcceptedAsync(pipeline, token);
            await Task.Delay(550, token);
            await input.WriteAsync(new ReceivedByteChunk([0xA2], Stopwatch.GetTimestamp()), token);
            await Task.Delay(550, token);
            Assert.False(pipeline.Logs.TryPeek(out _));
            Assert.Equal(new byte[] { 0xA1, 0xA2 },
                await ReadGroupAsync(pipeline, token).WaitAsync(TimeSpan.FromSeconds(2), token));
        });
    }

    [Fact]
    public async Task ModeSwitchWithPendingBytes_FinalizesOldGroupAndUsesNewTimeout()
    {
        await WithPipelineAsync(5000, async (pipeline, input, token) =>
        {
            await input.WriteAsync(new ReceivedByteChunk([0xA1], Stopwatch.GetTimestamp()), token);
            await WaitForAcceptedAsync(pipeline, token);
            pipeline.ConfigureRxDisplay(RxDisplayMode.Terminal, 5000);
            Assert.Equal(new byte[] { 0xA1 },
                await ReadGroupAsync(pipeline, token).WaitAsync(TimeSpan.FromSeconds(1), token));
            pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, 40);
            await input.WriteAsync(new ReceivedByteChunk([0xA2], Stopwatch.GetTimestamp()), token);
            Assert.Equal(new byte[] { 0xA2 },
                await ReadGroupAsync(pipeline, token).WaitAsync(TimeSpan.FromSeconds(1), token));
        });
    }

    private static async Task WaitForAcceptedAsync(LogPipeline pipeline, CancellationToken token)
    {
        while (pipeline.HexPendingByteCount == 0)
            await Task.Delay(5, token);
        // Let the consumer enter its timeout wait before changing configuration.
        await Task.Delay(30, token);
    }

    private static async Task<byte[]> ReadGroupAsync(LogPipeline pipeline, CancellationToken token)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var line = await pipeline.Logs.ReadAsync(token);
            if (line.IsPartialRxTerminator)
                return bytes.ToArray();
            Assert.True(line.IsPartialRxSegment);
            bytes.AddRange(line.RawBytes!);
        }
    }

    private static async Task WithPipelineAsync(
        int timeoutMs,
        Func<LogPipeline, ChannelWriter<ReceivedByteChunk>, CancellationToken, Task> test)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var input = Channel.CreateUnbounded<ReceivedByteChunk>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, timeoutMs);
        await pipeline.StartAsync(input.Reader, new SerialSettings(), cancellation.Token);
        try
        {
            await test(pipeline, input.Writer, cancellation.Token);
            Assert.Equal(pipeline.HexAcceptedByteCount, pipeline.HexEmittedByteCount);
            Assert.Equal(0, pipeline.HexPendingByteCount);
        }
        finally
        {
            input.Writer.TryComplete();
            await pipeline.StopAsync(CancellationToken.None);
        }
    }
}
