using System.Text;
using System.Threading.Channels;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogPipelineDrainTests
{
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
