using System.Text;
using System.Reflection;
using System.Runtime.CompilerServices;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ProtocolReceiveIntegrationTests
{
    [Fact]
    public async Task MonitoringSequenceTransmitsAndAnalyzesBothDirectionsWithoutChangingRole()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var serial = new SerialService();
        await using var analyzer = new ProtocolAnalyzer();
        var protocol = new ProtocolViewModel(analyzer) { SelectedViewIndex = 1 };
        var settings = new SerialSettings { PortName = "MOCK" };
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainViewModel).GetField("_serialService", flags)!.SetValue(model, serial);
        typeof(MainViewModel).GetField("_currentSerialSettings", flags)!.SetValue(model, settings);
        typeof(MainViewModel).GetField("<Protocol>k__BackingField", flags)!.SetValue(model, protocol);
        var receive = typeof(MainViewModel).GetMethod("OnProtocolBytesReceived", flags)!
            .CreateDelegate<Action<long, ReceivedByteChunk>>(model);
        var transmit = typeof(MainViewModel).GetMethod("OfferProtocolTransmit", flags)!
            .CreateDelegate<Action<LogLine, long?>>(model);
        serial.CaptureBytesReceived += receive;
        serial.SetTransmitMode(SerialTransmitMode.Observe);
        await serial.ConnectAsync(settings, new(), timeout.Token);
        try
        {
            // All bytes are ASCII, including the CRC, so MOCK's text echo
            // preserves this valid request for the production RX analysis path.
            var request = Convert.FromHexString("010300030002340B");
            var sequence = new CommandSequence
            {
                Name = "monitor and send", RepeatCount = 2,
                Steps = [new() { CommandText = Convert.ToHexString(request), TxMode = TxSendMode.Hex, DelayAfterMs = 0 }]
            };
            var completed = 0;
            await new CommandSequenceRunner().RunAsync(sequence,
                _ => Task.FromResult(serial.IsConnected),
                async (step, token) =>
                {
                    var payload = TxPayloadEncoder.Encode(step.CommandText, step.TxMode!.Value, TxLineEndingMode.None);
                    var generation = serial.ReceiveSessionGeneration;
                    await serial.SendBytesAsync(payload, Encoding.ASCII.GetString(payload), token);
                    transmit(LogLine.Tx(step.CommandText, payload, contentMode: LogRuleMatchMode.Hex), generation);
                    return true;
                }, () => false, _ => { }, count => completed = count, timeout.Token);
            while (true)
            {
                var frames = analyzer.Snapshot().Frames!.Where(frame => frame.Bytes.Span.SequenceEqual(request)).ToArray();
                if (frames.Count(frame => frame.Direction == ProtocolDirection.Tx) == 2 &&
                    frames.Count(frame => frame.Direction == ProtocolDirection.Rx) >= 2) break;
                await Task.Delay(5, timeout.Token);
            }
            Assert.Equal(2, completed);
            Assert.Equal(2 * request.Length, serial.WrittenByteCount);
            Assert.Equal(1, protocol.SelectedViewIndex);
            Assert.True(analyzer.Enabled);
        }
        finally
        {
            serial.CaptureBytesReceived -= receive;
            await serial.DisconnectAsync(CancellationToken.None);
        }
    }

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
