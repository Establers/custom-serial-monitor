using System.Collections.Concurrent;
using System.Diagnostics;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

[Collection(StabilityIsolationCollection.Name)]
public sealed class SerialBridgeSessionIsolationTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(60);

    [Fact]
    public async Task EstablishedSessionCancellation_StopsWorkersBeforeGatedReadCanPublish()
    {
        var port = new GatedBridgePort([0xA1], ignoreReadCancellation: true);
        var deviceWrites = new ConcurrentQueue<byte[]>();
        using var sessionCancellation = new CancellationTokenSource();
        await using var service = CreateService((_, _) => port);

        await service.StartAsync(
            BridgeSettings(),
            DeviceSettings(),
            (bytes, _) =>
            {
                deviceWrites.Enqueue(bytes.ToArray());
                return Task.CompletedTask;
            },
            sessionCancellation.Token);
        await port.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        sessionCancellation.Cancel();
        port.ReleaseRead();
        await port.ReadReturned.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Empty(deviceWrites);
        Assert.False(service.IsRunning);
        Assert.Equal(0, service.OutstandingBridgeSessionCount);
        Assert.Contains("graceful stop", service.LastStopMode, StringComparison.Ordinal);
        Assert.Equal(1, port.CloseCallCount);
        Assert.Equal(1, port.DisposeCallCount);
    }

    [Fact]
    public async Task BeginStop_ImmediatelyRejectsInputAndOwnsBoundedBackgroundCleanup()
    {
        var port = new GatedBridgePort(
            [0xA1],
            ignoreReadCancellation: true,
            blockClose: true);
        var deviceWrites = new ConcurrentQueue<byte[]>();
        await using var service = CreateService((_, _) => port);

        try
        {
            await service.StartAsync(
                BridgeSettings(),
                DeviceSettings(),
                (bytes, _) =>
                {
                    deviceWrites.Enqueue(bytes.ToArray());
                    return Task.CompletedTask;
                },
                CancellationToken.None);
            await port.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

            service.BeginStop();
            service.BeginStop();

            Assert.False(service.IsRunning);
            Assert.False(service.TryEnqueueDeviceChunk(Chunk([0xB2])));
            await port.CloseEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1, port.CloseCallCount);
            Assert.Equal(0, port.DisposeCallCount);

            port.ReleaseClose();
            await WaitUntilAsync(() => port.DisposeCallCount == 1, TimeSpan.FromSeconds(1));
            port.ReleaseRead();
            await port.ReadReturned.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Empty(deviceWrites);
            Assert.False(service.IsRunning);
            Assert.Equal(0, service.OutstandingBridgeSessionCount);
            Assert.Contains("graceful stop", service.LastStopMode, StringComparison.Ordinal);
            Assert.Equal(1, port.CloseCallCount);
            Assert.Equal(1, port.DisposeCallCount);
        }
        finally
        {
            port.ReleaseClose();
            port.ReleaseRead();
        }
    }

    [Fact]
    public async Task PreviousSessionCancellation_DoesNotCancelReplacementSession()
    {
        var oldPort = new GatedBridgePort(readBytes: null);
        var newPort = new GatedBridgePort([0xB2]);
        var ports = new ConcurrentQueue<GatedBridgePort>([oldPort, newPort]);
        var newDeviceWrites = new ConcurrentQueue<byte[]>();
        using var oldSessionCancellation = new CancellationTokenSource();
        using var newSessionCancellation = new CancellationTokenSource();
        await using var service = CreateService((_, _) =>
            ports.TryDequeue(out var port) ? port : throw new InvalidOperationException("No bridge port configured."));

        await service.StartAsync(
            BridgeSettings(),
            DeviceSettings(),
            (_, _) => Task.CompletedTask,
            oldSessionCancellation.Token);
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        await service.StartAsync(
            BridgeSettings(),
            DeviceSettings(),
            (bytes, _) =>
            {
                newDeviceWrites.Enqueue(bytes.ToArray());
                return Task.CompletedTask;
            },
            newSessionCancellation.Token);
        await newPort.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        oldSessionCancellation.Cancel();
        newPort.ReleaseRead();
        await WaitUntilAsync(() => newDeviceWrites.Count == 1, TimeSpan.FromSeconds(1));

        Assert.Equal([0xB2], newDeviceWrites.Single());
        Assert.True(service.IsRunning);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(1, oldPort.CloseCallCount);
        Assert.Equal(1, oldPort.DisposeCallCount);
        Assert.Equal(1, newPort.CloseCallCount);
        Assert.Equal(1, newPort.DisposeCallCount);
    }

    [Fact]
    public async Task ForcedOldReaderResume_CannotEnqueueIntoNewDeviceSession()
    {
        var oldPort = new GatedBridgePort([0xA1], ignoreReadCancellation: true);
        var newPort = new GatedBridgePort([0xB2]);
        var ports = new ConcurrentQueue<GatedBridgePort>([oldPort, newPort]);
        var oldDeviceWrites = new ConcurrentQueue<byte[]>();
        var newDeviceWrites = new ConcurrentQueue<byte[]>();
        await using var service = CreateService((_, _) =>
            ports.TryDequeue(out var port) ? port : throw new InvalidOperationException("No bridge port configured."));

        await service.StartAsync(
            BridgeSettings(),
            DeviceSettings(),
            (bytes, _) =>
            {
                oldDeviceWrites.Enqueue(bytes.ToArray());
                return Task.CompletedTask;
            },
            CancellationToken.None);
        await oldPort.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, service.OutstandingBridgeSessionCount);
        Assert.Contains("forced stop", service.LastStopMode, StringComparison.Ordinal);

        await service.StartAsync(
            BridgeSettings(),
            DeviceSettings(),
            (bytes, _) =>
            {
                newDeviceWrites.Enqueue(bytes.ToArray());
                return Task.CompletedTask;
            },
            CancellationToken.None);
        newPort.ReleaseRead();
        await WaitUntilAsync(() => newDeviceWrites.Count == 1, TimeSpan.FromSeconds(1));

        oldPort.ReleaseRead();
        await WaitUntilAsync(() => service.OutstandingBridgeSessionCount == 0, TimeSpan.FromSeconds(1));
        await Task.Delay(20);
        Assert.Empty(oldDeviceWrites);
        Assert.Single(newDeviceWrites);
        Assert.Equal([0xB2], newDeviceWrites.Single());
        Assert.True(service.IsRunning);

        await service.StopAsync(CancellationToken.None);
        Assert.Equal(1, oldPort.CloseCallCount);
        Assert.Equal(1, oldPort.DisposeCallCount);
        Assert.Equal(1, newPort.CloseCallCount);
        Assert.Equal(1, newPort.DisposeCallCount);
    }

    [Fact]
    public async Task ForcedOldWriterResume_UsesOnlyOldQueueAndOldPort()
    {
        var oldPort = new GatedBridgePort(readBytes: null, blockWriteIgnoringCancellation: true);
        var newPort = new GatedBridgePort(readBytes: null);
        var ports = new ConcurrentQueue<GatedBridgePort>([oldPort, newPort]);
        await using var service = CreateService((_, _) =>
            ports.TryDequeue(out var port) ? port : throw new InvalidOperationException("No bridge port configured."));

        await service.StartAsync(
            BridgeSettings(),
            DeviceSettings(),
            (_, _) => Task.CompletedTask,
            CancellationToken.None);
        Assert.True(service.TryEnqueueDeviceChunk(Chunk([0x11])));
        await oldPort.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, service.OutstandingBridgeSessionCount);

        await service.StartAsync(
            BridgeSettings(),
            DeviceSettings(),
            (_, _) => Task.CompletedTask,
            CancellationToken.None);
        Assert.True(service.TryEnqueueDeviceChunk(Chunk([0x22])));
        await WaitUntilAsync(() => newPort.Writes.Count == 1, TimeSpan.FromSeconds(1));

        oldPort.ReleaseWrite();
        await WaitUntilAsync(() => service.OutstandingBridgeSessionCount == 0, TimeSpan.FromSeconds(1));
        Assert.Single(oldPort.Writes);
        Assert.Equal([0x11], oldPort.Writes.Single());
        Assert.Single(newPort.Writes);
        Assert.Equal([0x22], newPort.Writes.Single());
        Assert.True(service.IsRunning);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StuckBridgeSessions_ReachInstanceCap_RejectStartUntilOldWorkersExit()
    {
        var stuckPorts = Enumerable.Range(0, SerialBridgeService.MaximumOutstandingBridgeSessionCount)
            .Select(index => new GatedBridgePort([(byte)index], ignoreReadCancellation: true))
            .ToArray();
        var recoveryPort = new GatedBridgePort(readBytes: null);
        var factoryCalls = 0;
        await using var service = CreateService((_, _) =>
        {
            var index = Interlocked.Increment(ref factoryCalls) - 1;
            return index < stuckPorts.Length ? stuckPorts[index] : recoveryPort;
        });

        try
        {
            foreach (var port in stuckPorts)
            {
                await service.StartAsync(
                    BridgeSettings(),
                    DeviceSettings(),
                    (_, _) => Task.CompletedTask,
                    CancellationToken.None);
                await port.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
                await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            }

            Assert.Equal(SerialBridgeService.MaximumOutstandingBridgeSessionCount, service.OutstandingBridgeSessionCount);
            var callsBeforeRejectedStart = factoryCalls;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(
                BridgeSettings(),
                DeviceSettings(),
                (_, _) => Task.CompletedTask,
                CancellationToken.None));
            Assert.Contains("cleanup capacity", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(callsBeforeRejectedStart, factoryCalls);
        }
        finally
        {
            foreach (var port in stuckPorts)
            {
                port.ReleaseRead();
            }
        }

        await WaitUntilAsync(() => service.OutstandingBridgeSessionCount == 0, TimeSpan.FromSeconds(2));
        await service.StartAsync(
            BridgeSettings(),
            DeviceSettings(),
            (_, _) => Task.CompletedTask,
            CancellationToken.None);
        Assert.True(service.IsRunning);
        await service.StopAsync(CancellationToken.None);
    }

    private static SerialBridgeService CreateService(
        Func<string, SerialSettings, IBridgePortConnection> factory) => new(
            new SystemBridgeClock(),
            factory,
            ShortTimeout,
            ShortTimeout,
            maximumOutstandingOperations: 8);

    private static BridgeSettings BridgeSettings() => new()
    {
        Enabled = true,
        VirtualPortName = "COM_BRIDGE_SESSION",
        MaxQueuedChunks = 32,
        MaxQueuedBytes = 64 * 1024
    };

    private static SerialSettings DeviceSettings() => new()
    {
        PortName = "COM_DEVICE_SESSION",
        BaudRate = 115200
    };

    private static BridgeRxChunk Chunk(byte[] bytes) => new(
        bytes,
        Stopwatch.GetTimestamp(),
        EndsAtNativeIdleBoundary: false,
        AppliedIdleTimeoutMs: 0);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException("The bridge session did not settle.");
            }

            await Task.Delay(5);
        }
    }

    private sealed class GatedBridgePort : IBridgePortConnection
    {
        private readonly byte[]? _readBytes;
        private readonly bool _ignoreReadCancellation;
        private readonly bool _blockWriteIgnoringCancellation;
        private readonly ManualResetEventSlim _releaseClose;
        private readonly TaskCompletionSource _releaseRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;
        private int _closeCallCount;
        private int _disposeCallCount;

        public GatedBridgePort(
            byte[]? readBytes,
            bool ignoreReadCancellation = false,
            bool blockWriteIgnoringCancellation = false,
            bool blockClose = false)
        {
            _readBytes = readBytes;
            _ignoreReadCancellation = ignoreReadCancellation;
            _blockWriteIgnoringCancellation = blockWriteIgnoringCancellation;
            _releaseClose = new ManualResetEventSlim(!blockClose);
            if (readBytes is null)
            {
                _releaseRead.TrySetResult();
            }

            if (!blockWriteIgnoringCancellation)
            {
                _releaseWrite.TrySetResult();
            }
        }

        public TaskCompletionSource ReadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WriteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CloseEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<byte[]> Writes { get; } = new();

        public int CloseCallCount => Volatile.Read(ref _closeCallCount);

        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        public void Open() { }

        public void Close()
        {
            Interlocked.Increment(ref _closeCallCount);
            CloseEntered.TrySetResult();
            _releaseClose.Wait();
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCallCount);

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_readBytes is not null && Interlocked.Increment(ref _readCount) == 1)
            {
                ReadEntered.TrySetResult();
                if (_ignoreReadCancellation)
                {
                    await _releaseRead.Task;
                }
                else
                {
                    await _releaseRead.Task.WaitAsync(cancellationToken);
                }

                _readBytes.CopyTo(buffer);
                ReadReturned.TrySetResult();
                return _readBytes.Length;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            WriteEntered.TrySetResult();
            if (_blockWriteIgnoringCancellation)
            {
                await _releaseWrite.Task;
            }
            else
            {
                await _releaseWrite.Task.WaitAsync(cancellationToken);
            }

            Writes.Enqueue(buffer.ToArray());
        }

        public void ReleaseRead() => _releaseRead.TrySetResult();

        public void ReleaseWrite() => _releaseWrite.TrySetResult();

        public void ReleaseClose() => _releaseClose.Set();
    }
}
