using System.Buffers.Binary;
using System.Reflection;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusRtuMonitorServiceTests
{
    [Fact]
    public async Task ReadOnceAssemblesSplitResponseAndKeepsSuccessfulValuesUntilDisconnect()
    {
        var (serial, device) = Device();
        device.OnSend = packet =>
        {
            var response = ReadReply(packet, [253, 300]);
            device.Emit(response[..2]);
            device.Emit(response[2..5]);
            device.Emit(response[5..]);
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        await service.RunAsync(Settings(), false, default).WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = service.GetSnapshot();
        Assert.Equal(new ushort[] { 253, 300 }, snapshot.Values);
        Assert.True(snapshot.ValuesCurrent);
        Assert.NotNull(snapshot.UpdatedAt);
        Assert.Equal(1, snapshot.SuccessCount);
        Assert.Equal(0, snapshot.ErrorCount);
        Assert.False(snapshot.IsBusy);
        Assert.Null(device.Owner);
        device.Disconnect();
        Assert.False(service.GetSnapshot().ValuesCurrent);
        Assert.Equal(ModbusMonitorState.Disconnected, service.GetSnapshot().State);
        Assert.Equal(snapshot.Values, service.GetSnapshot().Values);
    }

    [Fact]
    public async Task StopRetainsOwnershipUntilOutstandingReplyAndDoesNotStartAnotherRequest()
    {
        var (serial, device) = Device();
        var sent = Signal();
        byte[]? request = null;
        device.OnSend = packet => { request = packet; sent.TrySetResult(); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunAsync(Settings(timeout: 2000), true, default);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = service.StopAsync();
        Assert.False(stopping.IsCompleted);
        Assert.True(service.IsBusy);
        Assert.NotNull(device.Owner);
        device.Emit(ReadReply(request!, [1, 2]));
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        await run;
        Assert.Single(device.Sent);
        Assert.Null(device.Owner);
        Assert.False(service.GetSnapshot().ValuesCurrent);
        Assert.True(device.Connected);
    }

    [Fact]
    public async Task TimeoutDisconnectsBeforeOwnershipReleaseAndNeverRetries()
    {
        var (serial, device) = Device();
        await using var service = new ModbusRtuMonitorService(serial);
        await Assert.ThrowsAsync<ModbusExchangeException>(() => service.RunAsync(Settings(timeout: 100), true, default));
        Assert.Single(device.Sent);
        Assert.False(device.Connected);
        Assert.True(device.WasOwnedWhenDisconnected);
        Assert.Null(device.Owner);
        Assert.Equal(ModbusMonitorState.Error, service.GetSnapshot().State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(Settings(), false, default));
    }

    [Fact]
    public async Task BadCrcCannotUpdateValuesAndRetiresConnection()
    {
        var (serial, device) = Device();
        device.OnSend = packet =>
        {
            var response = ReadReply(packet, [1, 2]);
            response[^1] ^= 1;
            device.Emit(response);
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        await Assert.ThrowsAsync<ModbusExchangeException>(() => service.RunAsync(Settings(), false, default));
        Assert.Null(service.GetSnapshot().Values);
        Assert.False(device.Connected);
        Assert.Single(device.Sent);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AmbiguousEightByteReadEchoFailsClosedWithoutPublishingValues(byte function)
    {
        var (serial, device) = Device();
        device.OnSend = packet => { device.Emit(packet.ToArray()); return Task.CompletedTask; };
        var settings = Settings();
        settings.Function = function;
        settings.Address = 0x0300;
        settings.Quantity = 24;
        await using var service = new ModbusRtuMonitorService(serial);

        await Assert.ThrowsAsync<ModbusExchangeException>(() => service.RunAsync(settings, false, default));

        Assert.Null(service.GetSnapshot().Values);
        Assert.Equal(0, service.GetSnapshot().SuccessCount);
        Assert.NotEqual(ModbusWriteState.Verified, service.GetSnapshot().WriteState);
        Assert.False(device.Connected);
        Assert.Single(device.Sent);
    }

    [Fact]
    public async Task EchoedWriteAcknowledgementAndReadRequestCannotFalselyVerifyACoilWrite()
    {
        var (serial, device) = Device();
        device.OnSend = packet => { device.Emit(packet.ToArray()); return Task.CompletedTask; };
        var settings = Settings();
        settings.Function = 1;
        settings.Address = 0x0300;
        settings.Quantity = 24;
        await using var service = new ModbusRtuMonitorService(serial);

        await Assert.ThrowsAsync<ModbusExchangeException>(() => service.WriteAsync(settings, 0x0300, [0], default));

        Assert.Null(service.GetSnapshot().Values);
        Assert.Equal(ModbusWriteState.NotVerified, service.GetSnapshot().WriteState);
        Assert.Equal(ModbusMonitorState.Error, service.GetSnapshot().State);
        Assert.False(device.Connected);
        Assert.Equal(2, device.Sent.Count);
        Assert.Equal((byte)5, device.Sent[0][1]);
        Assert.Equal((byte)1, device.Sent[1][1]);
    }

    [Fact]
    public async Task CompleteModbusExceptionStopsWithoutRetiringHealthyConnection()
    {
        var (serial, device) = Device();
        device.OnSend = packet => { device.Emit(Crc([packet[0], (byte)(packet[1] | 128), 2])); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var error = await Assert.ThrowsAsync<ModbusExchangeException>(() => service.RunAsync(Settings(), true, default));
        Assert.Contains("0x02", error.Message);
        Assert.True(device.Connected);
        Assert.Null(device.Owner);
    }

    [Fact]
    public async Task LongPollingIntervalDetectsDisconnectPromptlyAndDoesNotTouchReplacementSession()
    {
        var (serial, device) = Device();
        device.OnSend = packet => { device.Emit(ReadReply(packet, [1, 2])); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var settings = Settings();
        settings.IntervalMs = 60000;
        var run = service.RunAsync(settings, true, default);
        await Until(() => service.GetSnapshot().SuccessCount == 1);
        var generation = device.Generation;
        device.Disconnect();
        device.Reconnect();
        await Assert.ThrowsAnyAsync<Exception>(() => run.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(device.Connected);
        Assert.True(device.Generation > generation);
        Assert.False(service.IsBusy);
        Assert.False(service.GetSnapshot().ValuesCurrent);
        Assert.Single(device.Sent);
    }

    [Theory]
    [InlineData(1, 1, 5)]
    [InlineData(1, 2, 15)]
    [InlineData(3, 1, 6)]
    [InlineData(3, 2, 16)]
    public async Task DirectWriteUsesExpectedFunctionThenReadsBack(byte readFunction, int count, byte writeFunction)
    {
        var (serial, device) = Device();
        var values = readFunction == 1 ? new ushort[] { 1, 0 } : new ushort[] { 101, 202 };
        device.OnSend = packet =>
        {
            device.Emit(packet[1] == readFunction ? ReadReply(packet, values) : WriteAck(packet));
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        var settings = Settings();
        settings.Function = readFunction;
        await service.WriteAsync(settings, settings.Address, values.Take(count).ToArray(), default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { writeFunction, readFunction }, device.Sent.Select(packet => packet[1]));
        Assert.Equal(ModbusWriteState.Verified, service.GetSnapshot().WriteState);
        Assert.Equal(values, service.GetSnapshot().Values);
        Assert.Null(device.Owner);
    }

    [Fact]
    public async Task ReadBackMismatchIsReportedWithoutRetryingWrite()
    {
        var (serial, device) = Device();
        device.OnSend = packet =>
        {
            device.Emit(packet[1] == 3 ? ReadReply(packet, [5, 6]) : WriteAck(packet));
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        await Assert.ThrowsAsync<ModbusExchangeException>(() => service.WriteAsync(Settings(), 10, [999], default));
        Assert.Equal(ModbusWriteState.NotVerified, service.GetSnapshot().WriteState);
        Assert.Equal(new ushort[] { 5, 6 }, service.GetSnapshot().Values);
        Assert.True(device.Connected);
        Assert.Equal(2, device.Sent.Count);
    }

    [Fact]
    public async Task PollingWriteQueueIsBoundedAndStopCancelsPendingWriteWithoutTransmission()
    {
        var (serial, device) = Device();
        var sent = Signal();
        byte[]? request = null;
        device.OnSend = packet => { request = packet; sent.TrySetResult(); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunAsync(Settings(timeout: 2000), true, default);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var write = service.WriteAsync(Settings(timeout: 2000), 10, [123], default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteAsync(Settings(), 10, [456], default));
        var stop = service.StopAsync();
        device.Emit(ReadReply(request!, [1, 2]));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await run;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Single(device.Sent);
    }

    [Fact]
    public async Task DifferentAreaClearsOldValuesSynchronouslyAndLateOldGenerationCannotPopulateIt()
    {
        var (serial, device) = Device();
        device.OnSend = packet => { device.Emit(ReadReply(packet, [7, 8])); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        await service.RunAsync(Settings(), false, default);
        var oldGeneration = device.Generation;
        device.Disconnect();
        device.Reconnect();
        device.OnSend = packet => { device.Emit(ReadReply(packet, [99, 99]), oldGeneration); return Task.CompletedTask; };
        var settings = Settings(timeout: 100);
        settings.Address = 50;
        var second = service.RunAsync(settings, false, default);
        Assert.Null(service.GetSnapshot().Values);
        Assert.Null(service.GetSnapshot().UpdatedAt);
        await Assert.ThrowsAsync<ModbusExchangeException>(() => second);
        Assert.Null(service.GetSnapshot().Values);
        Assert.False(device.Connected);
    }

    [Fact]
    public async Task ReceiveOverflowFailsClosedInsteadOfDroppingBytesAndReportingSuccess()
    {
        var (serial, device) = Device();
        device.OnSend = packet => { device.Emit(new byte[65537]); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        await Assert.ThrowsAsync<ModbusExchangeException>(() => service.RunAsync(Settings(), false, default));
        Assert.False(device.Connected);
        Assert.Null(service.GetSnapshot().Values);
    }

    [Fact]
    public async Task InvalidTargetsAndBroadcastWritesNeverReachTransport()
    {
        var (serial, device) = Device();
        await using var service = new ModbusRtuMonitorService(serial);
        var settings = Settings();
        settings.UnitId = 0;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.RunAsync(settings, false, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.WriteAsync(settings, 10, [1], default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.WriteAsync(Settings(), 9, [1], default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.WriteAsync(Settings(), 11, [1, 2], default));
        Assert.Empty(device.Sent);
    }

    private static ModbusMonitorSettings Settings(int timeout = 1000) => new()
    { Address = 10, Quantity = 2, IntervalMs = 100, TimeoutMs = timeout };

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private static byte[] ReadReply(byte[] packet, ushort[] values)
    {
        if (packet[1] <= 2)
        {
            var data = new byte[(values.Length + 7) / 8];
            for (var i = 0; i < values.Length; i++) data[i / 8] |= (byte)(values[i] << (i % 8));
            return Crc([packet[0], packet[1], (byte)data.Length, .. data]);
        }
        var payload = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(i * 2), values[i]);
        return Crc([packet[0], packet[1], (byte)payload.Length, .. payload]);
    }

    private static byte[] WriteAck(byte[] packet) => packet[1] is 5 or 6 ? packet.ToArray() : Crc(packet[..6]);

    private static byte[] Crc(byte[] data)
    {
        var result = new byte[data.Length + 2];
        data.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(data.Length), ModbusRtuCrc.Compute(data));
        return result;
    }

    private static (ISerialService Serial, TestSerial Device) Device()
    {
        var serial = DispatchProxy.Create<ISerialService, TestSerial>();
        return (serial, (TestSerial)(object)serial);
    }

    public class TestSerial : DispatchProxy
    {
        private Action<long, ReceivedByteChunk>? _received;
        private EventHandler? _status;
        public bool Connected { get; private set; } = true;
        public long Generation { get; private set; } = 1;
        public string? Owner { get; private set; }
        public bool WasOwnedWhenDisconnected { get; private set; }
        public List<byte[]> Sent { get; } = [];
        public Func<byte[], Task> OnSend { get; set; } = _ => Task.CompletedTask;

        public void Emit(byte[] bytes, long? generation = null) => _received?.Invoke(generation ?? Generation, ReceivedByteChunk.Capture(bytes));
        public void Disconnect()
        {
            WasOwnedWhenDisconnected = Owner is not null;
            Connected = false;
            Generation++;
            _status?.Invoke(this, EventArgs.Empty);
        }
        public void Reconnect() { Connected = true; Generation++; _status?.Invoke(this, EventArgs.Empty); }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            args ??= [];
            switch (targetMethod!.Name)
            {
                case "get_IsConnected": return Connected;
                case "get_ReceiveSessionGeneration": return Generation;
                case "get_AppliedBaudRate": return 115200;
                case "get_ExclusiveSessionOwner": return Owner;
                case "add_CaptureBytesReceived": _received += (Action<long, ReceivedByteChunk>)args[0]!; return null;
                case "remove_CaptureBytesReceived": _received -= (Action<long, ReceivedByteChunk>)args[0]!; return null;
                case "add_StatusChanged": _status += (EventHandler)args[0]!; return null;
                case "remove_StatusChanged": _status -= (EventHandler)args[0]!; return null;
                case "AcquireExclusiveSession":
                    if (Owner is not null || !Connected) throw new InvalidOperationException("Port busy or disconnected.");
                    Owner = (string)args[0]!;
                    return new Lease(this, Generation);
                default: return targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
            }
        }

        private sealed class Lease(TestSerial serial, long generation) : ISerialExclusiveSession
        {
            public long Generation => generation;
            public async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!serial.Connected || serial.Generation != generation) throw new IOException("Old session.");
                serial.Sent.Add(payload.ToArray());
                await serial.OnSend(payload);
            }
            public Task DisconnectAsync(CancellationToken cancellationToken)
            { if (serial.Generation == generation) serial.Disconnect(); return Task.CompletedTask; }
            public ValueTask DisposeAsync() { serial.Owner = null; return ValueTask.CompletedTask; }
        }
    }
}
