using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialSettingsBehaviorTests
{
    [Fact]
    public async Task ProfileSaveLoadPreservesAllSerialOptionsAndLastSuccessfulConnection()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SerialSettingsTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "profile.json");
        try
        {
            var service = new ProfileService();
            var profile = service.CreateDefaultProfile();
            var parities = Enum.GetValues<SerialParityMode>();
            var stopBits = Enum.GetValues<SerialStopBitsMode>();
            var encodings = Enum.GetValues<RxEncodingMode>();
            var rxEndings = Enum.GetValues<RxLineEndingMode>();
            var txEndings = Enum.GetValues<TxLineEndingMode>();
            foreach (var handshake in Enum.GetValues<SerialHandshakeMode>())
            {
                var index = (int)handshake;
                profile.SerialSettings = new()
                {
                    PortName = "COM42",
                    BaudRate = 250000 + index,
                    DataBits = 5 + index % 4,
                    Parity = parities[index % parities.Length],
                    StopBits = stopBits[index % stopBits.Length],
                    Handshake = handshake,
                    DtrEnable = (index & 1) != 0,
                    RtsEnable = (index & 2) != 0,
                    RxLineEnding = rxEndings[index % rxEndings.Length],
                    TxLineEnding = txEndings[index % txEndings.Length],
                    Encoding = encodings[index % encodings.Length]
                };
                profile.LastSuccessfulSerialSettings = profile.SerialSettings.Clone();
                var expected = JsonSerializer.Serialize(profile.SerialSettings);
                await service.SaveAsync(path, profile, default);
                var loaded = await service.LoadAsync(path, default);
                Assert.Equal(expected, JsonSerializer.Serialize(loaded.SerialSettings));
                Assert.Equal(expected, JsonSerializer.Serialize(loaded.LastSuccessfulSerialSettings));
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(RxLineEndingMode.Auto, "one\rtwo\nthree\r\nfour\r\n", "one|two|three|four")]
    [InlineData(RxLineEndingMode.Cr, "one\rtwo\r", "one|two")]
    [InlineData(RxLineEndingMode.Lf, "one\ntwo\n", "one|two")]
    [InlineData(RxLineEndingMode.Crlf, "one\r\ntwo\r\n", "one|two")]
    public async Task RxEndingSeparatesLinesEvenWhenTerminatorsCrossReceiveChunks(
        RxLineEndingMode ending, string input, string expected)
    {
        var chunks = Channel.CreateBounded<ReceivedByteChunk>(64);
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipeline.StartAsync(chunks.Reader, new() { RxLineEnding = ending }, cancellation.Token);
        try
        {
            foreach (var value in Encoding.UTF8.GetBytes(input))
                await chunks.Writer.WriteAsync(ReceivedByteChunk.Capture([value]), cancellation.Token);
            chunks.Writer.TryComplete();
            await pipeline.DrainAsync(cancellation.Token);
            var lines = new List<string>();
            while (pipeline.Logs.TryRead(out var line)) lines.Add(line.Text);
            Assert.Equal(expected.Split('|'), lines);
        }
        finally
        {
            chunks.Writer.TryComplete();
            await pipeline.StopAsync(default);
        }
    }

    [Theory]
    [InlineData(RxEncodingMode.Ascii, "ASCII")]
    [InlineData(RxEncodingMode.Utf8, "한글 UTF-8")]
    [InlineData(RxEncodingMode.Cp949, "한글 CP949")]
    public async Task RxEncodingDecodesBytesSplitAcrossReceiveChunks(RxEncodingMode mode, string expected)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = mode switch
        {
            RxEncodingMode.Ascii => Encoding.ASCII,
            RxEncodingMode.Cp949 => Encoding.GetEncoding(949),
            _ => Encoding.UTF8
        };
        var chunks = Channel.CreateBounded<ReceivedByteChunk>(64);
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipeline.StartAsync(chunks.Reader, new() { Encoding = mode }, cancellation.Token);
        try
        {
            foreach (var value in encoding.GetBytes(expected + "\n"))
                await chunks.Writer.WriteAsync(ReceivedByteChunk.Capture([value]), cancellation.Token);
            var line = await pipeline.Logs.ReadAsync(cancellation.Token);
            Assert.Equal(expected, line.Text);
            Assert.Equal(0, pipeline.DecodeErrorCount);
        }
        finally
        {
            chunks.Writer.TryComplete();
            await pipeline.DrainAsync(default);
        }
    }
}
