using RJCP.IO.Ports;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed class SerialBridgeService : ISerialBridgeService
{
    private const int DeviceToVirtualQueueCapacity = 2_048;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateGate = new();
    private LosslessByteChannel _deviceToVirtualQueue = CreateQueue();
    private LosslessByteChannel _virtualToDeviceQueue = CreateQueue();
    private CancellationTokenSource? _cancellation;
    private SerialPortStream? _virtualPort;
    private Task? _readerTask;
    private Task? _writerTask;
    private Task? _deviceWriterTask;
    private string _virtualPortName = string.Empty;
    private string? _lastError;
    private long _deviceToVirtualByteCount;
    private long _deviceToVirtualChunkCount;
    private long _virtualToDeviceByteCount;
    private long _virtualToDeviceChunkCount;
    private long _droppedDeviceToVirtualByteCount;
    private long _droppedDeviceToVirtualChunkCount;
    private long _droppedVirtualToDeviceByteCount;
    private long _droppedVirtualToDeviceChunkCount;
    private long _errorCount;
    private bool _isRunning;
    private bool _disposed;

    public event EventHandler<string>? Error;

    public event EventHandler? StatusChanged;

    public bool IsRunning
    {
        get
        {
            lock (_stateGate)
            {
                return _isRunning;
            }
        }
    }

    public string VirtualPortName
    {
        get
        {
            lock (_stateGate)
            {
                return _virtualPortName;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (_stateGate)
            {
                return _lastError;
            }
        }
    }

    public long DeviceToVirtualByteCount => Interlocked.Read(ref _deviceToVirtualByteCount);

    public long DeviceToVirtualChunkCount => Interlocked.Read(ref _deviceToVirtualChunkCount);

    public long VirtualToDeviceByteCount => Interlocked.Read(ref _virtualToDeviceByteCount);

    public long VirtualToDeviceChunkCount => Interlocked.Read(ref _virtualToDeviceChunkCount);

    public long DroppedDeviceToVirtualByteCount => Interlocked.Read(ref _droppedDeviceToVirtualByteCount);

    public long DroppedDeviceToVirtualChunkCount => Interlocked.Read(ref _droppedDeviceToVirtualChunkCount);

    public long DroppedVirtualToDeviceByteCount => Interlocked.Read(ref _droppedVirtualToDeviceByteCount);

    public long DroppedVirtualToDeviceChunkCount => Interlocked.Read(ref _droppedVirtualToDeviceChunkCount);

    public long ErrorCount => Interlocked.Read(ref _errorCount);

    public int PendingDeviceToVirtualChunkCount => IsRunning ? _deviceToVirtualQueue.Count : 0;

    public int PendingVirtualToDeviceChunkCount => IsRunning ? _virtualToDeviceQueue.Count : 0;

    public async Task StartAsync(
        BridgeSettings settings,
        SerialSettings deviceSettings,
        Func<byte[], CancellationToken, Task> writeToDeviceAsync,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(deviceSettings);
        ArgumentNullException.ThrowIfNull(writeToDeviceAsync);

        var virtualPortName = settings.VirtualPortName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(virtualPortName))
        {
            throw new InvalidOperationException("Select a virtual COM port for the bridge.");
        }

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await StopCurrentAsync(CancellationToken.None);

            var virtualPort = new SerialPortStream(
                virtualPortName,
                deviceSettings.BaudRate,
                8,
                Parity.None,
                StopBits.One)
            {
                // The bridge side is a byte-transparent virtual COM endpoint.
                // Do not apply the physical port's flow control here: a
                // virtual pair may not assert CTS/DSR and could otherwise
                // stall an otherwise valid raw-byte bridge.
                Handshake = Handshake.None,
                ReadBufferSize = 1024 * 1024,
                WriteBufferSize = 1024 * 1024,
                // ReadAsync is canceled by the bridge token. Infinite avoids
                // waking every 500 ms on an idle virtual COM port.
                ReadTimeout = Timeout.Infinite,
                WriteTimeout = 1000,
                DtrEnable = false,
                RtsEnable = false
            };

            try
            {
                await Task.Run(virtualPort.Open, cancellationToken);
            }
            catch
            {
                SafeCloseAndDispose(virtualPort);
                throw;
            }

            _deviceToVirtualQueue = CreateQueue();
            _virtualToDeviceQueue = CreateQueue();
            var bridgeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_stateGate)
            {
                _cancellation = bridgeCancellation;
                _virtualPort = virtualPort;
                _virtualPortName = virtualPortName;
                _lastError = null;
                _isRunning = true;
            }

            _readerTask = Task.Run(
                () => RunVirtualReaderAsync(virtualPort, bridgeCancellation.Token),
                CancellationToken.None);
            _writerTask = Task.Run(
                () => RunVirtualWriterAsync(virtualPort, bridgeCancellation.Token),
                CancellationToken.None);
            _deviceWriterTask = Task.Run(
                () => RunDeviceWriterAsync(writeToDeviceAsync, bridgeCancellation.Token),
                CancellationToken.None);
            RaiseStatusChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportError($"Bridge start failed for {virtualPortName}: {ex.Message}");
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed && _readerTask is null && _writerTask is null && _deviceWriterTask is null)
        {
            return;
        }

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCurrentAsync(cancellationToken);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask EnqueueDeviceBytesAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
        {
            return;
        }

        LosslessByteChannel queue;
        CancellationToken bridgeToken;
        lock (_stateGate)
        {
            if (!_isRunning || _cancellation is null)
            {
                return;
            }

            queue = _deviceToVirtualQueue;
            bridgeToken = _cancellation.Token;
        }

        try
        {
            // The raw bridge is the priority consumer. Apply bounded
            // backpressure instead of silently dropping a chunk when the
            // virtual-port writer is briefly behind.
            await queue.WriteAsync(bytes, cancellationToken, bridgeToken);
        }
        catch (OperationCanceledException) when (bridgeToken.IsCancellationRequested)
        {
            // Intentional bridge shutdown is not a transport failure or drop.
        }
        catch (System.Threading.Channels.ChannelClosedException) when (
            bridgeToken.IsCancellationRequested || !IsRunning)
        {
            // The queue can close between the running-state snapshot and the
            // asynchronous write. Shutdown owns this race.
        }
        catch
        {
            Interlocked.Increment(ref _droppedDeviceToVirtualChunkCount);
            Interlocked.Add(ref _droppedDeviceToVirtualByteCount, bytes.Length);
            RaiseStatusChanged();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync(CancellationToken.None);
        _lifecycleGate.Dispose();
    }

    private async Task RunVirtualReaderAsync(
        SerialPortStream virtualPort,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await virtualPort.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead <= 0)
                {
                    continue;
                }

                var chunk = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);
                try
                {
                    await _virtualToDeviceQueue.WriteAsync(chunk, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (System.Threading.Channels.ChannelClosedException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    Interlocked.Increment(ref _droppedVirtualToDeviceChunkCount);
                    Interlocked.Add(ref _droppedVirtualToDeviceByteCount, bytesRead);
                    RaiseStatusChanged();
                    throw;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                ReportBackgroundFailure($"Bridge virtual-to-device failed: {ex.Message}");
            }
        }
    }

    private async Task RunDeviceWriterAsync(
        Func<byte[], CancellationToken, Task> writeToDeviceAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var bytes in _virtualToDeviceQueue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await writeToDeviceAsync(bytes, cancellationToken);
                    Interlocked.Add(ref _virtualToDeviceByteCount, bytes.Length);
                    Interlocked.Increment(ref _virtualToDeviceChunkCount);
                    RaiseStatusChangedPeriodically(VirtualToDeviceChunkCount);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    Interlocked.Increment(ref _droppedVirtualToDeviceChunkCount);
                    Interlocked.Add(ref _droppedVirtualToDeviceByteCount, bytes.Length);
                    throw;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                ReportBackgroundFailure($"Bridge virtual-to-device write failed: {ex.Message}");
            }
        }
    }

    private async Task RunVirtualWriterAsync(SerialPortStream virtualPort, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var bytes in _deviceToVirtualQueue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await virtualPort.WriteAsync(bytes, cancellationToken);
                    Interlocked.Add(ref _deviceToVirtualByteCount, bytes.Length);
                    Interlocked.Increment(ref _deviceToVirtualChunkCount);
                    RaiseStatusChangedPeriodically(DeviceToVirtualChunkCount);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    Interlocked.Increment(ref _droppedDeviceToVirtualChunkCount);
                    Interlocked.Add(ref _droppedDeviceToVirtualByteCount, bytes.Length);
                    throw;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                ReportBackgroundFailure($"Bridge device-to-virtual failed: {ex.Message}");
            }
        }
    }

    private void ReportBackgroundFailure(string message)
    {
        ReportError(message);
        try
        {
            _cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        SafeCloseAndDispose(_virtualPort);
        lock (_stateGate)
        {
            _isRunning = false;
        }

        RaiseStatusChanged();
    }

    private async Task StopCurrentAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? cancellation;
        SerialPortStream? virtualPort;
        Task? readerTask;
        Task? writerTask;
        Task? deviceWriterTask;
        lock (_stateGate)
        {
            cancellation = _cancellation;
            virtualPort = _virtualPort;
            readerTask = _readerTask;
            writerTask = _writerTask;
            deviceWriterTask = _deviceWriterTask;
            _cancellation = null;
            _virtualPort = null;
            _readerTask = null;
            _writerTask = null;
            _deviceWriterTask = null;
            _isRunning = false;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _deviceToVirtualQueue.TryComplete();
        _virtualToDeviceQueue.TryComplete();
        SafeCloseAndDispose(virtualPort);

        foreach (var task in new[] { readerTask, writerTask, deviceWriterTask })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        cancellation?.Dispose();
        RaiseStatusChanged();
    }

    private void ReportError(string message)
    {
        Interlocked.Increment(ref _errorCount);
        lock (_stateGate)
        {
            _lastError = message;
        }

        Error?.Invoke(this, message);
        RaiseStatusChanged();
    }

    private void RaiseStatusChangedPeriodically(long chunkCount)
    {
        if (chunkCount == 1 || chunkCount % 16 == 0)
        {
            RaiseStatusChanged();
        }
    }

    private void RaiseStatusChanged()
    {
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static LosslessByteChannel CreateQueue() => new(DeviceToVirtualQueueCapacity);

    private static void SafeCloseAndDispose(SerialPortStream? serialPort)
    {
        if (serialPort is null)
        {
            return;
        }

        try
        {
            serialPort.Close();
        }
        catch
        {
        }

        try
        {
            serialPort.Dispose();
        }
        catch
        {
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
