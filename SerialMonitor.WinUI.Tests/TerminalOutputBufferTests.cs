using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class TerminalOutputBufferTests
{
    [Fact]
    public async Task DeviceEditingResponsesUseRawDisplayWhileTheLogPipelineKeepsItsExistingRecords()
    {
        var scenarios = new[]
        {
            new { Name = "backspace", Fragments = new[] { "device> ab", "\b \b" }, Rows = new[] { "device> a" }, X = 9, Y = 0 },
            new { Name = "cursor", Fragments = new[] { "device> abc", "\u001b[", "2D", "Z", "\u001b[C" }, Rows = new[] { "device> aZc" }, X = 11, Y = 0 },
            new { Name = "history", Fragments = new[] { "device> typed", "\r", "\u001b[2", "Kdevice> previous" }, Rows = new[] { "device> previous" }, X = 16, Y = 0 },
            new { Name = "prompt after log", Fragments = new[] { "device> cmd", "\r\u001b[2K", "LOG ready\r\n", "device> cmd" }, Rows = new[] { "LOG ready", "device> cmd" }, X = 11, Y = 1 },
            new { Name = "linefeed is not carriage return", Fragments = new[] { "a", "\n", "b" }, Rows = new[] { "a", " b" }, X = 2, Y = 1 }
        };
        var fixtures = new List<object>();
        foreach (var scenario in scenarios)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var source = Channel.CreateBounded<ReceivedByteChunk>(32);
            var output = new TerminalOutputBuffer(7, RxEncodingMode.Utf8);
            var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
            await pipeline.StartAsync(source.Reader, new SerialSettings(), timeout.Token);
            var displayBatches = new List<string>();
            foreach (var fragment in scenario.Fragments)
            {
                // These same transport chunks independently enter the raw display
                // and normal parser; neither path consumes the other's channel.
                var chunk = ReceivedByteChunk.Capture(Encoding.UTF8.GetBytes(fragment));
                output.Offer(7, chunk);
                await source.Writer.WriteAsync(chunk, timeout.Token);
                displayBatches.Add(output.Drain(out var error));
                Assert.Null(error);
            }
            Assert.Equal(string.Concat(scenario.Fragments), string.Concat(displayBatches));
            output.Stop();
            var off = ReceivedByteChunk.Capture("\r\nOFF\r\n"u8.ToArray());
            output.Offer(7, off);
            Assert.Empty(output.Drain(out _));
            await source.Writer.WriteAsync(off, timeout.Token);
            source.Writer.TryComplete();
            await pipeline.DrainAsync(timeout.Token);
            var records = new List<LogLine>();
            while (pipeline.Logs.TryRead(out var line)) records.Add(line);
            var logs = new LogViewModel(capacity: 100);
            logs.AddRange(records);
            Assert.Contains("OFF", logs.GetVisibleTextSnapshot());
            if (scenario.Name == "backspace") Assert.Contains("\\x08", logs.GetVisibleTextSnapshot());
            if (scenario.Name == "cursor") Assert.Contains("\\x1B", logs.GetVisibleTextSnapshot());
            fixtures.Add(new { scenario.Name, Batches = displayBatches, scenario.Rows, scenario.X, scenario.Y,
                NormalLog = logs.GetXtermTextSnapshot() });
        }
        var fixturePath = Environment.GetEnvironmentVariable("SERIALMONITOR_TERMINAL_RX_FIXTURE");
        if (!string.IsNullOrEmpty(fixturePath))
            await File.WriteAllTextAsync(fixturePath, JsonSerializer.Serialize(fixtures));
    }

    [Theory]
    [InlineData(RxEncodingMode.Utf8)]
    [InlineData(RxEncodingMode.Cp949)]
    public void SplitMultibyteCharactersAndControlSequencesRemainAStream(RxEncodingMode mode)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = mode == RxEncodingMode.Cp949 ? Encoding.GetEncoding(949) : Encoding.UTF8;
        const string expected = "한글\b\r\u001b[2Kdevice> ";
        var output = new TerminalOutputBuffer(1, mode);
        var actual = new StringBuilder();
        foreach (var value in encoding.GetBytes(expected))
        {
            output.Offer(1, ReceivedByteChunk.Capture([value]));
            actual.Append(output.Drain(out var error));
            Assert.Null(error);
        }
        Assert.Equal(expected, actual.ToString());
    }

    [Fact]
    public void StaleGenerationsAndStoppedSessionsCannotContaminateANewPrompt()
    {
        var output = new TerminalOutputBuffer(2, RxEncodingMode.Utf8);
        output.Offer(1, ReceivedByteChunk.Capture("stale\r"u8.ToArray()));
        Assert.Empty(output.Drain(out _));
        output.Offer(2, ReceivedByteChunk.Capture("current> "u8.ToArray()));
        Assert.Equal("current> ", output.Drain(out _));
        output.Stop();
        output.Offer(2, ReceivedByteChunk.Capture("after OFF\r"u8.ToArray()));
        Assert.Empty(output.Drain(out var error));
        Assert.Null(error);
        Assert.Equal(0, output.PendingBytes);
    }

    [Fact]
    public void ByteAndChunkLimitsFailClosedRatherThanDropAnsiFragments()
    {
        foreach (var chunkSize in new[] { 1, TerminalOutputBuffer.MaxPendingBytes })
        {
            var output = new TerminalOutputBuffer(1, RxEncodingMode.Utf8);
            var attempts = chunkSize == 1 ? 1025 : 2;
            for (var index = 0; index < attempts; index++)
                output.Offer(1, ReceivedByteChunk.Capture(new byte[chunkSize]));
            Assert.Empty(output.Drain(out var error));
            Assert.Contains("queue is full", error);
            Assert.Equal(0, output.PendingBytes);
        }
    }

    [Fact]
    public void AUiDrainHasABoundedCostAndKeepsTheRestInOrder()
    {
        var output = new TerminalOutputBuffer(1, RxEncodingMode.Utf8);
        var bytes = Encoding.UTF8.GetBytes(new string('a', TerminalOutputBuffer.MaxBatchBytes) + "\r\u001b[Kend");
        output.Offer(1, ReceivedByteChunk.Capture(bytes));
        Assert.Equal(new string('a', TerminalOutputBuffer.MaxBatchBytes), output.Drain(out _));
        Assert.InRange(output.PendingBytes, 1, TerminalOutputBuffer.MaxBatchBytes);
        Assert.Equal("\r\u001b[Kend", output.Drain(out _));
        Assert.Equal(0, output.PendingBytes);
    }

    [Fact]
    public async Task ActualSerialCapturePublishesRawEchoAlongsideTheParserChannel()
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new SerialSettings { PortName = "MOCK" }, new(), default);
        var output = new TerminalOutputBuffer(serial.ReceiveSessionGeneration, RxEncodingMode.Utf8);
        serial.CaptureBytesReceived += output.Offer;
        const string response = "device> ab\b \b\r\u001b[2Kdevice> previous";
        await serial.SendTerminalInputAsync(Encoding.UTF8.GetBytes(response), serial.ReceiveSessionGeneration, default);
        Assert.Equal(response, output.Drain(out var error));
        Assert.Null(error);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        ReceivedByteChunk chunk;
        do { chunk = await serial.ReceivedBytes.ReadAsync(timeout.Token); }
        while (!chunk.Bytes.AsSpan().StartsWith("device> ab"u8));
        Assert.Equal(Encoding.UTF8.GetBytes(response), chunk.Bytes);
        serial.CaptureBytesReceived -= output.Offer;
    }
}
