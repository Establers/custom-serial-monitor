using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using RJCP.IO.Ports;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

[Collection(StabilityIsolationCollection.Name)]
public sealed class SerialReceiveSessionIsolationTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(60);

    [Fact]
    public async Task ReceiveWorker_WaitsForConnectedCommit_AndImmediateReadFailureRemainsFaulted()
    {
        var commitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerCommitWaitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var port = new ImmediateFailingSerialPort();
        await using var service = CreateService(
            (_, _) => port,
            beforeConnectedCommitAsync: async (_, cancellationToken) =>
            {
                commitEntered.TrySetResult();
                await releaseCommit.Task.WaitAsync(cancellationToken);
            },
            receiveWorkerCommitWaitEntered: _ => workerCommitWaitEntered.TrySetResult());

        var connect = service.ConnectAsync(Settings(), new SerialReceiveOptions(), CancellationToken.None);
        await commitEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await workerCommitWaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(SerialConnectionState.Connecting, service.ConnectionState);
        Assert.Equal(0, port.ReadCallCount);
        Assert.False(port.ReadEntered.Task.IsCompleted);

        releaseCommit.TrySetResult();
        await connect.WaitAsync(TimeSpan.FromSeconds(1));
        await port.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(
            () => service.ConnectionState == SerialConnectionState.Faulted,
            TimeSpan.FromSeconds(1));

        Assert.Contains("controlled immediate receive failure", service.LastError, StringComparison.Ordinal);
        Assert.Equal(1, service.ConnectionErrorCount);
        Assert.False(service.IsConnected);
    }

    [Fact]
    public async Task ConnectCanceledBeforeCommit_CleansSessionAndReturnsToDisconnected()
    {
        using var connectCancellation = new CancellationTokenSource();
        var commitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerCommitWaitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var port = new GatedSerialPort("unreachable"u8.ToArray());
        await using var service = CreateService(
            (_, _) => port,
            beforeConnectedCommitAsync: (_, _) =>
            {
                commitEntered.TrySetResult();
                connectCancellation.Cancel();
                return ValueTask.CompletedTask;
            },
            receiveWorkerCommitWaitEntered: _ => workerCommitWaitEntered.TrySetResult());

        var connect = service.ConnectAsync(
            Settings(),
            new SerialReceiveOptions(),
            connectCancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        await commitEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await workerCommitWaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var sessionReader = service.ReceivedBytes;
        Assert.Equal(SerialConnectionState.Disconnected, service.ConnectionState);
        Assert.False(service.IsConnected);
        Assert.Null(service.LastError);
        Assert.False(port.ReadEntered.Task.IsCompleted);
        Assert.Equal(1, port.CloseCallCount);
        Assert.Equal(1, port.DisposeCallCount);
        Assert.Equal(0, service.OutstandingReceiveSessionCount);
        Assert.False(await sessionReader.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        await sessionReader.Completion.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Disconnect_ReadCompletedBeforePublish_DrainsOldReaderBeforeCompletionThirtyTimes()
    {
        for (var iteration = 0; iteration < 30; iteration++)
        {
            var publishEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var port = new GatedSerialPort(Encoding.ASCII.GetBytes($"tail-{iteration:D2}\r\n"));
            await using var service = CreateService(
                (_, _) => port,
                stopTimeout: TimeSpan.FromSeconds(1),
                beforePublishAsync: async (_, forceAbortToken) =>
                {
                    publishEntered.TrySetResult();
                    await releasePublish.Task.WaitAsync(forceAbortToken);
                });

            await service.ConnectAsync(Settings(), new SerialReceiveOptions(), CancellationToken.None);
            var sessionReader = service.ReceivedBytes;
            await publishEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var disconnect = service.DisconnectAsync(CancellationToken.None);
            await Task.Delay(10);
            Assert.False(disconnect.IsCompleted);
            releasePublish.TrySetResult();
            await disconnect.WaitAsync(TimeSpan.FromSeconds(1));

            var chunk = await sessionReader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal($"tail-{iteration:D2}\r\n", Encoding.ASCII.GetString(chunk.Bytes));
            Assert.False(await sessionReader.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(1, service.ReceivedChunkCount);
            Assert.Equal("graceful drain", service.LastReceiveStopMode);
        }
    }

    [Fact]
    public async Task ForcedOldReceiverResume_CannotWriteOrCompleteNewSession()
    {
        var oldPort = new GatedSerialPort(Encoding.ASCII.GetBytes("OLD\r\n"), ignoreReadCancellation: true);
        var newPort = new GatedSerialPort(Encoding.ASCII.GetBytes("NEW\r\n"));
        newPort.ReleaseRead();
        var ports = new ConcurrentQueue<GatedSerialPort>([oldPort, newPort]);
        var rawChunks = new ConcurrentQueue<byte[]>();
        await using var service = CreateService((_, _) =>
            ports.TryDequeue(out var port) ? port : throw new InvalidOperationException("No port configured."));
        service.RawBytesReceived += chunk => rawChunks.Enqueue(chunk.Bytes.ToArray());

        await service.ConnectAsync(Settings(), new SerialReceiveOptions(), CancellationToken.None);
        var oldReader = service.ReceivedBytes;
        await oldPort.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await service.DisconnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, service.OutstandingReceiveSessionCount);
        Assert.Contains("forced abort", service.LastReceiveStopMode, StringComparison.Ordinal);

        await service.ConnectAsync(Settings(), new SerialReceiveOptions(), CancellationToken.None);
        var newReader = service.ReceivedBytes;
        var newChunk = await newReader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("NEW\r\n", Encoding.ASCII.GetString(newChunk.Bytes));
        service.SetRawBridgePriorityEnabled(true);

        oldPort.ReleaseRead();
        await WaitUntilAsync(() => service.OutstandingReceiveSessionCount == 0, TimeSpan.FromSeconds(1));
        await Task.Delay(20);
        Assert.False(newReader.TryRead(out _));
        Assert.False(newReader.Completion.IsCompleted);
        Assert.True(oldReader.Completion.IsCompleted);
        Assert.Empty(rawChunks);

        await service.DisconnectAsync(CancellationToken.None);
        Assert.Equal(1, oldPort.CloseCallCount);
        Assert.Equal(1, oldPort.DisposeCallCount);
        Assert.Equal(1, newPort.CloseCallCount);
        Assert.Equal(1, newPort.DisposeCallCount);
    }

    [Fact]
    public async Task StuckReceiveSessions_ReachInstanceCap_RejectReconnectUntilWorkersExit()
    {
        var stuckPorts = Enumerable.Range(0, SerialService.MaximumOutstandingReceiveSessionCount)
            .Select(index => new GatedSerialPort(
                Encoding.ASCII.GetBytes($"old-{index}"),
                ignoreReadCancellation: true))
            .ToArray();
        var recoveryPort = new GatedSerialPort(Encoding.ASCII.GetBytes("recovered\r\n"));
        recoveryPort.ReleaseRead();
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
                await service.ConnectAsync(Settings(), new SerialReceiveOptions(), CancellationToken.None);
                await port.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
                await service.DisconnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            }

            Assert.Equal(SerialService.MaximumOutstandingReceiveSessionCount, service.OutstandingReceiveSessionCount);
            var callsBeforeRejectedConnect = factoryCalls;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ConnectAsync(Settings(), new SerialReceiveOptions(), CancellationToken.None));
            Assert.Contains("receive cleanup capacity", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(callsBeforeRejectedConnect, factoryCalls);
        }
        finally
        {
            foreach (var port in stuckPorts)
            {
                port.ReleaseRead();
            }
        }

        await WaitUntilAsync(() => service.OutstandingReceiveSessionCount == 0, TimeSpan.FromSeconds(2));
        await service.ConnectAsync(Settings(), new SerialReceiveOptions(), CancellationToken.None);
        var chunk = await service.ReceivedBytes.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("recovered\r\n", Encoding.ASCII.GetString(chunk.Bytes));
        await service.DisconnectAsync(CancellationToken.None);
    }

    [Fact]
    public async Task MockStress_GeneratedButPublishInflight_IsDrainedBeforeNormalCompletion()
    {
        var publishEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = CreateService(
            (_, _) => throw new InvalidOperationException("MOCK must not create a native port."),
            stopTimeout: TimeSpan.FromSeconds(1),
            beforePublishAsync: async (chunk, forceAbortToken) =>
            {
                if (chunk.Bytes.Length > 0 && chunk.Bytes[0] is >= (byte)'0' and <= (byte)'9')
                {
                    publishEntered.TrySetResult();
                    await releasePublish.Task.WaitAsync(forceAbortToken);
                }
            });

        await service.ConnectAsync(
            new SerialSettings { PortName = "MOCK", BaudRate = 921600 },
            new SerialReceiveOptions(),
            CancellationToken.None);
        var sessionReader = service.ReceivedBytes;
        service.ConfigureMockStress(
            linesPerSecond: 10_000,
            burstSize: 1_000,
            injectEvents: false,
            injectInvalidBytes: false,
            MockGeneratorPattern.NormalLines);
        service.StartMockStress();
        await publishEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1_000, service.MockGeneratedButNotAcceptedLineCount);
        Assert.Equal(1_000, service.MockLastGeneratedSequence);
        Assert.Equal(0, service.MockLastAcceptedSequence);

        var disconnect = service.DisconnectAsync(CancellationToken.None);
        releasePublish.TrySetResult();
        await disconnect.WaitAsync(TimeSpan.FromSeconds(1));

        using var received = new MemoryStream();
        await foreach (var chunk in sessionReader.ReadAllAsync())
        {
            received.Write(chunk.Bytes);
        }

        var text = Encoding.UTF8.GetString(received.ToArray());
        Assert.Contains("001000 INFO mock serial sample", text, StringComparison.Ordinal);
        Assert.Equal(service.MockLastGeneratedSequence, service.MockLastAcceptedSequence);
        Assert.Equal(0, service.MockGeneratedButNotAcceptedLineCount);
        Assert.Equal("graceful drain", service.LastReceiveStopMode);
    }

    [Fact]
    public async Task OldError_PausedAfterCurrentCheck_CannotFaultReplacementSession()
    {
        using var releaseErrorCommit = new ManualResetEventSlim(initialState: false);
        var errorCheckEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldPort = new LateFailingSerialPort();
        var newPort = new CancellableWaitingSerialPort();
        var ports = new ConcurrentQueue<ISerialPortConnection>([oldPort, newPort]);
        await using var service = CreateService(
            (_, _) => ports.TryDequeue(out var port)
                ? port
                : throw new InvalidOperationException("No serial port configured."),
            afterSessionErrorCurrentCheck: _ =>
            {
                errorCheckEntered.TrySetResult();
                releaseErrorCommit.Wait();
            });

        try
        {
            await service.ConnectAsync(Settings(), new SerialReceiveOptions(), CancellationToken.None);
            oldPort.ReleaseFailure();
            await errorCheckEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

            await service.DisconnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            await service.ConnectAsync(Settings(), new SerialReceiveOptions(), CancellationToken.None);
            Assert.Equal(SerialConnectionState.Connected, service.ConnectionState);
            Assert.Null(service.LastError);

            releaseErrorCommit.Set();
            await WaitUntilAsync(() => service.OutstandingReceiveSessionCount == 0, TimeSpan.FromSeconds(1));
            Assert.Equal(SerialConnectionState.Connected, service.ConnectionState);
            Assert.Null(service.LastError);
        }
        finally
        {
            releaseErrorCommit.Set();
        }

        await service.DisconnectAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OldRawPublish_AfterForceCheck_CannotEnterNewGenerationBoundBridge()
    {
        var rawPublishEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRawPublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldPort = new GatedSerialPort(Encoding.ASCII.GetBytes("OLD"), ignoreReadCancellation: true);
        var newPort = new GatedSerialPort(Encoding.ASCII.GetBytes("NEW"), ignoreReadCancellation: true);
        var ports = new ConcurrentQueue<GatedSerialPort>([oldPort, newPort]);
        await using var serial = CreateService(
            (_, _) => ports.TryDequeue(out var port)
                ? port
                : throw new InvalidOperationException("No serial port configured."),
            beforeRawPublishAsync: async chunk =>
            {
                if (chunk.Bytes.AsSpan().SequenceEqual("OLD"u8))
                {
                    rawPublishEntered.TrySetResult();
                    await releaseRawPublish.Task;
                }
            });
        var bridgePort = new CapturingBridgePort();
        await using var bridge = new SerialBridgeService(
            new SystemBridgeClock(),
            (_, _) => bridgePort,
            ShortTimeout,
            ShortTimeout,
            maximumOutstandingOperations: 8);
        var enqueueResults = new ConcurrentQueue<(long Generation, bool Accepted)>();
        serial.RawBytesReceived += chunk => enqueueResults.Enqueue(
            (chunk.SourceSerialSessionGeneration, bridge.TryEnqueueDeviceChunk(chunk)));

        try
        {
            await serial.ConnectAsync(Settings(), new SerialReceiveOptions(), CancellationToken.None);
            var oldGeneration = serial.ReceiveSessionGeneration;
            await StartBridgeAsync(bridge, oldGeneration);
            serial.SetRawBridgePriorityEnabled(true);
            oldPort.ReleaseRead();
            await rawPublishEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

            await serial.DisconnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            await bridge.StopAsync(CancellationToken.None);
            await serial.ConnectAsync(Settings(), new SerialReceiveOptions(), CancellationToken.None);
            var newGeneration = serial.ReceiveSessionGeneration;
            Assert.NotEqual(oldGeneration, newGeneration);
            await StartBridgeAsync(bridge, newGeneration);
            serial.SetRawBridgePriorityEnabled(true);

            releaseRawPublish.TrySetResult();
            await WaitUntilAsync(() => enqueueResults.Any(result => result.Generation == oldGeneration), TimeSpan.FromSeconds(1));
            Assert.Contains(enqueueResults, result =>
                result.Generation == oldGeneration && !result.Accepted);
            Assert.Empty(bridgePort.Writes);

            newPort.ReleaseRead();
            await WaitUntilAsync(() => bridgePort.Writes.Count == 1, TimeSpan.FromSeconds(1));
            Assert.Contains(enqueueResults, result =>
                result.Generation == newGeneration && result.Accepted);
            Assert.Equal("NEW", Encoding.ASCII.GetString(bridgePort.Writes.Single()));
            Assert.True(bridge.IsRunning);
            Assert.True(serial.IsConnected);
        }
        finally
        {
            releaseRawPublish.TrySetResult();
            serial.SetRawBridgePriorityEnabled(false);
            await bridge.StopAsync(CancellationToken.None);
            await serial.DisconnectAsync(CancellationToken.None);
        }
    }

    private static SerialService CreateService(
        Func<SerialSettings, SerialReceiveOptions, ISerialPortConnection> factory,
        TimeSpan? stopTimeout = null,
        Func<ReceivedByteChunk, CancellationToken, ValueTask>? beforePublishAsync = null,
        Action<long>? afterSessionErrorCurrentCheck = null,
        Func<BridgeRxChunk, ValueTask>? beforeRawPublishAsync = null,
        Func<long, CancellationToken, ValueTask>? beforeConnectedCommitAsync = null,
        Action<long>? receiveWorkerCommitWaitEntered = null) => new(
            factory,
            ShortTimeout,
            stopTimeout ?? ShortTimeout,
            maximumOutstandingOperations: 8,
            beforePublishAsync: beforePublishAsync,
            afterSessionErrorCurrentCheck: afterSessionErrorCurrentCheck,
            beforeRawPublishAsync: beforeRawPublishAsync,
            beforeConnectedCommitAsync: beforeConnectedCommitAsync,
            receiveWorkerCommitWaitEntered: receiveWorkerCommitWaitEntered);

    private static Task StartBridgeAsync(SerialBridgeService bridge, long sourceGeneration) =>
        bridge.StartAsync(
            new BridgeSettings
            {
                Enabled = true,
                VirtualPortName = "COM_RAW_GENERATION",
                MaxQueuedChunks = 32,
                MaxQueuedBytes = 64 * 1024
            },
            Settings(),
            (_, _) => Task.CompletedTask,
            CancellationToken.None,
            sourceGeneration);

    private static SerialSettings Settings() => new()
    {
        PortName = "COM_SESSION_TEST",
        BaudRate = 115200
    };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException("The serial receive session did not settle.");
            }

            await Task.Delay(5);
        }
    }

    private sealed class GatedSerialPort : ISerialPortConnection
    {
        private readonly byte[] _bytes;
        private readonly bool _ignoreReadCancellation;
        private readonly TaskCompletionSource _releaseRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;
        private int _closeCallCount;
        private int _disposeCallCount;

        public GatedSerialPort(byte[] bytes, bool ignoreReadCancellation = false)
        {
            _bytes = bytes;
            _ignoreReadCancellation = ignoreReadCancellation;
            if (!ignoreReadCancellation)
            {
                _releaseRead.TrySetResult();
            }
        }

        public event EventHandler<SerialErrorReceivedEventArgs>? ErrorReceived
        {
            add { }
            remove { }
        }

        public TaskCompletionSource ReadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CloseCallCount => Volatile.Read(ref _closeCallCount);

        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        public void Open() { }

        public void Close() => Interlocked.Increment(ref _closeCallCount);

        public void Dispose() => Interlocked.Increment(ref _disposeCallCount);

        public NativeReadCompletion ReadNativeCompletion(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                ReadEntered.TrySetResult();
                if (_ignoreReadCancellation)
                {
                    _releaseRead.Task.GetAwaiter().GetResult();
                }
                else
                {
                    _releaseRead.Task.Wait(cancellationToken);
                }

                return new NativeReadCompletion(
                    _bytes.ToArray(),
                    Stopwatch.GetTimestamp(),
                    EndsAtNativeIdleBoundary: false,
                    BoundarySuppressedByLineError: false);
            }

            cancellationToken.WaitHandle.WaitOne();
            throw new OperationCanceledException(cancellationToken);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void ReleaseRead() => _releaseRead.TrySetResult();
    }

    private sealed class ImmediateFailingSerialPort : ISerialPortConnection
    {
        private int _readCallCount;

        public event EventHandler<SerialErrorReceivedEventArgs>? ErrorReceived
        {
            add { }
            remove { }
        }

        public TaskCompletionSource ReadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReadCallCount => Volatile.Read(ref _readCallCount);

        public void Open() { }

        public void Close() { }

        public void Dispose() { }

        public NativeReadCompletion ReadNativeCompletion(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCallCount);
            ReadEntered.TrySetResult();
            throw new IOException("controlled immediate receive failure");
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class LateFailingSerialPort : ISerialPortConnection
    {
        private readonly TaskCompletionSource _releaseFailure =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<SerialErrorReceivedEventArgs>? ErrorReceived
        {
            add { }
            remove { }
        }

        public void Open() { }

        public void Close() { }

        public void Dispose() { }

        public NativeReadCompletion ReadNativeCompletion(CancellationToken cancellationToken)
        {
            _releaseFailure.Task.GetAwaiter().GetResult();
            throw new IOException("controlled late receive failure");
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void ReleaseFailure() => _releaseFailure.TrySetResult();
    }

    private sealed class CancellableWaitingSerialPort : ISerialPortConnection
    {
        public event EventHandler<SerialErrorReceivedEventArgs>? ErrorReceived
        {
            add { }
            remove { }
        }

        public void Open() { }

        public void Close() { }

        public void Dispose() { }

        public NativeReadCompletion ReadNativeCompletion(CancellationToken cancellationToken)
        {
            cancellationToken.WaitHandle.WaitOne();
            throw new OperationCanceledException(cancellationToken);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class CapturingBridgePort : IBridgePortConnection
    {
        public ConcurrentQueue<byte[]> Writes { get; } = new();

        public void Open() { }

        public void Close() { }

        public void Dispose() { }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            Writes.Enqueue(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
    }
}
