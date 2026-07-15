using System.Text;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class BridgeLogProcessorTests
{
    public static TheoryData<int[]> HexChunkSplits => new()
    {
        new[] { 2, 4 },
        new[] { 3, 3 },
        new[] { 4, 2 },
        new[] { 1, 1, 1, 1, 1, 1 },
        new[] { 1, 2, 1, 2 }
    };

    [Fact]
    public async Task TerminalMode_DecodesTextWithoutBridgePrefixAndActivatesOnlyTerminalRxRules()
    {
        await using var processor = new BridgeLogProcessor(TimeSpan.FromMilliseconds(10));
        var bytes = "ERROR"u8.ToArray();

        Assert.True(processor.TryEnqueue(bytes, RxDisplayMode.Terminal, RxEncodingMode.Utf8));
        var line = await ReadLineAsync(processor);

        Assert.Equal(LogDirection.Rx, line.Direction);
        Assert.Equal("ERROR", line.Text);
        Assert.Equal("ERROR", line.DisplayText);
        Assert.Equal(bytes, line.RawBytes);
        Assert.Equal(LogRuleMatchMode.Terminal, line.ContentMode);

        var terminalRule = LogRuleMatcher.Compile(new EventRule
        {
            Name = "terminal-rx",
            Keyword = "ERROR",
            Enabled = true,
            Mode = LogRuleMatchMode.Terminal,
            MatchDirection = EventMatchDirection.RxOnly
        });
        var hexRule = LogRuleMatcher.Compile(new EventRule
        {
            Name = "hex-rx",
            Keyword = "45 52 52 4F 52",
            Enabled = true,
            Mode = LogRuleMatchMode.Hex,
            MatchDirection = EventMatchDirection.RxOnly
        });

        Assert.True(LogRuleMatcher.IsMatch(line, terminalRule, LogRuleMatchMode.Terminal, out _));
        Assert.False(LogRuleMatcher.IsMatch(line, hexRule, LogRuleMatchMode.Terminal, out _));
    }

    [Fact]
    public async Task HexMode_FormatsBytesWithoutBridgePrefixAndActivatesOnlyHexRxRules()
    {
        await using var processor = new BridgeLogProcessor(TimeSpan.FromMilliseconds(10));
        var bytes = "ERROR"u8.ToArray();

        Assert.True(processor.TryEnqueue(bytes, RxDisplayMode.Hex, RxEncodingMode.Utf8));
        var line = await ReadLineAsync(processor);

        Assert.Equal(LogDirection.Rx, line.Direction);
        Assert.Equal("45 52 52 4F 52", line.Text);
        Assert.Equal(bytes, line.RawBytes);
        Assert.Equal(LogRuleMatchMode.Hex, line.ContentMode);

        var terminalRule = LogRuleMatcher.Compile(new EventRule
        {
            Keyword = "ERROR",
            Enabled = true,
            Mode = LogRuleMatchMode.Terminal,
            MatchDirection = EventMatchDirection.RxOnly
        });
        var hexRule = LogRuleMatcher.Compile(new EventRule
        {
            Keyword = "45 52 52 4F 52",
            Enabled = true,
            Mode = LogRuleMatchMode.Hex,
            MatchDirection = EventMatchDirection.RxOnly
        });

        Assert.False(LogRuleMatcher.IsMatch(line, terminalRule, LogRuleMatchMode.Hex, out _));
        Assert.True(LogRuleMatcher.IsMatch(line, hexRule, LogRuleMatchMode.Hex, out _));
    }

    [Theory]
    [MemberData(nameof(HexChunkSplits))]
    public async Task HexMode_GroupsReadChunksIntoOneLogLine_AndMatchesWithinThatRecord(int[] chunkLengths)
    {
        await using var processor = new BridgeLogProcessor(TimeSpan.FromMilliseconds(15));
        var bytes = new byte[] { 0x10, 0xDE, 0xAD, 0xBE, 0xEF, 0x20 };
        var offset = 0;
        foreach (var length in chunkLengths)
        {
            Assert.True(processor.TryEnqueue(
                bytes.AsSpan(offset, length).ToArray(),
                RxDisplayMode.Hex,
                RxEncodingMode.Utf8));
            offset += length;
        }

        Assert.Equal(bytes.Length, offset);
        var line = await ReadLineAsync(processor);
        var rule = LogRuleMatcher.Compile(new EventRule
        {
            Name = "split-hex",
            Keyword = "DE AD BE EF",
            Enabled = true,
            Mode = LogRuleMatchMode.Hex,
            MatchDirection = EventMatchDirection.RxOnly
        });

        Assert.Equal(bytes, line.RawBytes);
        Assert.True(LogRuleMatcher.IsMatch(line, rule, LogRuleMatchMode.Hex, out _));
        await Task.Delay(40);
        Assert.False(processor.Logs.TryRead(out _));
    }

    [Fact]
    public async Task HexMode_ResetStream_DiscardsAFormerPartialPattern()
    {
        await using var processor = new BridgeLogProcessor(TimeSpan.FromMilliseconds(15));
        Assert.True(processor.TryEnqueue(
            new byte[] { 0xDE, 0xAD },
            RxDisplayMode.Hex,
            RxEncodingMode.Utf8));

        processor.ResetStream();
        Assert.True(processor.TryEnqueue(
            new byte[] { 0xBE, 0xEF },
            RxDisplayMode.Hex,
            RxEncodingMode.Utf8));
        var line = await ReadLineAsync(processor);
        var rule = LogRuleMatcher.Compile(new EventRule
        {
            Keyword = "DE AD BE EF",
            Enabled = true,
            Mode = LogRuleMatchMode.Hex,
            MatchDirection = EventMatchDirection.RxOnly
        });

        Assert.Equal(new byte[] { 0xBE, 0xEF }, line.RawBytes);
        Assert.False(LogRuleMatcher.IsMatch(line, rule, LogRuleMatchMode.Hex, out _));
    }

    [Fact]
    public async Task HexMode_OutputDrop_ResetsGroupingBeforeTheNextAcceptedLine()
    {
        await using var processor = new BridgeLogProcessor(
            TimeSpan.FromMilliseconds(10),
            inputQueueCapacity: 8,
            outputQueueCapacity: 1);
        Assert.True(processor.TryEnqueue(new byte[] { 0x01 }, RxDisplayMode.Hex, RxEncodingMode.Utf8));
        await WaitUntilAsync(() => processor.Logs.Count == 1);

        Assert.True(processor.TryEnqueue(new byte[] { 0xDE, 0xAD }, RxDisplayMode.Hex, RxEncodingMode.Utf8));
        await WaitUntilAsync(() => processor.DroppedOutputLineCount == 1);
        Assert.True(processor.Logs.TryRead(out _));

        Assert.True(processor.TryEnqueue(new byte[] { 0xBE, 0xEF }, RxDisplayMode.Hex, RxEncodingMode.Utf8));
        var accepted = await ReadLineAsync(processor);
        Assert.Equal(new byte[] { 0xBE, 0xEF }, accepted.RawBytes);
    }

    [Fact]
    public async Task HexMode_ContinuousStream_EmitsByByteCapWithoutWaitingForIdle()
    {
        await using var processor = new BridgeLogProcessor(TimeSpan.FromMilliseconds(25));
        var bytes = new byte[BridgeLogProcessor.MaxHexLogBytes * 8];
        new Random(384009600).NextBytes(bytes);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        Assert.True(processor.TryEnqueue(bytes, RxDisplayMode.Hex, RxEncodingMode.Utf8));
        var received = new List<byte>(bytes.Length);
        while (received.Count < bytes.Length)
        {
            var line = await ReadLineAsync(processor);
            Assert.NotNull(line.RawBytes);
            Assert.InRange(line.RawBytes!.Length, 1, BridgeLogProcessor.MaxHexLogBytes);
            received.AddRange(line.RawBytes);
            if (received.Count == line.RawBytes.Length)
            {
                Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(500));
            }
        }

        Assert.Equal(bytes, received.ToArray());
    }

    [Fact]
    public async Task HexMode_ContinuousTrickle_EmitsByMaximumLatency()
    {
        await using var processor = new BridgeLogProcessor(TimeSpan.FromMilliseconds(25));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var producer = Task.Run(async () =>
        {
            for (var value = 0; value < 20; value++)
            {
                Assert.True(processor.TryEnqueue(
                    new[] { (byte)value },
                    RxDisplayMode.Hex,
                    RxEncodingMode.Utf8));
                await Task.Delay(10, timeout.Token);
            }
        }, timeout.Token);

        var first = await processor.Logs.ReadAsync(timeout.Token);
        Assert.InRange(first.RawBytes!.Length, 1, 10);
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(250));
        await producer;
    }

    [Fact]
    public async Task HexMode_SlowOutputQueue_RetainsAtMostCapacityTimesByteCap()
    {
        const int outputCapacity = 4;
        await using var processor = new BridgeLogProcessor(
            TimeSpan.FromMilliseconds(10),
            inputQueueCapacity: 8,
            outputQueueCapacity: outputCapacity);
        Assert.True(processor.TryEnqueue(
            new byte[BridgeLogProcessor.MaxHexLogBytes * 16],
            RxDisplayMode.Hex,
            RxEncodingMode.Utf8));
        await WaitUntilAsync(() => processor.DroppedOutputLineCount > 0);

        var retainedRawBytes = 0;
        while (processor.Logs.TryRead(out var line))
        {
            retainedRawBytes += line.RawBytes?.Length ?? 0;
        }

        Assert.InRange(
            retainedRawBytes,
            0,
            outputCapacity * BridgeLogProcessor.MaxHexLogBytes);
    }

    [Theory]
    [InlineData(RxEncodingMode.Utf8)]
    [InlineData(RxEncodingMode.Cp949)]
    public async Task TerminalMode_PreservesMultibyteCharactersAndKeywordsAcrossChunks(RxEncodingMode encodingMode)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        await using var processor = new BridgeLogProcessor(TimeSpan.FromMilliseconds(20));
        var encoding = encodingMode == RxEncodingMode.Cp949
            ? Encoding.GetEncoding(949)
            : Encoding.UTF8;
        var bytes = encoding.GetBytes("가ERROR나");

        Assert.True(processor.TryEnqueue(bytes[..1], RxDisplayMode.Terminal, encodingMode));
        Assert.True(processor.TryEnqueue(bytes[1..4], RxDisplayMode.Terminal, encodingMode));
        Assert.True(processor.TryEnqueue(bytes[4..], RxDisplayMode.Terminal, encodingMode));
        var line = await ReadLineAsync(processor);

        Assert.Equal("가ERROR나", line.Text);
        Assert.Equal(bytes, line.RawBytes);
        var rule = LogRuleMatcher.Compile(new EventRule
        {
            Keyword = "가ERROR나",
            Enabled = true,
            Mode = LogRuleMatchMode.Terminal,
            MatchDirection = EventMatchDirection.RxOnly
        });
        Assert.True(LogRuleMatcher.IsMatch(line, rule, LogRuleMatchMode.Terminal, out _));
    }

    [Theory]
    [InlineData(RxEncodingMode.Utf8, 0xFF)]
    [InlineData(RxEncodingMode.Cp949, 0x81)]
    [InlineData(RxEncodingMode.Ascii, 0xFF)]
    public async Task TerminalMode_UsesTheSameReplacementPolicyAsTerminalRxDecoder(
        RxEncodingMode encodingMode,
        byte invalidByte)
    {
        await using var processor = new BridgeLogProcessor(TimeSpan.FromMilliseconds(10));
        var invalidBytes = new[] { invalidByte };
        var expected = new EncodingDecoder().Decode(invalidBytes, encodingMode);

        Assert.True(processor.TryEnqueue(invalidBytes, RxDisplayMode.Terminal, encodingMode));
        var line = await ReadLineAsync(processor);

        Assert.True(expected.HadError);
        Assert.Equal(expected.Text, line.Text);
        Assert.True(processor.DecodeErrorCount > 0);
    }

    private static async Task<LogLine> ReadLineAsync(IBridgeLogProcessor processor)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await processor.Logs.ReadAsync(timeout.Token);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }
}
