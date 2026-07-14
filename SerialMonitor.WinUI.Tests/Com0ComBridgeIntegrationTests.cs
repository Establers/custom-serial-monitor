using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using RJCP.IO.Ports;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class Com0ComBridgeIntegrationTests
{
    [Fact]
    public async Task Com4Com5_PreservesBytesBothWays_AndArbitratesOneManualTransmit()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SERIAL_COM0COM_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var partner = new SerialPortStream("COM5", 115200, 8, Parity.None, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = Timeout.Infinite,
            WriteTimeout = 1000
        };
        partner.Open();

        var deviceWrites = Channel.CreateUnbounded<byte[]>();
        var blockNextBridgeWrite = 0;
        var blockedWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlockedWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var bridge = new SerialBridgeService();
        await bridge.StartAsync(
            new BridgeSettings
            {
                Enabled = true,
                VirtualPortName = "COM4",
                ManualTxIdleGuardMs = 25
            },
            new SerialSettings { PortName = "DEVICE", BaudRate = 115200 },
            async (bytes, cancellationToken) =>
            {
                if (Interlocked.Exchange(ref blockNextBridgeWrite, 0) == 1)
                {
                    blockedWriteEntered.TrySetResult();
                    await releaseBlockedWrite.Task.WaitAsync(cancellationToken);
                }

                await deviceWrites.Writer.WriteAsync(bytes, cancellationToken);
            },
            timeout.Token);
        partner.DiscardInBuffer();
        partner.DiscardOutBuffer();

        var random = new Random(384009600);
        var deviceToVirtual = new byte[16 * 1024];
        random.NextBytes(deviceToVirtual);
        Assert.True(bridge.TryEnqueueDeviceChunk(new BridgeRxChunk(
            deviceToVirtual,
            Stopwatch.GetTimestamp(),
            false,
            0)));
        var partnerReceived = await ReadExactlyAsync(partner, deviceToVirtual.Length, timeout.Token);
        Assert.Equal(SHA256.HashData(deviceToVirtual), SHA256.HashData(partnerReceived));

        var virtualToDevice = new byte[12 * 1024];
        random.NextBytes(virtualToDevice);
        await partner.WriteAsync(virtualToDevice, timeout.Token);
        var deviceReceived = await ReadChannelBytesExactlyAsync(
            deviceWrites.Reader,
            virtualToDevice.Length,
            timeout.Token);
        Assert.Equal(SHA256.HashData(virtualToDevice), SHA256.HashData(deviceReceived));

        Volatile.Write(ref blockNextBridgeWrite, 1);
        await partner.WriteAsync(new byte[] { 0xA1, 0xA2, 0xA3 }, timeout.Token);
        await blockedWriteEntered.Task.WaitAsync(timeout.Token);

        var manualSendCount = 0;
        var firstManual = bridge.QueueManualTransmitAsync(
            _ =>
            {
                Interlocked.Increment(ref manualSendCount);
                return Task.CompletedTask;
            },
            timeout.Token);
        await Task.Delay(10, timeout.Token);
        Assert.Equal(ManualTxState.WaitingForBridgeIdle, bridge.ManualTxState);

        var secondManual = await bridge.QueueManualTransmitAsync(
            _ => Task.CompletedTask,
            timeout.Token);
        Assert.Equal(ManualTransmitResult.Busy, secondManual);

        releaseBlockedWrite.TrySetResult();
        Assert.Equal(ManualTransmitResult.Sent, await firstManual.WaitAsync(timeout.Token));
        Assert.Equal(1, Volatile.Read(ref manualSendCount));
        Assert.Equal(ManualTxState.Idle, bridge.ManualTxState);
        Assert.Equal(0, bridge.DroppedDeviceToVirtualByteCount);
        Assert.Equal(0, bridge.DroppedVirtualToDeviceByteCount);

        Assert.True(bridge.TryEnqueueDeviceChunk(new BridgeRxChunk(
            new byte[] { 0xF1 },
            Stopwatch.GetTimestamp(),
            false,
            0)));
        var canceledManualSendCount = 0;
        var canceledManual = bridge.QueueManualTransmitAsync(
            _ =>
            {
                Interlocked.Increment(ref canceledManualSendCount);
                return Task.CompletedTask;
            },
            timeout.Token);
        await bridge.StopAsync(timeout.Token);
        Assert.Equal(ManualTransmitResult.Canceled, await canceledManual.WaitAsync(timeout.Token));
        Assert.Equal(0, Volatile.Read(ref canceledManualSendCount));
    }

    [Fact]
    public async Task Com4Com5_QueueOverflowFaultsBridgeImmediately()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SERIAL_COM0COM_TEST"), "1", StringComparison.Ordinal))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var partner = new SerialPortStream("COM5", 115200, 8, Parity.None, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = Timeout.Infinite,
            WriteTimeout = 1000
        };
        partner.Open();
        await using var bridge = new SerialBridgeService();
        await bridge.StartAsync(
            new BridgeSettings
            {
                Enabled = true,
                VirtualPortName = "COM4",
                MaxQueuedChunks = 1,
                MaxQueuedBytes = 64 * 1024
            },
            new SerialSettings { PortName = "DEVICE", BaudRate = 115200 },
            (_, _) => Task.CompletedTask,
            timeout.Token);
        partner.DiscardInBuffer();

        var now = Stopwatch.GetTimestamp();
        Assert.True(bridge.TryEnqueueDeviceChunk(new BridgeRxChunk(new byte[] { 1 }, now, false, 0)));
        Assert.Equal(new byte[] { 1 }, await ReadExactlyAsync(partner, 1, timeout.Token));

        var farFuture = now + Stopwatch.Frequency * 10;
        Assert.True(bridge.TryEnqueueDeviceChunk(new BridgeRxChunk(new byte[] { 2 }, farFuture, false, 0)));
        await WaitUntilAsync(() => bridge.PendingDeviceToVirtualChunkCount == 0, timeout.Token);
        Assert.True(bridge.TryEnqueueDeviceChunk(new BridgeRxChunk(new byte[] { 3 }, farFuture + 1, false, 0)));

        var started = Stopwatch.GetTimestamp();
        Assert.False(bridge.TryEnqueueDeviceChunk(new BridgeRxChunk(new byte[] { 4 }, farFuture + 2, false, 0)));
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(50));
        Assert.False(bridge.IsRunning);
        Assert.Equal("Bridge stopped: virtual COM consumer too slow", bridge.LastFaultReason);
        Assert.Equal(1, bridge.QueueOverflowCount);
        Assert.True(partner.IsOpen);
    }

    private static async Task<byte[]> ReadExactlyAsync(
        SerialPortStream port,
        int byteCount,
        CancellationToken cancellationToken)
    {
        var result = new byte[byteCount];
        var offset = 0;
        while (offset < result.Length)
        {
            offset += await port.ReadAsync(result.AsMemory(offset), cancellationToken);
        }

        return result;
    }

    private static async Task<byte[]> ReadChannelBytesExactlyAsync(
        ChannelReader<byte[]> reader,
        int byteCount,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(byteCount);
        while (stream.Length < byteCount)
        {
            var chunk = await reader.ReadAsync(cancellationToken);
            await stream.WriteAsync(chunk, cancellationToken);
        }

        Assert.Equal(byteCount, stream.Length);
        return stream.ToArray();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(5, cancellationToken);
        }
    }
}
