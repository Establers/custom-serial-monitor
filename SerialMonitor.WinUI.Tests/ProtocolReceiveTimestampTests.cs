using System.Diagnostics;
using System.Threading.Channels;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class ProtocolReceiveTimestampTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedSplitHexGroups_UseSameFirstChunkTimeAsProtocolFrames(bool nativeBoundary)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = Convert.FromHexString("010300000002C40B");
        var ticks = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2;
        var chunks = new[]
        {
            new ReceivedByteChunk(request[..3], ticks),
            new ReceivedByteChunk(request[3..], ticks + Stopwatch.Frequency / 1000, nativeBoundary),
            new ReceivedByteChunk(request, ticks + Stopwatch.Frequency / 10, nativeBoundary)
        };
        var input = Channel.CreateUnbounded<ReceivedByteChunk>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, hexGroupTimeoutMs: 10);
        await using var analyzer = new ModbusRtuAnalyzer();
        analyzer.SetEnabled(true);
        foreach (var chunk in chunks)
        {
            Assert.True(analyzer.TryEnqueue(chunk.Bytes, ProtocolDirection.Rx, 1, chunk.ReceivedAt,
                new ProtocolInputTiming(chunk.ReceivedTimestamp, chunk.EndsAtNativeIdleBoundary, 10, 4)));
            await input.Writer.WriteAsync(chunk, timeout.Token);
        }
        input.Writer.TryComplete();
        await pipeline.StartAsync(input.Reader, new SerialSettings { Encoding = RxEncodingMode.Hex }, timeout.Token);
        var lines = new List<LogLine>();
        await foreach (var line in pipeline.Logs.ReadAllAsync(timeout.Token))
            if (line.RawBytes is { Length: > 0 }) lines.Add(line);
        await pipeline.StopAsync(timeout.Token);
        while (analyzer.Snapshot().TotalFrames < 2) await Task.Delay(5, timeout.Token);

        var frames = analyzer.Snapshot().Frames!;
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.Equal(request, line.RawBytes));
        Assert.Equal(new[] { chunks[0].ReceivedAt, chunks[2].ReceivedAt }, lines.Select(line => line.Timestamp));
        Assert.Equal(frames.Select(frame => frame.Timestamp), lines.Select(line => line.Timestamp));
        Assert.All(lines, line => Assert.True(line.Timestamp < DateTimeOffset.Now.AddSeconds(-1)));
    }

    [Fact]
    public async Task ParserRouting_PreservesRawObserversExactTimestamp()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var serial = new SerialService();
        var observed = new TaskCompletionSource<ReceivedByteChunk>(TaskCreationOptions.RunContinuationsAsynchronously);
        serial.CaptureBytesReceived += (_, chunk) => observed.TrySetResult(chunk);
        await serial.ConnectAsync(new SerialSettings { PortName = "MOCK" }, new SerialReceiveOptions(), timeout.Token);
        try
        {
            var raw = await observed.Task.WaitAsync(timeout.Token);
            var parser = await serial.ReceivedBytes.ReadAsync(timeout.Token);
            Assert.Same(raw.Bytes, parser.Bytes);
            Assert.Equal(raw.ReceivedTimestamp, parser.ReceivedTimestamp);
            Assert.Equal(raw.ReceivedAt, parser.ReceivedAt);
        }
        finally { await serial.DisconnectAsync(timeout.Token); }
    }
}
