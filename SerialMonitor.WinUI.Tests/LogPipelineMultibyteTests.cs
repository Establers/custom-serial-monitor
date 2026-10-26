using System.Text;
using System.Threading.Channels;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogPipelineMultibyteTests
{
    [Theory]
    [InlineData(RxEncodingMode.Utf8, 1)]
    [InlineData(RxEncodingMode.Utf8, 2)]
    [InlineData(RxEncodingMode.Cp949, 1)]
    public async Task ThresholdPartialFlushPreservesKoreanCharacterAcrossFragments(RxEncodingMode mode, int split)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = mode == RxEncodingMode.Cp949 ? Encoding.GetEncoding(949) : Encoding.UTF8;
        var korean = encoding.GetBytes("가나");
        var prefix = new string('x', 128 - split);
        var firstBytes = Encoding.ASCII.GetBytes(prefix).Concat(korean[..split]).ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var source = Channel.CreateUnbounded<ReceivedByteChunk>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        await pipeline.StartAsync(source.Reader, new SerialSettings { Encoding = mode }, timeout.Token);
        try
        {
            await source.Writer.WriteAsync(ReceivedByteChunk.Capture(firstBytes), timeout.Token);
            var first = await pipeline.Logs.ReadAsync(timeout.Token);
            Assert.True(first.IsPartialRxSegment);
            Assert.Equal(prefix, first.Text);
            Assert.Equal(firstBytes, first.RawBytes);
            Assert.Equal(0, pipeline.DecodeErrorCount);

            await source.Writer.WriteAsync(ReceivedByteChunk.Capture(korean[split..].Concat(new byte[] { (byte)'\n' }).ToArray()), timeout.Token);
            var last = await pipeline.Logs.ReadAsync(timeout.Token);
            Assert.Equal("가나", last.Text);
            Assert.Equal(prefix + "가나", first.Text + last.Text);
            Assert.Equal(Encoding.ASCII.GetBytes(prefix).Concat(korean), first.RawBytes!.Concat(last.RawBytes!));
            Assert.Equal(0, pipeline.DecodeErrorCount);
        }
        finally { source.Writer.TryComplete(); await pipeline.DrainAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(RxEncodingMode.Utf8)]
    [InlineData(RxEncodingMode.Cp949)]
    public async Task IdlePartialFlushDoesNotTreatIncompleteCharacterAsDecodeError(RxEncodingMode mode)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = (mode == RxEncodingMode.Cp949 ? Encoding.GetEncoding(949) : Encoding.UTF8).GetBytes("한");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var source = Channel.CreateUnbounded<ReceivedByteChunk>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        await pipeline.StartAsync(source.Reader, new SerialSettings { Encoding = mode }, timeout.Token);
        try
        {
            await source.Writer.WriteAsync(ReceivedByteChunk.Capture(bytes[..1]), timeout.Token);
            // Wait for the actual 250ms idle flush, rather than guessing timing.
            var first = await pipeline.Logs.ReadAsync(timeout.Token);
            Assert.Equal(bytes[..1], first.RawBytes);
            Assert.Equal("", first.Text);
            Assert.Equal(0, pipeline.DecodeErrorCount);
            await source.Writer.WriteAsync(ReceivedByteChunk.Capture(bytes[1..].Concat(new byte[] { (byte)'\n' }).ToArray()), timeout.Token);
            var last = await pipeline.Logs.ReadAsync(timeout.Token);
            Assert.Equal("한", last.Text);
            Assert.Equal(0, pipeline.DecodeErrorCount);
        }
        finally { source.Writer.TryComplete(); await pipeline.DrainAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(RxEncodingMode.Utf8, 0xEA)]
    [InlineData(RxEncodingMode.Cp949, 0xB0)]
    public async Task NewlineFlushesOnlyTrulyIncompleteTailAndDoesNotContaminateNextLine(RxEncodingMode mode, byte lead)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var source = Channel.CreateUnbounded<ReceivedByteChunk>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        await pipeline.StartAsync(source.Reader, new SerialSettings { Encoding = mode }, timeout.Token);
        try
        {
            await source.Writer.WriteAsync(ReceivedByteChunk.Capture([lead]), timeout.Token);
            var partial = await pipeline.Logs.ReadAsync(timeout.Token);
            Assert.Equal("", partial.Text);
            Assert.Equal(0, pipeline.DecodeErrorCount);
            await source.Writer.WriteAsync(ReceivedByteChunk.Capture("\nOK\n"u8.ToArray()), timeout.Token);
            var fallback = await pipeline.Logs.ReadAsync(timeout.Token);
            var terminator = await pipeline.Logs.ReadAsync(timeout.Token);
            var next = await pipeline.Logs.ReadAsync(timeout.Token);
            Assert.Equal(new EncodingDecoder().Decode(new[] { lead }, mode).Text, fallback.Text);
            Assert.Empty(fallback.RawBytes!);
            Assert.True(terminator.IsPartialRxTerminator);
            Assert.True(fallback.SequenceNumber < terminator.SequenceNumber);
            Assert.Equal("OK", next.Text);
            Assert.Equal(1, pipeline.DecodeErrorCount);
        }
        finally { source.Writer.TryComplete(); await pipeline.DrainAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ModeSwitchFlushesOldDecoderWithoutCarryingItIntoNextTerminalStream()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var source = Channel.CreateUnbounded<ReceivedByteChunk>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        await pipeline.StartAsync(source.Reader, new SerialSettings(), timeout.Token);
        try
        {
            await source.Writer.WriteAsync(ReceivedByteChunk.Capture([0xEA]), timeout.Token);
            Assert.Equal("", (await pipeline.Logs.ReadAsync(timeout.Token)).Text);
            pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, 10);
            Assert.Equal("?", (await pipeline.Logs.ReadAsync(timeout.Token)).Text);
            Assert.True((await pipeline.Logs.ReadAsync(timeout.Token)).IsPartialRxTerminator);
            pipeline.ConfigureRxDisplay(RxDisplayMode.Terminal, 10);
            await source.Writer.WriteAsync(ReceivedByteChunk.Capture(Encoding.UTF8.GetBytes("정상\n")), timeout.Token);
            Assert.Equal("정상", (await pipeline.Logs.ReadAsync(timeout.Token)).Text);
            Assert.Equal(1, pipeline.DecodeErrorCount);
        }
        finally { source.Writer.TryComplete(); await pipeline.DrainAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainOrStopFlushesIncompleteTailAndNewSessionUsesNewEncoding(bool cancelStop)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var source = Channel.CreateUnbounded<ReceivedByteChunk>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        await pipeline.StartAsync(source.Reader, new SerialSettings(), timeout.Token);
        await source.Writer.WriteAsync(ReceivedByteChunk.Capture([0xEA]), timeout.Token);
        Assert.Equal("", (await pipeline.Logs.ReadAsync(timeout.Token)).Text);
        var previousOutput = pipeline.Logs;
        if (cancelStop) await pipeline.StopAsync(timeout.Token);
        else
        {
            source.Writer.TryComplete();
            await pipeline.DrainAsync(timeout.Token);
        }
        Assert.Equal("?", (await previousOutput.ReadAsync(timeout.Token)).Text);
        Assert.Equal(1, pipeline.DecodeErrorCount);

        var nextSource = Channel.CreateUnbounded<ReceivedByteChunk>();
        await pipeline.StartAsync(nextSource.Reader, new SerialSettings { Encoding = RxEncodingMode.Cp949 }, timeout.Token);
        try
        {
            var expected = Encoding.GetEncoding(949).GetBytes("한글\n");
            await nextSource.Writer.WriteAsync(ReceivedByteChunk.Capture(expected), timeout.Token);
            Assert.Equal("한글", (await pipeline.Logs.ReadAsync(timeout.Token)).Text);
            Assert.Equal(1, pipeline.DecodeErrorCount);
        }
        finally { nextSource.Writer.TryComplete(); await pipeline.DrainAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task CancellationExactlyAfterDecoderTailFlushDoesNotLoseFallback()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var source = Channel.CreateUnbounded<ReceivedByteChunk>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        await pipeline.StartAsync(source.Reader, new SerialSettings(), lifetime.Token);
        await source.Writer.WriteAsync(ReceivedByteChunk.Capture([0xEA]), timeout.Token);
        Assert.Equal("", (await pipeline.Logs.ReadAsync(timeout.Token)).Text);
        var canceledAfterDecode = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.StatusChanged += (_, _) =>
        {
            if (pipeline.DecodeErrorCount != 1 || lifetime.IsCancellationRequested) return;
            // DecodeTerminalSafely reports the final incomplete character before
            // the tail is written. Cancel at that exact point, without a sleep.
            lifetime.Cancel();
            canceledAfterDecode.TrySetResult();
        };
        var output = pipeline.Logs;
        source.Writer.TryComplete();
        await pipeline.DrainAsync(timeout.Token);
        await canceledAfterDecode.Task.WaitAsync(timeout.Token);
        Assert.Equal("?", (await output.ReadAsync(timeout.Token)).Text);
        Assert.Equal(1, pipeline.DecodeErrorCount);
        Assert.False(output.TryRead(out _));
    }

    [Fact]
    public async Task StopWithFullOutputQueueNeverWaitsToWriteDecoderFallback()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var source = Channel.CreateUnbounded<ReceivedByteChunk>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        var fullOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.StatusChanged += (_, _) =>
        {
            if (pipeline.PartialRxFlushCount == 1) fullOutput.TrySetResult();
        };
        await pipeline.StartAsync(source.Reader, new SerialSettings(), timeout.Token);
        // Fill all 10,000 slots: 9,999 complete lines and a threshold partial
        // whose last byte is an incomplete UTF-8 character. No consumer runs.
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("OK\n", 9999)) + new string('x', 127))
            .Concat(new byte[] { 0xEA }).ToArray();
        await source.Writer.WriteAsync(ReceivedByteChunk.Capture(bytes), timeout.Token);
        await fullOutput.Task.WaitAsync(timeout.Token);
        Assert.Equal(10000, pipeline.ParsedLineCount);
        await pipeline.StopAsync(timeout.Token);
        Assert.Equal(1, pipeline.DecodeErrorCount);
        var retained = 0;
        while (pipeline.Logs.TryRead(out _)) retained++;
        Assert.Equal(10000, retained);
    }
}
