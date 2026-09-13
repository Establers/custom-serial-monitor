using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

// Regression coverage for the partial-flush fix and the intentional trigger queue policy.
public sealed class SequenceTriggerAuditTests
{
    [Fact]
    public async Task KeywordAcrossTerminalPartialFlush_TriggersOnce()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var input = Channel.CreateUnbounded<ReceivedByteChunk>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        await using var detector = new EventDetector();
        await detector.StartAsync(
            [new EventRule { Keyword = "FAULT", TriggerSequenceName = "Recover", ShowInEventList = false }],
            new EventContextSettings(), timeout.Token);
        await pipeline.StartAsync(input.Reader, new SerialSettings(), timeout.Token);
        try
        {
            await input.Writer.WriteAsync(new ReceivedByteChunk(Encoding.ASCII.GetBytes("FA"), Stopwatch.GetTimestamp()), timeout.Token);
            // Wait for the actual partial flush, without relying on a guessed sleep.
            var first = await pipeline.Logs.ReadAsync(timeout.Token);
            Assert.True(first.IsPartialRxSegment);
            Assert.Equal("FA", first.Text);
            Assert.True(detector.TryEnqueue(first));

            await input.Writer.WriteAsync(new ReceivedByteChunk(Encoding.ASCII.GetBytes("ULT\n"), Stopwatch.GetTimestamp()), timeout.Token);
            var second = await pipeline.Logs.ReadAsync(timeout.Token);
            Assert.Equal("FAULT", first.Text + second.Text);
            Assert.True(detector.TryEnqueue(second));
            await detector.StopAsync(timeout.Token);
            Assert.Equal(1, detector.DetectedEventCount);
            Assert.True(detector.SequenceTriggerEvents.TryRead(out var trigger));
            Assert.Equal("Recover", trigger.TriggerSequenceName);
            Assert.Equal("FAULT", trigger.Message);
            Assert.False(detector.SequenceTriggerEvents.TryRead(out _));
        }
        finally
        {
            input.Writer.TryComplete();
            await pipeline.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DifferentTriggersInOneLine_SecondRequestIsLostWhenConsumerHasNotReadFirst()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var detector = new EventDetector();
        await detector.StartAsync(
            [
                new EventRule { Keyword = "FAULT", TriggerSequenceName = "RecoverA", ShowInEventList = false },
                new EventRule { Keyword = "FAULT", TriggerSequenceName = "RecoverB", ShowInEventList = false }
            ], new EventContextSettings(), timeout.Token);
        Assert.True(detector.TryEnqueue(LogLine.Rx("FAULT")));
        await detector.StopAsync(timeout.Token);
        Assert.Equal(2, detector.DetectedEventCount);
        Assert.Equal(1, detector.CoalescedSequenceTriggerCount);
        Assert.True(detector.SequenceTriggerEvents.TryRead(out var trigger));
        Assert.Equal("RecoverA", trigger.TriggerSequenceName);
        Assert.False(detector.SequenceTriggerEvents.TryRead(out _));
    }
}
