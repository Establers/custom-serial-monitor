using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Win32;
using SerialMonitor.Core.Protocols;
using SerialMonitor.Core.Protocols.VirtualModbus;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class VirtualModbusSerialIntegrationTests
{
    [Com0comPair0Fact]
    public async Task RealVirtualCom_RequestReply_LiveEdit_Broadcast_StopAndSessionFence()
    {
        await VerifyLifecycleAsync(useNativeIdleTimeout: false);
        await VerifyLifecycleAsync(useNativeIdleTimeout: true);
    }

    private static async Task VerifyLifecycleAsync(bool useNativeIdleTimeout)
    {
        foreach (var name in new[] { "CNCA0", "CNCB0" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\com0com\port\" + name);
            Assert.NotNull(key); Assert.Equal("com0com", key.GetValue("Service"));
            using var settings = key.OpenSubKey("Device Parameters"); Assert.Equal(name, settings!.GetValue("PortName"));
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var server = new SerialService(); await using var product = new SerialService();
        await server.ConnectAsync(new() { PortName = "CNCA0", BaudRate = 115200 }, new() { UseNativeIdleTimeout = useNativeIdleTimeout, IdleTimeoutMs = 5 }, timeout.Token);
        await product.ConnectAsync(new() { PortName = "CNCB0", BaudRate = 115200 }, new(), timeout.Token);
        var generation = server.ReceiveSessionGeneration;
        var store = new VirtualModbusDeviceStore(); store.Add(new()
        {
            SchemaVersion = 1, Devices = [new() { Name = "Sensor", UnitId = 1, Registers = [new()
            { Name = "Temperature", Area = "holding", Address = 0, DataType = "int16", Value = JsonSerializer.SerializeToElement(25), Scale = .1 }] }]
        });
        await using var analyzer = new ModbusRtuAnalyzer(); analyzer.SetEnabled(true);
        await using var runtime = new VirtualModbusDeviceRuntime(store, async (bytes, session, token) =>
        {
            await server.SendTerminalInputAsync(bytes, session, token);
            analyzer.TryEnqueue(bytes, ProtocolDirection.Tx, session, DateTimeOffset.Now, new ProtocolInputTiming(Role: ProtocolFrameRole.Response));
        });
        server.CaptureBytesReceived += (session, chunk) => runtime.TryEnqueue(chunk.Bytes, session, chunk.ReceivedTimestamp, chunk.EndsAtNativeIdleBoundary);
        server.StatusChanged += (_, _) => { if (!server.IsConnected || server.ReceiveSessionGeneration != generation) runtime.RequestStop(); };
        runtime.Start(generation, 1.75);
        await product.SendTerminalInputAsync(Adu("010300000001"), product.ReceiveSessionGeneration, timeout.Token);
        Assert.Equal(Adu("01030200FA"), await Read(product, 7, timeout.Token));
        // The observer does not consume the original RX pipeline.
        Assert.Equal(Adu("010300000001"), await Read(server, 8, timeout.Token));
        store.SetValue(1, 0, "80");
        await product.SendTerminalInputAsync(Adu("010300000001"), product.ReceiveSessionGeneration, timeout.Token);
        Assert.Equal(Adu("0103020320"), await Read(product, 7, timeout.Token));
        await product.SendTerminalInputAsync(Adu("00060000007B"), product.ReceiveSessionGeneration, timeout.Token);
        while (store.Page(1, 0)[0].RawWords[0] != 123) await Task.Delay(5, timeout.Token);
        using (var quiet = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
        {
            quiet.CancelAfter(80);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await product.ReceivedBytes.ReadAsync(quiet.Token));
        }
        await runtime.StopAsync(timeout.Token);
        Assert.False(runtime.Snapshot().Running);
        await server.DisconnectAsync(timeout.Token);
        await server.ConnectAsync(new() { PortName = "CNCA0" }, new(), timeout.Token);
        Assert.NotEqual(generation, server.ReceiveSessionGeneration);
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.SendTerminalInputAsync(Adu("0103020001"), generation, timeout.Token));
        Assert.False(runtime.Snapshot().Running);
        while (analyzer.Snapshot().TotalFrames < 2) await Task.Delay(5, timeout.Token);
        Assert.All(analyzer.Snapshot().Frames!, frame => { Assert.Equal(generation, frame.Session); Assert.Equal(ModbusFrameKind.Response, frame.Kind); });
    }

    private static byte[] Adu(string body)
    {
        var bytes = Convert.FromHexString(body); var result = new byte[bytes.Length + 2]; bytes.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(bytes.Length), ModbusRtuCrc.Compute(bytes)); return result;
    }
    private static async Task<byte[]> Read(SerialService serial, int count, CancellationToken token)
    {
        var result = new List<byte>();
        while (result.Count < count) result.AddRange((await serial.ReceivedBytes.ReadAsync(token)).Bytes);
        Assert.Equal(count, result.Count); return result.ToArray();
    }
}
