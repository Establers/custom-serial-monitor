using System.Threading.Channels;
using RJCP.IO.Ports;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed class SerialBridgeService : ISerialBridgeService
{
    private const int DeviceToVirtualQueueCapacity = 2_048;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateGate = new();
    private Channel<byte[]> _deviceToVirtualQueue = CreateQueue();
    private Channel<byte[]> _virtualToDeviceQueue = CreateQueue();
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
    private int _pendingDeviceToVirtualChunkCount;
    private int _pendingVirtualToDeviceChunkCount;
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

    public int PendingDeviceToVirtualChunkCount => Volatile.Read(ref _pendingDeviceToVirtualChunkCount);

    public int PendingVirtualToDeviceChunkCount => Volatile.Read(ref _pendingVirtualToDeviceChunkCount);

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
                Handshake = Handshake.None,
                ReadBufferSize = 1024 * 1024,
                WriteBufferSize = 1024 * 1024,
                ReadTimeout = 500,
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
            Volatile.Write(ref _pendingDeviceToVirtualChunkCount, 0);
            Volatile.Write(ref _pendingVirtualToDeviceChunkCount, 0);
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _virtualPort = virtualPort;
            lock (_stateGate)
            {
                _virtualPortName = virtualPortName;
                _lastError = null;
                _isRunning = true;
            }

            _readerTask = Task.Run(
                () => RunVirtualReaderAsync(virtualPort, _cancellation.Token),
                CancellationToken.None);
            _writerTask = Task.Run(
                () => RunVirtualWriterAsync(virtualPort, _cancellation.Token),
                CancellationToken.None);
            _deviceWriterTask = Task.Run(
                () => RunDeviceWriterAsync(writeToDeviceAsync, _cancellation.Token),
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

    public bool TryEnqueueDeviceBytes(byte[] bytes)
    {
        if (!IsRunning || bytes is null || bytes.Length == 0)
        {
            return false;
        }

        Interlocked.Increment(ref _pendingDeviceToVirtualChunkCount);
        if (_deviceToVirtualQueue.Writer.TryWrite(bytes))
        {
            return true;
        }

        Interlocked.Decrement(ref _pendingDeviceToVirtualChunkCount);
        Interlocked.Increment(ref _droppedDeviceToVirtualChunkCount);
        Interlocked.Add(ref _droppedDeviceToVirtualByteCount, bytes.Length);
        RaiseStatusChanged();
        return false;
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
                Interlocked.Increment(ref _pendingVirtualToDeviceChunkCount);
                if (!_virtualToDeviceQueue.Writer.TryWrite(chunk))
                {
                    Interlocked.Decrement(ref _pendingVirtualToDeviceChunkCount);
                    Interlocked.Increment(ref _droppedVirtualToDeviceChunkCount);
                    Interlocked.Add(ref _droppedVirtualToDeviceByteCount, bytesRead);
                    RaiseStatusChanged();
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
                Interlocked.Decrement(ref _pendingVirtualToDeviceChunkCount);
                await writeToDeviceAsync(bytes, cancellationToken);
                Interlocked.Add(ref _virtualToDeviceByteCount, bytes.Length);
                Interlocked.Increment(ref _virtualToDeviceChunkCount);
                RaiseStatusChangedPeriodically(VirtualToDeviceChunkCount);
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
                Interlocked.Decrement(ref _pendingDeviceToVirtualChunkCount);
                await virtualPort.WriteAsync(bytes, cancellationToken);
                Interlocked.Add(ref _deviceToVirtualByteCount, bytes.Length);
                Interlocked.Increment(ref _deviceToVirtualChunkCount);
                RaiseStatusChangedPeriodically(DeviceToVirtualChunkCount);
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
        var cancellation = _cancellation;
        var virtualPort = _virtualPort;
        var readerTask = _readerTask;
        var writerTask = _writerTask;
        var deviceWriterTask = _deviceWriterTask;

        _cancellation = null;
        _virtualPort = null;
        _readerTask = null;
        _writerTask = null;
        _deviceWriterTask = null;
        lock (_stateGate)
        {
            _isRunning = false;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _deviceToVirtualQueue.Writer.TryComplete();
        _virtualToDeviceQueue.Writer.TryComplete();
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
        Volatile.Write(ref _pendingDeviceToVirtualChunkCount, 0);
        Volatile.Write(ref _pendingVirtualToDeviceChunkCount, 0);
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

    private static Channel<byte[]> CreateQueue()
    {
        return Channel.CreateBounded<byte[]>(new BoundedChannelOptions(DeviceToVirtualQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

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
