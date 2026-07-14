using System.Text;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class BridgeLogProcessorTests
{
    [Fact]
    public async Task TerminalMode_DecodesTextAndActivatesOnlyTerminalTxRules()
    {
        await using var processor = new BridgeLogProcessor(TimeSpan.FromMilliseconds(10));
        var bytes = "ERROR"u8.ToArray();

        Assert.True(processor.TryEnqueue(bytes, RxDisplayMode.Terminal, RxEncodingMode.Utf8));
        var line = await ReadLineAsync(processor);

        Assert.Equal(LogDirection.Tx, line.Direction);
        Assert.Equal("[BRIDGE] ERROR", line.Text);
        Assert.Equal("[BRIDGE] ERROR", line.DisplayText);
        Assert.Equal(bytes, line.RawBytes);
        Assert.Equal(LogRuleMatchMode.Terminal, line.ContentMode);

        var terminalRule = LogRuleMatcher.Compile(new EventRule
        {
            Name = "terminal-tx",
            Keyword = "ERROR",
            Enabled = true,
            Mode = LogRuleMatchMode.Terminal,
            MatchDirection = EventMatchDirection.TxOnly
        });
        var hexRule = LogRuleMatcher.Compile(new EventRule
        {
            Name = "hex-tx",
            Keyword = "45 52 52 4F 52",
            Enabled = true,
            Mode = LogRuleMatchMode.Hex,
            MatchDirection = EventMatchDirection.TxOnly
        });

        Assert.True(LogRuleMatcher.IsMatch(line, terminalRule, LogRuleMatchMode.Terminal, out _));
        Assert.False(LogRuleMatcher.IsMatch(line, hexRule, LogRuleMatchMode.Terminal, out _));
    }

    [Fact]
    public async Task HexMode_FormatsBytesAndActivatesOnlyHexTxRules()
    {
        await using var processor = new BridgeLogProcessor(TimeSpan.FromMilliseconds(10));
        var bytes = "ERROR"u8.ToArray();

        Assert.True(processor.TryEnqueue(bytes, RxDisplayMode.Hex, RxEncodingMode.Utf8));
        var line = await ReadLineAsync(processor);

        Assert.Equal(LogDirection.Tx, line.Direction);
        Assert.Equal("[BRIDGE] 45 52 52 4F 52", line.Text);
        Assert.Equal(bytes, line.RawBytes);
        Assert.Equal(LogRuleMatchMode.Hex, line.ContentMode);

        var terminalRule = LogRuleMatcher.Compile(new EventRule
        {
            Keyword = "ERROR",
            Enabled = true,
            Mode = LogRuleMatchMode.Terminal,
            MatchDirection = EventMatchDirection.TxOnly
        });
        var hexRule = LogRuleMatcher.Compile(new EventRule
        {
            Keyword = "45 52 52 4F 52",
            Enabled = true,
            Mode = LogRuleMatchMode.Hex,
            MatchDirection = EventMatchDirection.TxOnly
        });

        Assert.False(LogRuleMatcher.IsMatch(line, terminalRule, LogRuleMatchMode.Hex, out _));
        Assert.True(LogRuleMatcher.IsMatch(line, hexRule, LogRuleMatchMode.Hex, out _));
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

        Assert.Equal("[BRIDGE] 가ERROR나", line.Text);
        Assert.Equal(bytes, line.RawBytes);
        var rule = LogRuleMatcher.Compile(new EventRule
        {
            Keyword = "가ERROR나",
            Enabled = true,
            Mode = LogRuleMatchMode.Terminal,
            MatchDirection = EventMatchDirection.TxOnly
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
        Assert.Equal($"[BRIDGE] {expected.Text}", line.Text);
        Assert.True(processor.DecodeErrorCount > 0);
    }

    private static async Task<LogLine> ReadLineAsync(IBridgeLogProcessor processor)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await processor.Logs.ReadAsync(timeout.Token);
    }
}
