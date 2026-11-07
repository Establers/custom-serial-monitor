using System.Text;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class ProtocolReceiveIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExistingRawObserver_AnalyzesWithoutConsumingOrChangingLogPipeline(bool hex, bool bridgePriority)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var serial = new SerialService();
        await using var analyzer = new ModbusRtuAnalyzer();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        var settings = new SerialSettings { PortName = "MOCK", Encoding = RxEncodingMode.Utf8 };
        pipeline.ConfigureRxDisplay(hex ? RxDisplayMode.Hex : RxDisplayMode.Terminal, 5);
        analyzer.SetEnabled(true);
        serial.CaptureBytesReceived += (generation, chunk) =>
            analyzer.TryEnqueue(chunk.Bytes, ProtocolDirection.Rx, generation, DateTimeOffset.Now);
        await serial.ConnectAsync(settings, new SerialReceiveOptions(), timeout.Token);
        serial.SetRawBridgePriorityEnabled(bridgePriority);
        await pipeline.StartAsync(serial.ReceivedBytes, settings, timeout.Token);
        var logRead = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var line in pipeline.Logs.ReadAllAsync(timeout.Token)) count++;
            return count;
        });
        try
        {
            // CRC-valid address-3 request with an entirely ASCII payload, so
            // MOCK's UTF-8 text echo also preserves every original byte.
            var request = Convert.FromHexString("010300030002340B");
            await serial.SendBytesAsync("ignored"u8.ToArray(), Encoding.ASCII.GetString(request), timeout.Token);
            while (!analyzer.Snapshot().Frames!.Any(frame => frame.Bytes.Span.SequenceEqual(request)))
                await Task.Delay(5, timeout.Token);
            Assert.True(pipeline.ProcessedRxByteCount > 0);
        }
        finally
        {
            await serial.DisconnectAsync(timeout.Token);
            await pipeline.DrainAsync(timeout.Token);
        }
        Assert.True(await logRead > 0);
    }
}
