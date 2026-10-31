using System.Reflection;
using System.Text;
using System.Threading.Channels;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ReceiveGapRegressionTests
{
    [Fact]
    public async Task DisconnectCancelsAMockEchoBlockedOnAFullParserChannelWithoutACallerToken()
    {
        await using var serial = new SerialService();
        var nested = typeof(SerialService).GetNestedType("ReceiveSession", BindingFlags.NonPublic)!;
        var session = Activator.CreateInstance(nested, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, [1L, true, CancellationToken.None], null)!;
        typeof(SerialService).GetMethod("SetCurrentReceiveSession", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(serial, [session]);
        typeof(SerialService).GetField("_connectionState", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(serial, SerialConnectionState.Connecting);
        typeof(SerialService).GetMethod("SetConnectedIfCurrentSession", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(serial, [session]);
        var channel = (Channel<ReceivedByteChunk>)nested.GetProperty("Channel")!.GetValue(session)!;
        for (var i = 0; i < 4096; i++) Assert.True(channel.Writer.TryWrite(ReceivedByteChunk.Capture("x"u8.ToArray())));
        var sending = serial.SendBytesAsync("command"u8.ToArray(), "echo", CancellationToken.None);
        Assert.False(sending.IsCompleted);
        var disconnecting = serial.DisconnectAsync(default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(3)));
        await disconnecting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(SerialConnectionState.Disconnected, serial.ConnectionState);
    }

    [Theory]
    [InlineData(RxDisplayMode.Terminal, true)]
    [InlineData(RxDisplayMode.Hex, true)]
    [InlineData(RxDisplayMode.Terminal, false)]
    [InlineData(RxDisplayMode.Hex, false)]
    public async Task ParserOverflowPreservesRawObserversAndCannotCreateAKeywordAcrossMissingBytes(RxDisplayMode mode, bool overflow)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        await using var serial = new SerialService();
        var nested = typeof(SerialService).GetNestedType("ReceiveSession", BindingFlags.NonPublic)!;
        var session = Activator.CreateInstance(nested, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, [1L, true, CancellationToken.None], null)!;
        typeof(SerialService).GetMethod("SetCurrentReceiveSession", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(serial, [session]);
        serial.SetRawBridgePriorityEnabled(true);
        var publish = typeof(SerialService).GetMethod("PublishReceivedChunkAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var channel = (Channel<ReceivedByteChunk>)nested.GetProperty("Channel")!.GetValue(session)!;
        var captured = new StringBuilder(); var forwarded = new StringBuilder();
        serial.CaptureBytesReceived += (_, chunk) => { Assert.Null(chunk.StreamOffset); captured.Append(Encoding.UTF8.GetString(chunk.Bytes)); };
        serial.RawBytesReceived += chunk => forwarded.Append(Encoding.UTF8.GetString(chunk.Bytes));
        async Task Send(string text) => await (ValueTask)publish.Invoke(serial,
            [session, ReceivedByteChunk.Capture(Encoding.UTF8.GetBytes(text)), token, true])!;
        for (var i = 0; i < 4095; i++) await Send("x");
        await Send("FA");
        if (overflow) await Send("X");
        var kind = mode == RxDisplayMode.Hex ? LogRuleMatchMode.Hex : LogRuleMatchMode.Terminal;
        var pattern = kind == LogRuleMatchMode.Hex ? "46 41 55 4C 54" : "FAULT";
        await using var detector = new EventDetector();
        detector.UpdateRuleMode(kind);
        await detector.StartAsync([new EventRule { Name = "must-not-match", Keyword = pattern, Mode = kind,
            ShowInEventList = false, TriggerSequenceName = "must-not-send" }], new(), token);
        var log = new LogViewModel(10_000);
        log.SetRxDisplayMode(mode);
        log.SetHighlightRules([new HighlightRule { Keyword = pattern, Mode = kind, Enabled = true }]);
        var lines = new List<LogLine>(); var errors = new List<string>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        pipeline.ConfigureRxDisplay(mode, 5000);
        pipeline.Error += (_, error) => errors.Add(error);
        await pipeline.StartAsync(channel.Reader, new(), token);
        var consumer = Task.Run(async () =>
        {
            await foreach (var line in pipeline.Logs.ReadAllAsync(token))
            { Assert.True(detector.TryEnqueue(line)); lines.Add(line); log.AddRange([line]); }
        }, token);
        while (channel.Reader.Count >= 4096) await Task.Delay(1, token);
        if (!overflow) await Send("X");
        while (channel.Reader.Count >= 4096) await Task.Delay(1, token);
        await Send("ULT");
        channel.Writer.TryComplete();
        await pipeline.DrainAsync(token); await consumer; await detector.StopAsync(token);
        Assert.EndsWith("FAXULT", captured.ToString());
        Assert.Equal(captured.ToString(), forwarded.ToString());
        Assert.Equal(overflow ? 1 : 0, serial.BridgePriorityDroppedPipelineByteCount);
        Assert.Equal(0, detector.DetectedEventCount);
        Assert.False(detector.SequenceTriggerEvents.TryRead(out _));
        Assert.Equal(0, log.HighlightedLineCount);
        Assert.Equal(overflow ? 1 : 0, errors.Count);
        if (overflow)
        {
            Assert.Contains(lines, line => line.Direction == LogDirection.System && line.Text.Contains("1 byte(s) omitted"));
            Assert.Contains(lines, line => line.IsPartialRxTerminator);
        }
    }

    [Theory]
    [InlineData(RxEncodingMode.Utf8)]
    [InlineData(RxEncodingMode.Cp949)]
    public async Task MissingByteEndsTheDecoderStateBeforeTheNextChunk(RxEncodingMode encoding)
    {
        var bytes = encoding == RxEncodingMode.Utf8 ? new byte[] { 0xE2, 0x82, 0xAC } : new byte[] { 0xB0, 0xA1 };
        var source = Channel.CreateUnbounded<ReceivedByteChunk>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        pipeline.Error += (_, _) => { };
        await pipeline.StartAsync(source.Reader, new() { Encoding = encoding }, default);
        await source.Writer.WriteAsync(new([bytes[0]], 1, streamOffset: 0));
        await source.Writer.WriteAsync(new(bytes.AsSpan(1).ToArray(), 2, streamOffset: 2));
        source.Writer.TryComplete();
        await pipeline.DrainAsync(default);
        var lines = new List<LogLine>(); while (pipeline.Logs.TryRead(out var line)) lines.Add(line);
        var text = string.Concat(lines.Where(line => line.Direction == LogDirection.Rx).Select(line => line.Text));
        Assert.DoesNotContain(encoding == RxEncodingMode.Utf8 ? "€" : "가", text);
        Assert.Contains(lines, line => line.IsPartialRxTerminator);
    }
}
