using System.Diagnostics;
using System.Threading.Channels;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogPipelineHexFramingTests
{
    [Theory]
    [InlineData(2, 4, 220, 12002)]
    [InlineData(10, 30, 180, 12010)]
    [InlineData(100, 300, 120, 12100)]
    [InlineData(500, 1200, 80, 12500)]
    public async Task VariablePackets_WithSubTimeoutTransportChunkDelays_RoundTripExactly(
        int timeoutMs,
        int packetGapMs,
        int packetCount,
        int seed)
    {
        var random = new Random(seed);
        var packets = Enumerable.Range(0, packetCount)
            .Select(packetIndex => CreatePacket(packetIndex, random.Next(1, 4_097)))
            .ToArray();
        var chunks = CreateTimedChunks(packets, timeoutMs, packetGapMs, seed);

        var result = await RunPipelineAsync(chunks, timeoutMs);

        Assert.Equal(packets.Length, result.Groups.Count);
        for (var index = 0; index < packets.Length; index++)
        {
            Assert.Equal(packets[index], result.Groups[index]);
        }

        var expectedByteCount = packets.Sum(packet => (long)packet.Length);
        Assert.Equal(expectedByteCount, result.ProcessedByteCount);
        Assert.Equal(expectedByteCount, result.AcceptedByteCount);
        Assert.Equal(expectedByteCount, result.EmittedByteCount);
        Assert.Equal(0, result.PendingByteCount);
    }

    [Fact]
    public async Task PacketLargerThanStreamingSegment_RemainsOneLogicalHexLine()
    {
        var packets = new[]
        {
            CreatePacket(1, 200_000),
            CreatePacket(2, 65_537),
            CreatePacket(3, 1)
        };
        var chunks = CreateTimedChunks(packets, timeoutMs: 10, packetGapMs: 25, seed: 64000);

        var result = await RunPipelineAsync(chunks, timeoutMs: 10);

        Assert.Equal(3, result.Groups.Count);
        Assert.Equal(packets[0], result.Groups[0]);
        Assert.Equal(packets[1], result.Groups[1]);
        Assert.Equal(packets[2], result.Groups[2]);
        Assert.Equal(packets.Sum(packet => (long)packet.Length), result.EmittedByteCount);
    }

    [Fact]
    public async Task GapExactlyEqualToConfiguredTimeout_ClosesPreviousPacket()
    {
        var timeout = TimeSpan.FromMilliseconds(37);
        var firstTimestamp = Timestamp(TimeSpan.FromMilliseconds(1));
        var chunks = new[]
        {
            new ReceivedByteChunk(new byte[] { 0x01, 0x02 }, firstTimestamp),
            new ReceivedByteChunk(new byte[] { 0x03 }, firstTimestamp + Timestamp(timeout))
        };

        var result = await RunPipelineAsync(chunks, timeoutMs: 37);

        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(new byte[] { 0x01, 0x02 }, result.Groups[0]);
        Assert.Equal(new byte[] { 0x03 }, result.Groups[1]);
    }

    [Fact]
    public async Task NativeIdleBoundary_DoesNotWaitForProfileTimeoutAgain()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var input = Channel.CreateUnbounded<ReceivedByteChunk>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, hexGroupTimeoutMs: 2_000);
        await pipeline.StartAsync(
            input.Reader,
            new SerialSettings { Encoding = RxEncodingMode.Hex },
            cancellation.Token);

        var packet = CreatePacket(packetIndex: 17, length: 11);
        var groupTask = ReadNextHexGroupAsync(pipeline.Logs, cancellation.Token);
        var stopwatch = Stopwatch.StartNew();
        await input.Writer.WriteAsync(
            new ReceivedByteChunk(
                packet,
                Stopwatch.GetTimestamp(),
                endsAtNativeIdleBoundary: true),
            cancellation.Token);

        var group = await groupTask.WaitAsync(TimeSpan.FromMilliseconds(750), cancellation.Token);
        stopwatch.Stop();

        Assert.Equal(packet, group);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(750),
            $"Native idle boundary took {stopwatch.Elapsed.TotalMilliseconds:N1} ms to flush.");

        input.Writer.TryComplete();
        await pipeline.StopAsync(cancellation.Token);
    }

    [Fact]
    public async Task ChunkWithoutNativeIdleBoundary_RetainsApplicationTimeoutFallback()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var input = Channel.CreateUnbounded<ReceivedByteChunk>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, hexGroupTimeoutMs: 500);
        await pipeline.StartAsync(
            input.Reader,
            new SerialSettings { Encoding = RxEncodingMode.Hex },
            cancellation.Token);

        var packet = CreatePacket(packetIndex: 23, length: 73);
        var groupTask = ReadNextHexGroupAsync(pipeline.Logs, cancellation.Token);
        await input.Writer.WriteAsync(
            new ReceivedByteChunk(packet, Stopwatch.GetTimestamp()),
            cancellation.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token);
        Assert.False(groupTask.IsCompleted);

        input.Writer.TryComplete();
        var group = await groupTask.WaitAsync(TimeSpan.FromSeconds(1), cancellation.Token);
        Assert.Equal(packet, group);
        await pipeline.StopAsync(cancellation.Token);
    }

    [Fact]
    public async Task AccumulatedNativeCompletions_RemainSeparateHexGroups()
    {
        var packets = new[]
        {
            CreatePacket(packetIndex: 31, length: 11),
            CreatePacket(packetIndex: 32, length: 7),
            CreatePacket(packetIndex: 33, length: 129)
        };
        var managedBuffer = packets.SelectMany(packet => packet).ToArray();
        var tracker = new NativeReadBoundaryTracker(bufferCapacity: 1024 * 1024);
        var boundaries = new List<NativeReadBoundary>();
        var availableBytes = 0;

        for (var index = 0; index < packets.Length; index++)
        {
            availableBytes += packets[index].Length;
            boundaries.Add(tracker.ObserveCompletion(
                availableBytes,
                Timestamp(TimeSpan.FromMilliseconds(index + 1)),
                usesNativeIdleTimeout: true));
        }

        var chunks = new List<ReceivedByteChunk>();
        var offset = 0;
        foreach (var boundary in boundaries)
        {
            var bytes = managedBuffer.AsSpan(offset, boundary.ByteCount).ToArray();
            chunks.Add(new ReceivedByteChunk(
                bytes,
                boundary.CompletedTimestamp,
                boundary.EndsAtNativeIdleBoundary));
            offset += boundary.ByteCount;
            tracker.RecordConsumed(boundary.ByteCount);
        }

        var result = await RunPipelineAsync(chunks, timeoutMs: 2);

        Assert.Equal(packets.Length, result.Groups.Count);
        for (var index = 0; index < packets.Length; index++)
        {
            Assert.Equal(packets[index], result.Groups[index]);
        }
    }

    [Fact]
    public async Task HexDisplay_WithUtf8TerminalEncoding_EmitsByteExactTextForEventAndFileConsumers()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var input = Channel.CreateUnbounded<ReceivedByteChunk>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, hexGroupTimeoutMs: 10);
        await pipeline.StartAsync(
            input.Reader,
            new SerialSettings { Encoding = RxEncodingMode.Utf8 },
            cancellation.Token);

        var bytes = new byte[] { 0x00, 0x80, 0xFF, 0xFE, 0x3F };
        await input.Writer.WriteAsync(
            new ReceivedByteChunk(bytes, Stopwatch.GetTimestamp(), endsAtNativeIdleBoundary: true),
            cancellation.Token);

        var line = await pipeline.Logs.ReadAsync(cancellation.Token);
        Assert.Equal(bytes, line.RawBytes);
        Assert.Equal("\0????", line.Text);
        Assert.Equal("00 80 FF FE 3F", line.DisplayText);
        Assert.Contains("00 80 FF FE 3F", line.Formatted);
        Assert.DoesNotContain('\uFFFD', line.Formatted);

        input.Writer.TryComplete();
        await pipeline.StopAsync(cancellation.Token);
    }

    [Fact]
    public async Task HexMode_SkipsTerminalRuleEvenThoughDecodedTextIsRetained()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var input = Channel.CreateUnbounded<ReceivedByteChunk>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, hexGroupTimeoutMs: 10);
        await pipeline.StartAsync(
            input.Reader,
            new SerialSettings { Encoding = RxEncodingMode.Utf8 },
            cancellation.Token);

        var bytes = "ERROR"u8.ToArray();
        await input.Writer.WriteAsync(
            new ReceivedByteChunk(bytes, Stopwatch.GetTimestamp(), endsAtNativeIdleBoundary: true),
            cancellation.Token);

        var line = await pipeline.Logs.ReadAsync(cancellation.Token);
        Assert.Equal("ERROR", line.Text);
        Assert.Equal("45 52 52 4F 52", line.DisplayText);
        Assert.Equal(LogRuleMatchMode.Hex, line.ContentMode);
        Assert.Contains("45 52 52 4F 52", line.Formatted);

        var terminalRule = new EventRule
        {
            Enabled = true,
            Keyword = "ERROR",
            Mode = LogRuleMatchMode.Terminal
        };
        Assert.False(LogRuleMatcher.IsMatch(line, terminalRule, LogRuleMatchMode.Hex, out var error));
        Assert.Null(error);

        var hexRule = new EventRule
        {
            Enabled = true,
            Keyword = "45 52 52 4F 52",
            Mode = LogRuleMatchMode.Hex
        };
        Assert.True(LogRuleMatcher.IsMatch(line, hexRule, LogRuleMatchMode.Hex, out error));
        Assert.Null(error);

        input.Writer.TryComplete();
        await pipeline.StopAsync(cancellation.Token);
    }

    [Fact]
    public async Task SwitchingEmptyTerminalParserToHex_DoesNotEmitPhantomTerminator()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var input = Channel.CreateUnbounded<ReceivedByteChunk>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        var settings = new SerialSettings
        {
            Encoding = RxEncodingMode.Utf8,
            RxLineEnding = RxLineEndingMode.Lf
        };
        pipeline.ConfigureRxDisplay(RxDisplayMode.Terminal, hexGroupTimeoutMs: 10);
        await pipeline.StartAsync(input.Reader, settings, cancellation.Token);

        await input.Writer.WriteAsync(
            new ReceivedByteChunk("READY\n"u8.ToArray(), Stopwatch.GetTimestamp()),
            cancellation.Token);
        var terminalLine = await pipeline.Logs.ReadAsync(cancellation.Token);
        Assert.Equal("READY", terminalLine.Text);

        pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, hexGroupTimeoutMs: 10);
        var packet = new byte[] { 0xAA, 0x55 };
        await input.Writer.WriteAsync(
            new ReceivedByteChunk(packet, Stopwatch.GetTimestamp(), endsAtNativeIdleBoundary: true),
            cancellation.Token);

        var firstHexLine = await pipeline.Logs.ReadAsync(cancellation.Token);
        Assert.False(firstHexLine.IsPartialRxTerminator);
        Assert.Equal(packet, firstHexLine.RawBytes);
        Assert.Equal(LogRuleMatchMode.Hex, firstHexLine.ContentMode);

        input.Writer.TryComplete();
        await pipeline.StopAsync(cancellation.Token);
    }

    private static async Task<PipelineResult> RunPipelineAsync(
        IReadOnlyList<ReceivedByteChunk> chunks,
        int timeoutMs)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var input = Channel.CreateUnbounded<ReceivedByteChunk>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, timeoutMs);
        await pipeline.StartAsync(input.Reader, new SerialSettings { Encoding = RxEncodingMode.Hex }, cancellation.Token);

        foreach (var chunk in chunks)
        {
            await input.Writer.WriteAsync(chunk, cancellation.Token);
        }

        input.Writer.TryComplete();
        var groups = new List<byte[]>();
        var currentGroup = new List<byte>();
        await foreach (var line in pipeline.Logs.ReadAllAsync(cancellation.Token))
        {
            if (line.RawBytes is { Length: > 0 })
            {
                currentGroup.AddRange(line.RawBytes);
            }

            if (line.IsPartialRxTerminator)
            {
                groups.Add(currentGroup.ToArray());
                currentGroup.Clear();
            }
        }

        await pipeline.StopAsync(cancellation.Token);
        Assert.Empty(currentGroup);
        return new PipelineResult(
            groups,
            pipeline.ProcessedRxByteCount,
            pipeline.HexAcceptedByteCount,
            pipeline.HexEmittedByteCount,
            pipeline.HexPendingByteCount);
    }

    private static IReadOnlyList<ReceivedByteChunk> CreateTimedChunks(
        IReadOnlyList<byte[]> packets,
        int timeoutMs,
        int packetGapMs,
        int seed)
    {
        var random = new Random(seed);
        var result = new List<ReceivedByteChunk>();
        var timestamp = Timestamp(TimeSpan.FromMilliseconds(1));

        foreach (var packet in packets)
        {
            var offset = 0;
            while (offset < packet.Length)
            {
                var count = Math.Min(packet.Length - offset, random.Next(1, 513));
                var bytes = packet.AsSpan(offset, count).ToArray();
                result.Add(new ReceivedByteChunk(bytes, timestamp));
                offset += count;

                var maxTransportDelay = Math.Max(0.05, timeoutMs - 0.05);
                var transportDelay = 0.01 + random.NextDouble() * (maxTransportDelay - 0.01);
                timestamp += Timestamp(TimeSpan.FromMilliseconds(transportDelay));
            }

            timestamp += Timestamp(TimeSpan.FromMilliseconds(packetGapMs));
        }

        return result;
    }

    private static byte[] CreatePacket(int packetIndex, int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)((packetIndex * 31 + index * 17) & 0xFF);
        }

        return bytes;
    }

    private static async Task<byte[]> ReadNextHexGroupAsync(
        ChannelReader<LogLine> logs,
        CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        await foreach (var line in logs.ReadAllAsync(cancellationToken))
        {
            if (line.RawBytes is { Length: > 0 })
            {
                bytes.AddRange(line.RawBytes);
            }

            if (line.IsPartialRxTerminator)
            {
                return bytes.ToArray();
            }
        }

        throw new InvalidOperationException("The log channel completed before a HEX group terminator was emitted.");
    }

    private static long Timestamp(TimeSpan duration) =>
        (long)Math.Round(duration.TotalSeconds * Stopwatch.Frequency, MidpointRounding.AwayFromZero);

    private sealed record PipelineResult(
        IReadOnlyList<byte[]> Groups,
        long ProcessedByteCount,
        long AcceptedByteCount,
        long EmittedByteCount,
        int PendingByteCount);
}
