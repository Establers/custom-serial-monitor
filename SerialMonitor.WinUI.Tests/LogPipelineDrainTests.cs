using System.Text;
using System.Threading.Channels;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogPipelineDrainTests
{
    [Fact]
    public async Task SerialDisconnect_PreservesAcceptedRxForDelayedPipelineDrainToDisk()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"SerialDisconnectDrain_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var serial = new SerialService();
            await using var writer = new FileLogWriter();
            var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
            var settings = new SerialSettings { PortName = "MOCK" };
            await serial.ConnectAsync(settings, new SerialReceiveOptions(), timeout.Token);
            var source = serial.ReceivedBytes;
            var expected = Enumerable.Range(0, 100).Select(index => $"disconnect-packet-{index:D3}").ToArray();
            foreach (var packet in expected)
            {
                await serial.SendBytesAsync(Encoding.UTF8.GetBytes(packet), packet, timeout.Token);
            }

            // Keep the consumer paused until disconnect has finished so the full
            // accepted backlog must survive producer cleanup.
            await serial.DisconnectAsync(timeout.Token);
            Assert.Equal(SerialConnectionState.Disconnected, serial.ConnectionState);

            await writer.StartAsync(directory, timeout.Token);
            await pipeline.StartAsync(source, settings, timeout.Token);
            try
            {
                var consumer = Task.Run(async () =>
                {
                    await foreach (var line in pipeline.Logs.ReadAllAsync(timeout.Token))
                    {
                        Assert.True(writer.TryEnqueue(line));
                    }
                });
                await pipeline.DrainAsync(timeout.Token);
                await consumer.WaitAsync(timeout.Token);
                await source.Completion.WaitAsync(timeout.Token);
                await writer.StopAsync(timeout.Token);

                var saved = await File.ReadAllLinesAsync(writer.LastLogFilePath!, timeout.Token);
                var packets = saved.Where(line => line.Contains("disconnect-packet-", StringComparison.Ordinal)).ToArray();
                Assert.Equal(expected.Length, packets.Length);
                for (var index = 0; index < expected.Length; index++)
                {
                    Assert.EndsWith(expected[index], packets[index]);
                }
                Assert.Equal(0, serial.ConnectionErrorCount);
                Assert.Equal(0, pipeline.DecodeErrorCount);
                Assert.Equal(0, writer.DroppedLineCount);
                Assert.Equal(0, writer.AbandonedLineCount);
                Assert.Equal(0, writer.FileErrorCount);
            }
            finally
            {
                await pipeline.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ClosedProducer_DrainsQueuedPacketsAndUnterminatedTailToDisk()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"SerialDrain_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var writer = new FileLogWriter();
            await writer.StartAsync(directory, CancellationToken.None);
            var source = Channel.CreateUnbounded<ReceivedByteChunk>();
            var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
            await pipeline.StartAsync(source.Reader, new SerialSettings(), CancellationToken.None);
            var consumer = Task.Run(async () =>
            {
                await foreach (var line in pipeline.Logs.ReadAllAsync())
                {
                    Assert.True(writer.TryEnqueue(line));
                }
            });
            for (var index = 0; index < 5_000; index++)
            {
                Assert.True(source.Writer.TryWrite(ReceivedByteChunk.Capture(Encoding.UTF8.GetBytes($"packet-{index:D6}\n"))));
            }
            Assert.True(source.Writer.TryWrite(ReceivedByteChunk.Capture("final-tail"u8.ToArray())));
            source.Writer.TryComplete();
            await pipeline.DrainAsync(CancellationToken.None);
            await consumer.WaitAsync(TimeSpan.FromSeconds(5));
            await writer.StopAsync(CancellationToken.None);
            var saved = await File.ReadAllLinesAsync(writer.LastLogFilePath!);
            Assert.Equal(5_001, saved.Length);
            for (var index = 0; index < 5_000; index++)
            {
                Assert.EndsWith($"packet-{index:D6}", saved[index]);
            }
            Assert.EndsWith("final-tail", saved[^1]);
            Assert.Equal(0, writer.AbandonedLineCount);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
