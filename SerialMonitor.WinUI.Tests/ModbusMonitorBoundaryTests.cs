using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusMonitorBoundaryTests
{
    [Fact]
    public async Task AnOperationStartedOnThePreviousConnectionCannotSendOnAReplacementAcquiredLater()
    {
        var (serial, device) = Device();
        var acquiring = Signal();
        using var releaseAcquisition = new ManualResetEventSlim();
        device.BeforeAcquire = () =>
        {
            acquiring.TrySetResult();
            if (!releaseAcquisition.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test acquisition gate timed out.");
        };
        device.Send = (packet, _) =>
        {
            device.Emit(ReadReply(packet, [10, 20]));
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        var originalGeneration = device.Generation;
        var run = service.RunAsync(Settings(), false, default);
        try
        {
            await acquiring.Task.WaitAsync(TimeSpan.FromSeconds(5));
            device.ReplaceConnection();
            var replacementGeneration = device.Generation;
            Assert.True(replacementGeneration > originalGeneration);
            releaseAcquisition.Set();

            await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Empty(device.Sent);
            Assert.True(device.Connected);
            Assert.Equal(replacementGeneration, device.Generation);
            Assert.Null(device.Owner);
            Assert.Null(service.GetSnapshot().Values);
        }
        finally { releaseAcquisition.Set(); }
    }

    [Theory]
    [InlineData(true, 115200, 8, SerialParityMode.None, SerialStopBitsMode.One, true)]
    [InlineData(false, 115200, 8, SerialParityMode.None, SerialStopBitsMode.One, false)]
    [InlineData(true, 115200, 1, SerialParityMode.None, SerialStopBitsMode.One, false)]
    [InlineData(true, 9600, 4, SerialParityMode.None, SerialStopBitsMode.One, true)]
    [InlineData(true, 9600, 4, SerialParityMode.Even, SerialStopBitsMode.Two, false)]
    public async Task ResponseAssemblyRespectsOnlyNativeIdleBoundariesQualifiedForTheAppliedFraming(
        bool nativeBoundary, int baud, int idleTimeout, SerialParityMode parity, SerialStopBitsMode stopBits, bool reject)
    {
        var (serial, device) = Device();
        device.ReadBaudRate = _ => baud;
        device.IdleTimeoutMs = idleTimeout;
        device.Configuration = new SerialPortConfiguration(baud, 8, parity, stopBits, SerialHandshakeMode.None, false, false, false);
        device.Send = (packet, _) =>
        {
            var response = ReadReply(packet, [10, 20]);
            device.Emit(response[..3], nativeBoundary);
            device.Emit(response[3..]);
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);

        if (reject)
        {
            await Assert.ThrowsAsync<ModbusExchangeException>(() => service.RunAsync(Settings(), false, default));
            Assert.Null(service.GetSnapshot().Values);
            Assert.False(device.Connected);
        }
        else
        {
            await service.RunAsync(Settings(), false, default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new ushort[] { 10, 20 }, service.GetSnapshot().Values);
            Assert.True(device.Connected);
        }
    }

    [Fact]
    public async Task DeadlineAndStopCannotReleaseOwnershipWhileAnAdmittedSendIsStillPending()
    {
        var (serial, device) = Device();
        var entered = Signal();
        var deadlineExpired = Signal();
        var release = Signal();
        device.Send = async (_, token) =>
        {
            using var registration = token.Register(() => deadlineExpired.TrySetResult());
            entered.TrySetResult();
            await release.Task;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunAsync(Settings(timeout: 100), false, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await deadlineExpired.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopping = service.StopAsync();

            Assert.False(run.IsCompleted);
            Assert.False(stopping.IsCompleted);
            Assert.True(service.IsBusy);
            Assert.NotNull(device.Owner);
            Assert.Equal(1, device.ActiveSends);

            release.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<ModbusExchangeException>(() => run);
            Assert.False(device.Connected);
            Assert.Null(device.Owner);
            Assert.False(device.ReleasedWithSendPending);
            Assert.False(device.DisconnectedWithSendPending);
            Assert.Single(device.Sent);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task CancelingAQueuedWriteAtThePreTransmitBoundaryDoesNotSendItOrStopPolling()
    {
        var (serial, device) = Device();
        var preparingWrite = Signal();
        using var releasePreparation = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        ModbusRtuMonitorService? observedService = null;
        var preparationIntercepted = 0;
        device.ReadBaudRate = _ =>
        {
            if (observedService?.GetSnapshot().WriteState == ModbusWriteState.Writing &&
                Interlocked.Exchange(ref preparationIntercepted, 1) == 0)
            {
                preparingWrite.TrySetResult();
                if (!releasePreparation.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test preparation gate timed out.");
            }
            return 115200;
        };
        device.Send = (packet, _) =>
        {
            device.Emit(ReadReply(packet, [10, 20]));
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        observedService = service;
        var settings = Settings(interval: 60000);
        var run = service.RunAsync(settings, true, default);
        try
        {
            await Until(() => service.GetSnapshot().SuccessCount == 1);
            var write = service.WriteAsync(settings, settings.Address, [99], cancellation.Token);
            await preparingWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            releasePreparation.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(TimeSpan.FromSeconds(5)));
            await Until(() => service.GetSnapshot().WriteState == ModbusWriteState.None);

            Assert.True(service.IsBusy);
            Assert.True(service.GetSnapshot().IsPolling);
            Assert.Equal(ModbusWriteState.None, service.GetSnapshot().WriteState);
            Assert.NotNull(device.Owner);
            Assert.True(device.Connected);
            Assert.Equal((byte)3, Assert.Single(device.Sent)[1]);

            await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await run;
        }
        finally
        {
            releasePreparation.Set();
            await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task StopAfterAcknowledgementBeforeReadbackPreservesTheAppliedButUnverifiedState()
    {
        var (serial, device) = Device();
        var preparingReadback = Signal();
        using var releasePreparation = new ManualResetEventSlim();
        ModbusRtuMonitorService? observedService = null;
        var preparationIntercepted = 0;
        device.ReadBaudRate = _ =>
        {
            if (observedService?.GetSnapshot().WriteState == ModbusWriteState.NotVerified &&
                Interlocked.Exchange(ref preparationIntercepted, 1) == 0)
            {
                preparingReadback.TrySetResult();
                if (!releasePreparation.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test preparation gate timed out.");
            }
            return 115200;
        };
        device.Send = (packet, _) =>
        {
            device.Emit(packet.ToArray());
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        observedService = service;
        var write = service.WriteAsync(Settings(), 10, [99], default);
        try
        {
            await preparingReadback.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ModbusWriteState.NotVerified, service.GetSnapshot().WriteState);
            var stopping = service.StopAsync();
            releasePreparation.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);

            Assert.Equal(ModbusWriteState.NotVerified, service.GetSnapshot().WriteState);
            Assert.False(service.GetSnapshot().ValuesCurrent);
            Assert.True(device.Connected);
            Assert.Null(device.Owner);
            Assert.Equal((byte)6, Assert.Single(device.Sent)[1]);
        }
        finally { releasePreparation.Set(); }
    }

    private static ModbusMonitorSettings Settings(int timeout = 1000, int interval = 100) => new()
    { Address = 10, Quantity = 2, IntervalMs = interval, TimeoutMs = timeout };

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    private static byte[] ReadReply(byte[] request, ushort[] values)
    {
        var frame = new byte[5 + values.Length * 2];
        frame[0] = request[0];
        frame[1] = request[1];
        frame[2] = (byte)(values.Length * 2);
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3 + index * 2, 2), values[index]);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frame.Length - 2), ModbusRtuCrc.Compute(frame.AsSpan(0, frame.Length - 2)));
        return frame;
    }

    private static (ISerialService Serial, BoundarySerial Device) Device()
    {
        var serial = DispatchProxy.Create<ISerialService, BoundarySerial>();
        return (serial, (BoundarySerial)(object)serial);
    }

    public class BoundarySerial : DispatchProxy
    {
        private Action<long, ReceivedByteChunk>? _received;
        private EventHandler? _status;
        private int _baudReads;
        private int _activeSends;
        public bool Connected { get; private set; } = true;
        public long Generation { get; private set; } = 1;
        public string? Owner { get; private set; }
        public int ActiveSends => Volatile.Read(ref _activeSends);
        public bool ReleasedWithSendPending { get; private set; }
        public bool DisconnectedWithSendPending { get; private set; }
        public ConcurrentQueue<byte[]> Sent { get; } = new();
        public Action? BeforeAcquire { get; set; }
        public Func<int, int> ReadBaudRate { get; set; } = _ => 115200;
        public int IdleTimeoutMs { get; set; } = 4;
        public SerialPortConfiguration Configuration { get; set; } = SerialPortConfiguration.FromSettings(new SerialSettings());
        public Func<byte[], CancellationToken, Task> Send { get; set; } = (_, _) => Task.CompletedTask;

        public void Emit(byte[] bytes, bool nativeBoundary = false) =>
            _received?.Invoke(Generation, ReceivedByteChunk.CaptureAt(bytes, System.Diagnostics.Stopwatch.GetTimestamp(), nativeBoundary));

        public void ReplaceConnection()
        {
            Connected = false;
            Generation++;
            _status?.Invoke(this, EventArgs.Empty);
            Connected = true;
            Generation++;
            _status?.Invoke(this, EventArgs.Empty);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            args ??= [];
            switch (targetMethod!.Name)
            {
                case "get_IsConnected": return Connected;
                case "get_ReceiveSessionGeneration": return Generation;
                case "get_AppliedBaudRate": return ReadBaudRate(Interlocked.Increment(ref _baudReads));
                case "get_AppliedReceiveIdleTimeoutMs": return IdleTimeoutMs;
                case "get_UsesNativeReceiveIdleTimeout": return true;
                case "get_AppliedConfiguration": return Configuration;
                case "get_ExclusiveSessionOwner": return Owner;
                case "add_CaptureBytesReceived": _received += (Action<long, ReceivedByteChunk>)args[0]!; return null;
                case "remove_CaptureBytesReceived": _received -= (Action<long, ReceivedByteChunk>)args[0]!; return null;
                case "add_StatusChanged": _status += (EventHandler)args[0]!; return null;
                case "remove_StatusChanged": _status -= (EventHandler)args[0]!; return null;
                case "AcquireExclusiveSession":
                    BeforeAcquire?.Invoke();
                    if (Owner is not null || !Connected) throw new InvalidOperationException("Port busy or disconnected.");
                    Owner = (string)args[0]!;
                    return new Lease(this, Generation);
                default: return targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
            }
        }

        private sealed class Lease(BoundarySerial serial, long generation) : ISerialExclusiveSession
        {
            public long Generation => generation;
            public async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!serial.Connected || serial.Generation != generation) throw new IOException("Old session.");
                Interlocked.Increment(ref serial._activeSends);
                try
                {
                    serial.Sent.Enqueue(payload.ToArray());
                    await serial.Send(payload, cancellationToken);
                }
                finally { Interlocked.Decrement(ref serial._activeSends); }
            }
            public Task DisconnectAsync(CancellationToken cancellationToken)
            {
                if (serial.Generation == generation)
                {
                    serial.DisconnectedWithSendPending |= serial.ActiveSends != 0;
                    serial.Connected = false;
                    serial.Generation++;
                    serial._status?.Invoke(this, EventArgs.Empty);
                }
                return Task.CompletedTask;
            }
            public ValueTask DisposeAsync()
            {
                serial.ReleasedWithSendPending |= serial.ActiveSends != 0;
                serial.Owner = null;
                return ValueTask.CompletedTask;
            }
        }
    }
}
