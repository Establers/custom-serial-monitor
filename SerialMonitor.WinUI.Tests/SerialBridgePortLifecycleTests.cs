using System.Collections.Concurrent;
using System.Diagnostics;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

[Collection(StabilityIsolationCollection.Name)]
public sealed class SerialBridgePortLifecycleTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(60);

    [Fact]
    public async Task FactoryBlockedBeforeReturn_TimesOutAndLatePortIsCleanedExactlyOnce()
    {
        using var releaseFactory = new ManualResetEventSlim(initialState: false);
        using var factoryEntered = new ManualResetEventSlim(initialState: false);
        var port = new ControlledBridgePort();
        await using var service = CreateService((_, _) =>
        {
            factoryEntered.Set();
            releaseFactory.Wait();
            return port;
        });
        var runningPublished = false;
        service.StatusChanged += (_, _) => runningPublished |= service.IsRunning;

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var start = service.StartAsync(
                BridgeSettings(),
                SerialSettings(),
                (_, _) => Task.CompletedTask,
                CancellationToken.None);
            Assert.True(factoryEntered.Wait(TimeSpan.FromSeconds(2)));
            await Assert.ThrowsAsync<TimeoutException>(() => start);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.False(service.IsRunning);
            Assert.False(runningPublished);
            Assert.Equal(1, service.OutstandingPortOperationCount);
        }
        finally
        {
            releaseFactory.Set();
        }

        await WaitUntilAsync(() => service.OutstandingPortOperationCount == 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, port.OpenCallCount);
        Assert.Equal(1, port.CloseCallCount);
        Assert.Equal(1, port.DisposeCallCount);
        Assert.False(runningPublished);
    }

    [Fact]
    public async Task OpenBlockedAfterEntry_TimesOutAndLateSuccessNeverPublishesRunning()
    {
        var port = new ControlledBridgePort(blockOpen: true);
        await using var service = CreateService((_, _) => port);
        var runningPublished = false;
        service.StatusChanged += (_, _) => runningPublished |= service.IsRunning;

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var start = service.StartAsync(
                BridgeSettings(),
                SerialSettings(),
                (_, _) => Task.CompletedTask,
                CancellationToken.None);
            Assert.True(port.OpenEntered.Wait(TimeSpan.FromSeconds(2)));
            await Assert.ThrowsAsync<TimeoutException>(() => start);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.False(service.IsRunning);
            Assert.False(runningPublished);
            Assert.Equal(1, service.OutstandingPortOperationCount);
            Assert.Equal(0, port.DisposeCallCount);
        }
        finally
        {
            port.ReleaseAll();
        }

        await WaitUntilAsync(() => service.OutstandingPortOperationCount == 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, port.CloseCallCount);
        Assert.Equal(1, port.DisposeCallCount);
        Assert.False(runningPublished);
    }

    [Fact]
    public async Task Stop_CancelsBlockedStartBeforeLifecycleGateAndOwnsLateResultOnce()
    {
        var port = new ControlledBridgePort(blockOpen: true);
        await using var service = CreateService((_, _) => port, openTimeout: TimeSpan.FromSeconds(5));
        var runningStates = new ConcurrentQueue<bool>();
        service.StatusChanged += (_, _) => runningStates.Enqueue(service.IsRunning);

        try
        {
            var start = service.StartAsync(
                BridgeSettings(),
                SerialSettings(),
                (_, _) => Task.CompletedTask,
                CancellationToken.None);
            Assert.True(port.OpenEntered.Wait(TimeSpan.FromSeconds(2)));

            var stopwatch = Stopwatch.StartNew();
            var stop = service.StopAsync(CancellationToken.None);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            await stop.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.False(service.IsRunning);
            Assert.DoesNotContain(true, runningStates);
            Assert.Equal(0, port.DisposeCallCount);
        }
        finally
        {
            port.ReleaseAll();
        }

        await WaitUntilAsync(() => service.OutstandingPortOperationCount == 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, port.CloseCallCount);
        Assert.Equal(1, port.DisposeCallCount);
        Assert.DoesNotContain(true, runningStates);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Stop_CloseOrDisposeSyncBlock_ReturnsWithinHardDeadline(
        bool blockClose,
        bool blockDispose)
    {
        var port = new ControlledBridgePort(blockClose: blockClose, blockDispose: blockDispose);
        await using var service = CreateService((_, _) => port);
        await service.StartAsync(
            BridgeSettings(),
            SerialSettings(),
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(800));
            Assert.False(service.IsRunning);
            Assert.Equal(1, port.CloseCallCount);
            if (blockClose)
            {
                Assert.Equal(0, port.DisposeCallCount);
            }
            else
            {
                Assert.Equal(1, port.DisposeCallCount);
            }

            Assert.Equal(1, service.OutstandingPortOperationCount);
        }
        finally
        {
            port.ReleaseAll();
        }

        await WaitUntilAsync(() => service.OutstandingPortOperationCount == 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, port.CloseCallCount);
        Assert.Equal(1, port.DisposeCallCount);
    }

    [Fact]
    public async Task StuckOpenOperations_ReachCapRejectNewStartAndRecoverAfterRelease()
    {
        var ports = Enumerable.Range(0, 3)
            .Select(_ => new ControlledBridgePort(blockOpen: true))
            .ToArray();
        var successfulPort = new ControlledBridgePort();
        var factoryCalls = 0;
        await using var service = CreateService(
            (_, _) =>
            {
                var index = Interlocked.Increment(ref factoryCalls) - 1;
                return index < ports.Length ? ports[index] : successfulPort;
            },
            openTimeout: TimeSpan.FromMilliseconds(35),
            maximumOutstandingOperations: 4);

        try
        {
            foreach (var port in ports)
            {
                await Assert.ThrowsAsync<TimeoutException>(() => service.StartAsync(
                    BridgeSettings(),
                    SerialSettings(),
                    (_, _) => Task.CompletedTask,
                    CancellationToken.None));
                Assert.True(port.OpenEntered.Wait(TimeSpan.FromSeconds(1)));
            }

            Assert.Equal(3, service.OutstandingPortOperationCount);
            var callsBeforeRejectedStart = factoryCalls;
            await Assert.ThrowsAsync<PortOperationCapacityException>(() => service.StartAsync(
                BridgeSettings(),
                SerialSettings(),
                (_, _) => Task.CompletedTask,
                CancellationToken.None));
            Assert.Equal(callsBeforeRejectedStart, factoryCalls);
            Assert.False(service.IsRunning);
        }
        finally
        {
            foreach (var port in ports)
            {
                port.ReleaseAll();
            }
        }

        await WaitUntilAsync(() => service.OutstandingPortOperationCount == 0, TimeSpan.FromSeconds(2));
        await service.StartAsync(
            BridgeSettings(),
            SerialSettings(),
            (_, _) => Task.CompletedTask,
            CancellationToken.None);
        Assert.True(service.IsRunning);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(1, successfulPort.DisposeCallCount);
    }

    private static SerialBridgeService CreateService(
        Func<string, SerialSettings, IBridgePortConnection> factory,
        TimeSpan? openTimeout = null,
        int maximumOutstandingOperations = 4) => new(
            new SystemBridgeClock(),
            factory,
            openTimeout ?? ShortTimeout,
            ShortTimeout,
            maximumOutstandingOperations);

    private static BridgeSettings BridgeSettings() => new()
    {
        Enabled = true,
        VirtualPortName = "COM_BRIDGE_TEST"
    };

    private static SerialSettings SerialSettings() => new()
    {
        PortName = "COM_DEVICE_TEST",
        BaudRate = 115200
    };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException("The bridge lifecycle state did not settle.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class ControlledBridgePort : IBridgePortConnection
    {
        private readonly ManualResetEventSlim _releaseOpen;
        private readonly ManualResetEventSlim _releaseClose;
        private readonly ManualResetEventSlim _releaseDispose;
        private int _openCallCount;
        private int _closeCallCount;
        private int _disposeCallCount;

        public ControlledBridgePort(
            bool blockOpen = false,
            bool blockClose = false,
            bool blockDispose = false)
        {
            _releaseOpen = new ManualResetEventSlim(!blockOpen);
            _releaseClose = new ManualResetEventSlim(!blockClose);
            _releaseDispose = new ManualResetEventSlim(!blockDispose);
        }

        public ManualResetEventSlim OpenEntered { get; } = new(false);

        public int OpenCallCount => Volatile.Read(ref _openCallCount);
        public int CloseCallCount => Volatile.Read(ref _closeCallCount);
        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        public void Open()
        {
            Interlocked.Increment(ref _openCallCount);
            OpenEntered.Set();
            _releaseOpen.Wait();
        }

        public void Close()
        {
            Interlocked.Increment(ref _closeCallCount);
            _releaseClose.Wait();
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCallCount);
            _releaseDispose.Wait();
        }

        public async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void ReleaseAll()
        {
            _releaseOpen.Set();
            _releaseClose.Set();
            _releaseDispose.Set();
        }
    }
}
