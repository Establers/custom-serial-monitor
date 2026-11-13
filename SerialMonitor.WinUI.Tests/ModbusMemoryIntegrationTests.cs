using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusMemoryIntegrationTests
{
    [Fact]
    public async Task PollingWithScreenEvictionPreservesRegisterValuesAndEveryTxRxByteOnDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(ModbusMemoryIntegrationTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        var source = Channel.CreateBounded<ReceivedByteChunk>(128);
        var projection = Channel.CreateBounded<LogLine>(128);
        var errors = new ConcurrentQueue<string>();
        var pipeline = new LogPipeline(new EncodingDecoder(), new LineParser());
        Task? forward = null, consume = null;
        try
        {
            await using var writer = new FileLogWriter();
            await writer.StartAsync(root, token);
            var serial = DispatchProxy.Create<ISerialService, ModbusRtuMonitorServiceTests.TestSerial>();
            var device = (ModbusRtuMonitorServiceTests.TestSerial)(object)serial;
            serial.CaptureBytesReceived += (_, chunk) =>
            {
                // Match the production boundary: callbacks enqueue only.
                if (!source.Writer.TryWrite(chunk)) errors.Enqueue("RX test channel overflowed.");
            };
            var expectedTx = new List<byte>();
            var expectedRx = new List<byte>();
            var responses = 0;
            device.OnSend = packet =>
            {
                var value = ++responses;
                var reply = ReadReply(packet, (ushort)value, (ushort)(value + 100));
                expectedRx.AddRange(reply);
                device.Emit(reply[..2]);
                device.Emit(reply[2..]);
                return Task.CompletedTask;
            };
            await using var monitor = new ModbusRtuMonitorService(serial, (bytes, _) =>
            {
                expectedTx.AddRange(bytes);
                if (!projection.Writer.TryWrite(LogLine.Tx(HexFormatter.Format(bytes), bytes,
                        contentMode: LogRuleMatchMode.Hex))) errors.Enqueue("TX test channel overflowed.");
            });
            var policy = new LogViewMemoryPolicy(RetainedBytes: 2048, VisibleCharacters: 4096);
            var screen = new LogViewModel(1000, policy);
            screen.SetRxDisplayMode(RxDisplayMode.Hex);
            pipeline.ConfigureRxDisplay(RxDisplayMode.Hex, 20);
            pipeline.Error += (_, error) => errors.Enqueue(error);
            writer.Error += (_, error) => errors.Enqueue(error);
            await pipeline.StartAsync(source.Reader, new() { Encoding = RxEncodingMode.Hex }, token);
            forward = Task.Run(async () =>
            {
                await foreach (var line in pipeline.Logs.ReadAllAsync(token))
                    await projection.Writer.WriteAsync(line, token);
            }, token);
            consume = Task.Run(async () =>
            {
                var records = 0;
                await foreach (var line in projection.Reader.ReadAllAsync(token))
                {
                    if (!line.IsPartialRxTerminator) Assert.True(writer.TryEnqueue(line));
                    screen.AddRange([line]);
                    if (++records == 12)
                    {
                        screen.SetCapacity(4);
                        screen.SetMemoryBudgetMiB(256);
                    }
                    if (records == 14) screen.SetMemoryBudgetMiB(64);
                    if (records == 20) screen.SetCapacity(1000);
                    Assert.InRange(screen.RetainedMemoryBytes, 0, screen.RetainedMemoryByteLimit);
                    Assert.InRange(screen.VisibleCharacterCount, 0, policy.VisibleCharacters);
                }
            }, token);
            var run = monitor.RunAsync(new() { Address = 10, Quantity = 2, IntervalMs = 100, TimeoutMs = 1000 }, true, token);
            try
            {
                while (monitor.GetSnapshot().SuccessCount < 8) await Task.Delay(5, token);
            }
            finally { await monitor.StopAsync().WaitAsync(token); }
            await run;
            source.Writer.TryComplete();
            await pipeline.DrainAsync(token);
            await forward;
            projection.Writer.TryComplete();
            await consume;
            await writer.StopAsync(token);

            var snapshot = monitor.GetSnapshot();
            Assert.Equal(responses, snapshot.SuccessCount);
            Assert.Equal(new ushort[] { (ushort)responses, (ushort)(responses + 100) }, snapshot.Values);
            Assert.Equal(0, snapshot.ErrorCount);
            Assert.Null(device.Owner);
            Assert.True(device.Connected);
            Assert.True(screen.DroppedVisibleLineCount > 0);
            Assert.Equal(64L * 1024 * 1024, screen.RetainedMemoryByteLimit);
            Assert.Contains(HexFormatter.Format(ReadReply(device.Sent[^1], (ushort)responses, (ushort)(responses + 100))),
                screen.GetVisibleTextSnapshot());
            var diskTx = new List<byte>();
            var diskRx = new List<byte>();
            foreach (var line in await File.ReadAllLinesAsync(writer.LastLogFilePath!, token))
            {
                var tx = line.IndexOf("TX > ", StringComparison.Ordinal);
                var rx = line.IndexOf("RX < ", StringComparison.Ordinal);
                Assert.True(tx >= 0 || rx >= 0);
                var payload = line[((tx >= 0 ? tx : rx) + 5)..].Replace(" ", "");
                (tx >= 0 ? diskTx : diskRx).AddRange(Convert.FromHexString(payload));
            }
            Assert.Equal(SHA256.HashData(expectedTx.ToArray()), SHA256.HashData(diskTx.ToArray()));
            Assert.Equal(SHA256.HashData(expectedRx.ToArray()), SHA256.HashData(diskRx.ToArray()));
            Assert.Equal(0, writer.DroppedLineCount);
            Assert.Equal(0, writer.AbandonedLineCount);
            Assert.Empty(errors);
        }
        finally
        {
            deadline.Cancel();
            source.Writer.TryComplete();
            projection.Writer.TryComplete();
            await pipeline.StopAsync(CancellationToken.None);
            // A preceding assertion/transport failure may cancel both workers.
            // Observe cleanup exceptions while preserving the original failure.
            try { await Task.WhenAll(new[] { forward, consume }.OfType<Task>()); }
            catch (Exception) { }
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ModbusAndMemorySettingsSurviveProfileAndSequenceSavesTogether()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(ModbusMemoryIntegrationTests), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "profile.json");
        Directory.CreateDirectory(root);
        try
        {
            var service = new ProfileService();
            var profile = service.CreateDefaultProfile();
            profile.UiSettings.LogMemoryBudgetMiB = 512;
            profile.UiSettings.MaxVisibleLogLines = 200_000;
            profile.ModbusSettings = new() { UnitId = 17, Address = 4000, Quantity = 8, IntervalMs = 250, TimeoutMs = 2000 };
            profile.CommandSequences = [new() { Name = "Integration", Steps = [new() { CommandText = "status" }] }];
            await service.SaveAsync(path, profile, CancellationToken.None);
            var loaded = await service.LoadAsync(path, CancellationToken.None);
            Assert.Equal(512, loaded.UiSettings.LogMemoryBudgetMiB);
            Assert.Equal(17, loaded.ModbusSettings.UnitId);
            loaded.UiSettings = loaded.UiSettings.Clone();
            loaded.ModbusSettings = loaded.ModbusSettings.Clone();
            loaded.UiSettings.LogMemoryBudgetMiB = 128;
            loaded.ModbusSettings.UnitId = 42;
            loaded.CommandSequences[0].Steps[0].CommandText = "version";
            await service.SaveSequenceChangeAsync(path, loaded, "Integration", CancellationToken.None);
            var restored = await new ProfileService().LoadAsync(path, CancellationToken.None);
            Assert.Equal(128, restored.UiSettings.LogMemoryBudgetMiB);
            Assert.Equal(200_000, restored.UiSettings.MaxVisibleLogLines);
            Assert.Equal(42, restored.ModbusSettings.UnitId);
            Assert.Equal(4000, restored.ModbusSettings.Address);
            Assert.Equal(8, restored.ModbusSettings.Quantity);
            Assert.Equal(250, restored.ModbusSettings.IntervalMs);
            Assert.Equal(2000, restored.ModbusSettings.TimeoutMs);
            Assert.Equal("version", restored.CommandSequences[0].Steps[0].CommandText);
            Assert.Null(service.LastError);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("\"not-a-format\"")]
    [InlineData("null")]
    public async Task InvalidModbusDisplayConfigurationCannotResetTheMemoryBudget(string valueType)
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(ModbusMemoryIntegrationTests), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "profile.json");
        Directory.CreateDirectory(root);
        try
        {
            var profiles = new ProfileService();
            var original = profiles.CreateDefaultProfile();
            original.UiSettings.LogMemoryBudgetMiB = 512;
            original.UiSettings.MaxVisibleLogLines = 200_000;
            original.ModbusSettings.UnitId = 42;
            original.ModbusSettings.Address = 4000;
            var json = JsonNode.Parse(JsonSerializer.Serialize(original))!;
            json[nameof(AppProfile.ModbusSettings)]![nameof(ModbusMonitorSettings.ValueType)] = JsonNode.Parse(valueType);
            await File.WriteAllTextAsync(path, json.ToJsonString());
            var loaded = await profiles.LoadAsync(path, CancellationToken.None);
            Assert.Equal(512, loaded.UiSettings.LogMemoryBudgetMiB);
            Assert.Equal(200_000, loaded.UiSettings.MaxVisibleLogLines);
            Assert.Equal(42, loaded.ModbusSettings.UnitId);
            Assert.Equal(4000, loaded.ModbusSettings.Address);
            Assert.Throws<ArgumentOutOfRangeException>(loaded.ModbusSettings.Validate);
            await profiles.SaveAsync(path, loaded, CancellationToken.None);
            var restored = await new ProfileService().LoadAsync(path, CancellationToken.None);
            Assert.Equal(512, restored.UiSettings.LogMemoryBudgetMiB);
            Assert.Equal(200_000, restored.UiSettings.MaxVisibleLogLines);
            Assert.Equal(42, restored.ModbusSettings.UnitId);
            Assert.Equal(4000, restored.ModbusSettings.Address);
        }
        finally { Directory.Delete(root, true); }
    }

    private static byte[] ReadReply(byte[] packet, ushort first, ushort second)
    {
        var response = new byte[9];
        response[0] = packet[0]; response[1] = packet[1]; response[2] = 4;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(3), first);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(5), second);
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(7), ModbusRtuCrc.Compute(response.AsSpan(0, 7)));
        return response;
    }
}
