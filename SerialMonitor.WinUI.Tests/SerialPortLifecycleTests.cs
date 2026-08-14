using System.Collections.Concurrent;
using System.Diagnostics;
using RJCP.IO.Ports;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

[Collection(StabilityIsolationCollection.Name)]
public sealed class SerialPortLifecycleTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(60);

    [Fact]
    public async Task FactoryBlockedBeforeReturn_TimesOutAndLatePortIsCleanedExactlyOnce()
    {
        using var releaseFactory = new ManualResetEventSlim(initialState: false);
        using var factoryEntered = new ManualResetEventSlim(initialState: false);
        var port = new ControlledSerialPort();
        await using var service = CreateService((_, _) =>
        {
            factoryEntered.Set();
            releaseFactory.Wait();
            return port;
        });
        var connectedPublished = false;
        service.StatusChanged += (_, _) => connectedPublished |= service.IsConnected;

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var connect = service.ConnectAsync(Settings(), ReceiveOptions(), CancellationToken.None);
            Assert.True(factoryEntered.Wait(TimeSpan.FromSeconds(2)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => connect);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.False(connectedPublished);
            Assert.False(service.IsConnected);
            Assert.Equal(1, service.OutstandingPortOperationCount);
            Assert.Equal(0, port.DisposeCallCount);
        }
        finally
        {
            releaseFactory.Set();
        }

        await WaitUntilAsync(() => service.OutstandingPortOperationCount == 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, port.OpenCallCount);
        Assert.Equal(1, port.CloseCallCount);
        Assert.Equal(1, port.DisposeCallCount);
        Assert.False(connectedPublished);
    }

    [Fact]
    public async Task OpenBlockedAfterEntry_HardTimeoutOwnsLateSuccessExactlyOnce()
    {
        var port = new ControlledSerialPort(blockOpen: true);
        await using var service = CreateService((_, _) => port);
        var connectedPublished = false;
        service.StatusChanged += (_, _) => connectedPublished |= service.IsConnected;

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var connect = service.ConnectAsync(Settings(), ReceiveOptions(), CancellationToken.None);
            Assert.True(port.OpenEntered.Wait(TimeSpan.FromSeconds(2)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => connect);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.False(service.IsConnected);
            Assert.False(connectedPublished);
            Assert.Equal(1, service.OutstandingPortOperationCount);
        }
        finally
        {
            port.ReleaseAll();
        }

        await WaitUntilAsync(() => service.OutstandingPortOperationCount == 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, port.CloseCallCount);
        Assert.Equal(1, port.DisposeCallCount);
        Assert.False(connectedPublished);
    }

    [Fact]
    public async Task ManualDisconnect_CancelsBlockedOpenBeforeLifecycleGateAndNeverPublishesLateSuccess()
    {
        var port = new ControlledSerialPort(blockOpen: true);
        await using var service = CreateService((_, _) => port, openTimeout: TimeSpan.FromSeconds(5));
        var states = new ConcurrentQueue<SerialConnectionState>();
        service.StatusChanged += (_, _) => states.Enqueue(service.ConnectionState);

        try
        {
            var connect = service.ConnectAsync(Settings(), ReceiveOptions(), CancellationToken.None);
            Assert.True(port.OpenEntered.Wait(TimeSpan.FromSeconds(2)));

            var stopwatch = Stopwatch.StartNew();
            var disconnect = service.DisconnectAsync(CancellationToken.None);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
            await disconnect.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.False(service.IsConnected);
            Assert.DoesNotContain(SerialConnectionState.Connected, states);
            Assert.Equal(0, port.DisposeCallCount);
        }
        finally
        {
            port.ReleaseAll();
        }

        await WaitUntilAsync(() => service.OutstandingPortOperationCount == 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, port.CloseCallCount);
        Assert.Equal(1, port.DisposeCallCount);
        Assert.DoesNotContain(SerialConnectionState.Connected, states);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Disconnect_CloseOrDisposeSyncBlock_ReturnsWithinHardDeadline(
        bool blockClose,
        bool blockDispose)
    {
        var port = new ControlledSerialPort(blockClose: blockClose, blockDispose: blockDispose);
        await using var service = CreateService((_, _) => port);
        await service.ConnectAsync(Settings(), ReceiveOptions(), CancellationToken.None);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            await service.DisconnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(800));
            Assert.False(service.IsConnected);
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
    public async Task StuckOpenOperations_ReachCapRejectNewConnectAndRecoverAfterRelease()
    {
        var ports = Enumerable.Range(0, 3)
            .Select(_ => new ControlledSerialPort(blockOpen: true))
            .ToArray();
        var successfulPort = new ControlledSerialPort();
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
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.ConnectAsync(Settings(), ReceiveOptions(), CancellationToken.None));
                Assert.True(port.OpenEntered.Wait(TimeSpan.FromSeconds(1)));
            }

            Assert.Equal(3, service.OutstandingPortOperationCount);
            var callsBeforeRejectedStart = factoryCalls;
            var capacityError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ConnectAsync(Settings(), ReceiveOptions(), CancellationToken.None));
            Assert.IsType<PortOperationCapacityException>(capacityError.InnerException);
            Assert.Equal(callsBeforeRejectedStart, factoryCalls);
            Assert.False(service.IsConnected);
        }
        finally
        {
            foreach (var port in ports)
            {
                port.ReleaseAll();
            }
        }

        await WaitUntilAsync(() => service.OutstandingPortOperationCount == 0, TimeSpan.FromSeconds(2));
        await service.ConnectAsync(Settings(), ReceiveOptions(), CancellationToken.None);
        Assert.True(service.IsConnected);
        await service.DisconnectAsync(CancellationToken.None);
        Assert.Equal(1, successfulPort.DisposeCallCount);
    }

    private static SerialService CreateService(
        Func<SerialSettings, SerialReceiveOptions, ISerialPortConnection> factory,
        TimeSpan? openTimeout = null,
        int maximumOutstandingOperations = 4) => new(
            factory,
            openTimeout ?? ShortTimeout,
            ShortTimeout,
            maximumOutstandingOperations);

    private static SerialSettings Settings() => new()
    {
        PortName = "COM_TEST",
        BaudRate = 115200
    };

    private static SerialReceiveOptions ReceiveOptions() => new();

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException("The serial lifecycle state did not settle.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class ControlledSerialPort : ISerialPortConnection
    {
        private readonly ManualResetEventSlim _releaseOpen;
        private readonly ManualResetEventSlim _releaseClose;
        private readonly ManualResetEventSlim _releaseDispose;

        public ControlledSerialPort(
            bool blockOpen = false,
            bool blockClose = false,
            bool blockDispose = false)
        {
            _releaseOpen = new ManualResetEventSlim(!blockOpen);
            _releaseClose = new ManualResetEventSlim(!blockClose);
            _releaseDispose = new ManualResetEventSlim(!blockDispose);
        }

        public event EventHandler<SerialErrorReceivedEventArgs>? ErrorReceived
        {
            add { }
            remove { }
        }

        public ManualResetEventSlim OpenEntered { get; } = new(false);

        public int OpenCallCount => Volatile.Read(ref _openCallCount);
        public int CloseCallCount => Volatile.Read(ref _closeCallCount);
        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        private int _openCallCount;
        private int _closeCallCount;
        private int _disposeCallCount;

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

        public NativeReadCompletion ReadNativeCompletion(CancellationToken cancellationToken)
        {
            cancellationToken.WaitHandle.WaitOne();
            throw new OperationCanceledException(cancellationToken);
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
