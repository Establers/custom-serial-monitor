using System.Text;
using System.Threading.Channels;
using RJCP.IO.Ports;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed class SerialService : ISerialService
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _stateGate = new();
    private Channel<ReceivedByteChunk> _receivedBytes = CreateChannel();
    private CancellationTokenSource? _receiveCancellation;
    private SerialPortStream? _serialPort;
    private Task? _receiveTask;
    private SerialConnectionState _connectionState = SerialConnectionState.Disconnected;
    private string? _lastError;
    private long _receivedByteCount;
    private long _receivedChunkCount;
    private long _writtenByteCount;
    private long _connectionErrorCount;
    private int _rawBridgePriorityEnabled;
    private long _bridgePriorityDroppedPipelineByteCount;
    private long _bridgePriorityDroppedPipelineChunkCount;
    private long _mockGeneratedLineCount;
    private long _mockLastGeneratedSequence;
    private long _mockNoNewlineEmittedBytes;
    private int _mockStressLinesPerSecond = 10;
    private int _mockStressBurstSize = 1;
    private int _mockGeneratorPattern = (int)MockGeneratorPattern.NormalLines;
    private bool _mockStressInjectEvents = true;
    private bool _mockStressInjectInvalidBytes;
    private bool _mockStressRunning;
    private bool _isMockConnection;
    private bool _disposed;

    public event EventHandler<string>? Error;

    public event EventHandler? StatusChanged;

    public event Action<byte[]>? RawBytesReceived;

    public bool IsConnected => ConnectionState == SerialConnectionState.Connected;

    public SerialConnectionState ConnectionState
    {
        get
        {
            lock (_stateGate)
            {
                return _connectionState;
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

    public long ReceivedByteCount => Interlocked.Read(ref _receivedByteCount);

    public long ReceivedChunkCount => Interlocked.Read(ref _receivedChunkCount);

    public long WrittenByteCount => Interlocked.Read(ref _writtenByteCount);

    public long ConnectionErrorCount => Interlocked.Read(ref _connectionErrorCount);

    public bool IsRawBridgePriorityEnabled => Volatile.Read(ref _rawBridgePriorityEnabled) != 0;

    public long BridgePriorityDroppedPipelineByteCount => Interlocked.Read(ref _bridgePriorityDroppedPipelineByteCount);

    public long BridgePriorityDroppedPipelineChunkCount => Interlocked.Read(ref _bridgePriorityDroppedPipelineChunkCount);

    public ChannelReader<ReceivedByteChunk> ReceivedBytes => _receivedBytes.Reader;

    public bool IsMockStressRunning => Volatile.Read(ref _mockStressRunning);

    public int MockStressLinesPerSecond => Volatile.Read(ref _mockStressLinesPerSecond);

    public int MockStressBurstSize => Volatile.Read(ref _mockStressBurstSize);

    public bool MockStressInjectEvents => Volatile.Read(ref _mockStressInjectEvents);

    public bool MockStressInjectInvalidBytes => Volatile.Read(ref _mockStressInjectInvalidBytes);

    public long MockGeneratedLineCount => Interlocked.Read(ref _mockGeneratedLineCount);

    public long MockLastGeneratedSequence => Interlocked.Read(ref _mockLastGeneratedSequence);

    public MockGeneratorPattern MockGeneratorPattern => NormalizeMockGeneratorPattern(
        (MockGeneratorPattern)Volatile.Read(ref _mockGeneratorPattern));

    public bool IsMockNoNewlineActive => IsMockStressRunning &&
        MockGeneratorPattern is MockGeneratorPattern.NoNewlineZzz or MockGeneratorPattern.NoNewlineZzzBurst;

    public long MockNoNewlineEmittedBytes => Interlocked.Read(ref _mockNoNewlineEmittedBytes);

    public string MockStressStatus => IsMockStressRunning
        ? MockGeneratorPattern switch
        {
            MockGeneratorPattern.NoNewlineZzz => "Stress running: No-newline zzz slow",
            MockGeneratorPattern.NoNewlineZzzBurst => "Stress running: No-newline zzz burst",
            _ => $"Stress running: {MockStressLinesPerSecond:N0} lps, burst {MockStressBurstSize:N0}"
        }
        : "Stress stopped";

    public async Task<IReadOnlyList<string>> GetAvailablePortsAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        try
        {
            var ports = await Task.Run(() =>
            {
                using var stream = new SerialPortStream();
                return stream.GetPortNames()
                    .OrderBy(port => port, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }, cancellationToken);

            return CreatePortListWithMock(ports);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportError($"Port scan failed: {ex.Message}", countConnectionError: false);
            return new[] { "MOCK" };
        }
    }

    private static IReadOnlyList<string> CreatePortListWithMock(IEnumerable<string> ports)
    {
        var result = new List<string> { "MOCK" };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MOCK" };

        foreach (var port in ports)
        {
            if (string.IsNullOrWhiteSpace(port))
            {
                continue;
            }

            var trimmed = port.Trim();
            if (seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    public async Task ConnectAsync(SerialSettings settings, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();

            if (ConnectionState is SerialConnectionState.Connected or SerialConnectionState.Connecting)
            {
                return;
            }

            await StopCurrentConnectionAsync(CancellationToken.None, publishDisconnected: false);

            Volatile.Write(ref _rawBridgePriorityEnabled, 0);
            Interlocked.Exchange(ref _bridgePriorityDroppedPipelineByteCount, 0);
            Interlocked.Exchange(ref _bridgePriorityDroppedPipelineChunkCount, 0);

            _receivedBytes = CreateChannel();
            _receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            SetConnectionState(SerialConnectionState.Connecting, clearLastError: true);

            if (IsMockPort(settings.PortName))
            {
                _isMockConnection = true;
                _receiveTask = Task.Run(() => RunMockReceiverAsync(settings.Clone(), _receiveCancellation.Token), CancellationToken.None);
                SetConnectionState(SerialConnectionState.Connected, clearLastError: true);
                return;
            }

            _isMockConnection = false;
            var serialPort = CreateSerialPort(settings);

            try
            {
                await Task.Run(serialPort.Open, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                SafeDispose(serialPort);
                throw;
            }
            catch (Exception ex)
            {
                SafeDispose(serialPort);
                _receiveCancellation.Dispose();
                _receiveCancellation = null;
                _receivedBytes.Writer.TryComplete();

                var message = $"Failed to open {settings.PortName}: {ex.Message}";
                ReportError(message, countConnectionError: true, state: SerialConnectionState.Faulted);
                throw new InvalidOperationException(message, ex);
            }

            _serialPort = serialPort;
            _receiveTask = Task.Run(() => RunSerialReceiverAsync(serialPort, _receiveCancellation.Token), CancellationToken.None);
            SetConnectionState(SerialConnectionState.Connected, clearLastError: true);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCurrentConnectionAsync(cancellationToken, publishDisconnected: true);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task SendAsync(TxCommand command, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(command);

        var payload = Encoding.UTF8.GetBytes(command.CommandText + ToLineEnding(command.LineEndingMode ?? TxLineEndingMode.None));
        await SendPayloadAsync(payload, $"mock device received command: {command.CommandText}{Environment.NewLine}", cancellationToken);
    }

    public async Task SendBytesAsync(byte[] payload, string mockEchoText, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(payload);

        await SendPayloadAsync(payload, $"mock device received bytes: {mockEchoText}{Environment.NewLine}", cancellationToken);
    }

    private async Task SendPayloadAsync(byte[] payload, string mockResponse, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsConnected)
            {
                const string message = "Write failed: serial service is disconnected.";
                ReportError(message, countConnectionError: false);
                throw new InvalidOperationException(message);
            }

            var serialPort = _serialPort;
            if (serialPort is null)
            {
                var responseBytes = Encoding.UTF8.GetBytes(mockResponse);
                await PublishReceivedBytesAsync(responseBytes, cancellationToken, countReceived: false);
                AddWrittenBytes(payload.Length);
                return;
            }

            try
            {
                await serialPort.WriteAsync(payload, cancellationToken);
                AddWrittenBytes(payload.Length);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var message = $"Serial write failed: {ex.Message}";
                ReportError(message, countConnectionError: true, state: SerialConnectionState.Faulted);
                _receiveCancellation?.Cancel();
                SafeCloseAndDispose(serialPort);
                ClearCurrentPortReference(serialPort);
                _receivedBytes.Writer.TryComplete();
                throw new InvalidOperationException(message, ex);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifecycleGate.WaitAsync(CancellationToken.None);
        try
        {
            await StopCurrentConnectionAsync(CancellationToken.None, publishDisconnected: true);
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
            _writeGate.Dispose();
        }
    }

    public void ConfigureMockStress(
        int linesPerSecond,
        int burstSize,
        bool injectEvents,
        bool injectInvalidBytes,
        MockGeneratorPattern pattern)
    {
        Volatile.Write(ref _mockStressLinesPerSecond, Math.Clamp(linesPerSecond, 1, 10_000));
        Volatile.Write(ref _mockStressBurstSize, Math.Clamp(burstSize, 1, 1_000));
        Volatile.Write(ref _mockStressInjectEvents, injectEvents);
        Volatile.Write(ref _mockStressInjectInvalidBytes, injectInvalidBytes);
        Volatile.Write(ref _mockGeneratorPattern, (int)NormalizeMockGeneratorPattern(pattern));
        RaiseStatusChanged();
    }

    public void StartMockStress()
    {
        if (!_isMockConnection || !IsConnected)
        {
            ReportError("Mock stress start ignored: connect to MOCK first.", countConnectionError: false);
            return;
        }

        Volatile.Write(ref _mockStressRunning, true);
        RaiseStatusChanged();
    }

    public void StopMockStress()
    {
        Volatile.Write(ref _mockStressRunning, false);
        RaiseStatusChanged();
    }

    public void ResetMockStressCounters()
    {
        Interlocked.Exchange(ref _mockGeneratedLineCount, 0);
        Interlocked.Exchange(ref _mockLastGeneratedSequence, 0);
        Interlocked.Exchange(ref _mockNoNewlineEmittedBytes, 0);
        RaiseStatusChanged();
    }

    public async Task SendMockCrlfAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (!_isMockConnection || !IsConnected)
        {
            ReportError("Mock CRLF ignored: connect to MOCK first.", countConnectionError: false);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes("\r\n");
        await PublishReceivedBytesAsync(bytes, cancellationToken, countReceived: true);
    }

    private async Task StopCurrentConnectionAsync(CancellationToken cancellationToken, bool publishDisconnected)
    {
        var hasResources = _receiveCancellation is not null || _receiveTask is not null || _serialPort is not null;
        if (!hasResources)
        {
            if (publishDisconnected && ConnectionState != SerialConnectionState.Disconnected)
            {
                SetConnectionState(SerialConnectionState.Disconnected);
            }

            return;
        }

        SetConnectionState(SerialConnectionState.Disconnecting);

        var receiveCancellation = _receiveCancellation;
        var receiveTask = _receiveTask;
        var serialPort = _serialPort;

        _receiveCancellation = null;
        _receiveTask = null;
        _serialPort = null;
        _isMockConnection = false;
        Volatile.Write(ref _rawBridgePriorityEnabled, 0);
        StopMockStress();

        try
        {
            receiveCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        SafeCloseAndDispose(serialPort);
        _receivedBytes.Writer.TryComplete();

        if (receiveTask is not null)
        {
            try
            {
                await receiveTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        receiveCancellation?.Dispose();

        if (publishDisconnected)
        {
            SetConnectionState(SerialConnectionState.Disconnected);
        }
    }

    private async Task RunSerialReceiverAsync(SerialPortStream serialPort, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await serialPort.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead <= 0)
                {
                    continue;
                }

                var chunk = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);
                await PublishReceivedBytesAsync(chunk, cancellationToken, countReceived: true);
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
                ReportError($"Serial receive failed: {ex.Message}", countConnectionError: true, state: SerialConnectionState.Faulted);
            }
        }
        finally
        {
            _receivedBytes.Writer.TryComplete();

            if (!cancellationToken.IsCancellationRequested)
            {
                SafeCloseAndDispose(serialPort);
                ClearCurrentPortReference(serialPort);
            }
        }
    }

    private async Task RunMockReceiverAsync(SerialSettings settings, CancellationToken cancellationToken)
    {
        var counter = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!IsMockStressRunning)
                {
                    var bytes = Encoding.UTF8.GetBytes(CreateMockMessage(counter, settings));
                    await PublishReceivedBytesAsync(bytes, cancellationToken, countReceived: true);
                    counter++;
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                    continue;
                }

                var pattern = MockGeneratorPattern;
                if (pattern is MockGeneratorPattern.NoNewlineZzz or MockGeneratorPattern.NoNewlineZzzBurst)
                {
                    var bytes = CreateMockNoNewlineChunk(pattern);
                    await PublishReceivedBytesAsync(bytes, cancellationToken, countReceived: true);
                    var delay = pattern == MockGeneratorPattern.NoNewlineZzzBurst
                        ? TimeSpan.FromMilliseconds(100)
                        : TimeSpan.FromMilliseconds(50);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                var stressBytes = CreateMockStressChunk();
                await PublishReceivedBytesAsync(stressBytes, cancellationToken, countReceived: true);

                var linesPerSecond = Math.Max(1, MockStressLinesPerSecond);
                var burstSize = Math.Max(1, MockStressBurstSize);
                var delayMilliseconds = Math.Max(1, (int)Math.Round(1000.0 * burstSize / linesPerSecond));
                await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                ReportError($"Mock serial receive failed: {ex.Message}", countConnectionError: false, state: SerialConnectionState.Faulted);
            }
        }
        finally
        {
            _receivedBytes.Writer.TryComplete();
        }
    }

    private byte[] CreateMockStressChunk()
    {
        var burstSize = Math.Max(1, MockStressBurstSize);
        using var stream = new MemoryStream(capacity: burstSize * 64);
        for (var i = 0; i < burstSize; i++)
        {
            var sequence = Interlocked.Increment(ref _mockLastGeneratedSequence);
            var line = CreateMockStressMessage(sequence);
            var lineBytes = Encoding.UTF8.GetBytes(line);
            stream.Write(lineBytes, 0, lineBytes.Length);

            if (MockStressInjectInvalidBytes && sequence % 97 == 0)
            {
                stream.WriteByte((byte)' ');
                stream.WriteByte(0xFF);
                stream.WriteByte(0xFE);
            }

            var newlineBytes = Encoding.UTF8.GetBytes(Environment.NewLine);
            stream.Write(newlineBytes, 0, newlineBytes.Length);

            Interlocked.Increment(ref _mockGeneratedLineCount);
        }

        return stream.ToArray();
    }

    private byte[] CreateMockNoNewlineChunk(MockGeneratorPattern pattern)
    {
        var sequence = Interlocked.Increment(ref _mockLastGeneratedSequence);
        var length = pattern == MockGeneratorPattern.NoNewlineZzzBurst
            ? 64 + (int)((sequence - 1) % 4) * 64
            : 1 + (int)((sequence - 1) % 3);
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)'z');
        Interlocked.Add(ref _mockNoNewlineEmittedBytes, length);
        return bytes;
    }

    private static SerialPortStream CreateSerialPort(SerialSettings settings)
    {
        var serialPort = new WinSerialPortStream(
            settings.PortName,
            settings.BaudRate,
            settings.DataBits,
            ToRjcpParity(settings.Parity),
            ToRjcpStopBits(settings.StopBits))
        {
            Handshake = ToRjcpHandshake(settings.Handshake),
            ReadBufferSize = 1024 * 1024,
            WriteBufferSize = 128 * 1024,
            // ReadAsync is canceled by the connection token. An infinite
            // stream-buffer wait avoids an otherwise unnecessary 500 ms idle
            // wake-up loop and does not participate in packet grouping.
            ReadTimeout = Timeout.Infinite,
            WriteTimeout = 1000,
            DtrEnable = settings.DtrEnable,
            RtsEnable = settings.RtsEnable
        };

        WindowsSerialReadTiming.Apply(serialPort);
        return serialPort;
    }

    private void ClearCurrentPortReference(SerialPortStream serialPort)
    {
        if (!ReferenceEquals(_serialPort, serialPort))
        {
            return;
        }

        _serialPort = null;
        _receiveCancellation?.Cancel();
    }

    private void AddReceivedChunk(int byteCount)
    {
        Interlocked.Add(ref _receivedByteCount, byteCount);
        var chunks = Interlocked.Increment(ref _receivedChunkCount);

        if (chunks == 1 || chunks % 16 == 0)
        {
            RaiseStatusChanged();
        }
    }

    public void SetRawBridgePriorityEnabled(bool enabled)
    {
        Volatile.Write(ref _rawBridgePriorityEnabled, enabled ? 1 : 0);
        RaiseStatusChanged();
    }

    private async ValueTask PublishReceivedBytesAsync(
        byte[] bytes,
        CancellationToken cancellationToken,
        bool countReceived)
    {
        var receivedChunk = ReceivedByteChunk.Capture(bytes);
        if (IsRawBridgePriorityEnabled)
        {
            PublishRawBytesReceived(bytes);
            if (!_receivedBytes.Writer.TryWrite(receivedChunk))
            {
                Interlocked.Add(ref _bridgePriorityDroppedPipelineByteCount, bytes.Length);
                Interlocked.Increment(ref _bridgePriorityDroppedPipelineChunkCount);
                RaiseStatusChanged();
            }
        }
        else
        {
            await _receivedBytes.Writer.WriteAsync(receivedChunk, cancellationToken);
        }

        if (countReceived)
        {
            AddReceivedChunk(bytes.Length);
        }
    }

    private void PublishRawBytesReceived(byte[] bytes)
    {
        try
        {
            RawBytesReceived?.Invoke(bytes);
        }
        catch (Exception ex)
        {
            ReportError($"Raw RX observer failed: {ex.Message}", countConnectionError: false);
        }
    }

    private void AddWrittenBytes(int byteCount)
    {
        Interlocked.Add(ref _writtenByteCount, byteCount);
        RaiseStatusChanged();
    }

    private void ReportError(string message, bool countConnectionError, SerialConnectionState? state = null)
    {
        if (countConnectionError)
        {
            Interlocked.Increment(ref _connectionErrorCount);
        }

        lock (_stateGate)
        {
            _lastError = message;
            if (state.HasValue)
            {
                _connectionState = state.Value;
            }
        }

        Error?.Invoke(this, message);
        RaiseStatusChanged();
    }

    private void SetConnectionState(SerialConnectionState state, bool clearLastError = false)
    {
        lock (_stateGate)
        {
            _connectionState = state;
            if (clearLastError)
            {
                _lastError = null;
            }
        }

        RaiseStatusChanged();
    }

    private void RaiseStatusChanged()
    {
        StatusChanged?.Invoke(this, EventArgs.Empty);
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

        SafeDispose(serialPort);
    }

    private static void SafeDispose(SerialPortStream? serialPort)
    {
        if (serialPort is null)
        {
            return;
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

    private static string CreateMockMessage(int counter, SerialSettings settings)
    {
        var text = counter switch
        {
            var value when value > 0 && value % 29 == 0 => $"ERROR simulated fault #{counter}",
            var value when value > 0 && value % 11 == 0 => $"WARN mock threshold reached #{counter}",
            _ => $"INFO mock serial sample #{counter} on {settings.PortName} at {settings.BaudRate}"
        };

        return text + Environment.NewLine;
    }

    private string CreateMockStressMessage(long sequence)
    {
        var level = "INFO";
        var message = "mock serial sample";
        if (MockStressInjectEvents)
        {
            if (sequence % 101 == 0)
            {
                level = "ERROR";
                message = "simulated fault";
            }
            else if (sequence % 53 == 0)
            {
                level = "FAULT";
                message = "mock bus fault";
            }
            else if (sequence % 17 == 0)
            {
                level = "WARN";
                message = "mock threshold reached";
            }
        }

        return $"{sequence:D6} {level} {message}";
    }

    private static string ToLineEnding(TxLineEndingMode mode)
    {
        return mode switch
        {
            TxLineEndingMode.Cr => "\r",
            TxLineEndingMode.Lf => "\n",
            TxLineEndingMode.Crlf => "\r\n",
            _ => string.Empty
        };
    }

    private static bool IsMockPort(string portName)
    {
        return string.Equals(portName, "MOCK", StringComparison.OrdinalIgnoreCase);
    }

    private static MockGeneratorPattern NormalizeMockGeneratorPattern(MockGeneratorPattern pattern)
    {
        return Enum.IsDefined(pattern)
            ? pattern
            : MockGeneratorPattern.NormalLines;
    }

    private static Parity ToRjcpParity(SerialParityMode parity)
    {
        return parity switch
        {
            SerialParityMode.Odd => Parity.Odd,
            SerialParityMode.Even => Parity.Even,
            SerialParityMode.Mark => Parity.Mark,
            SerialParityMode.Space => Parity.Space,
            _ => Parity.None
        };
    }

    private static StopBits ToRjcpStopBits(SerialStopBitsMode stopBits)
    {
        return stopBits switch
        {
            SerialStopBitsMode.OnePointFive => StopBits.One5,
            SerialStopBitsMode.Two => StopBits.Two,
            _ => StopBits.One
        };
    }

    private static Handshake ToRjcpHandshake(SerialHandshakeMode handshake)
    {
        return handshake switch
        {
            SerialHandshakeMode.XOn => Handshake.XOn,
            SerialHandshakeMode.Rts => Handshake.Rts,
            SerialHandshakeMode.Dtr => Handshake.Dtr,
            SerialHandshakeMode.RtsXOn => Handshake.RtsXOn,
            SerialHandshakeMode.DtrXOn => Handshake.DtrXOn,
            SerialHandshakeMode.DtrRts => Handshake.DtrRts,
            SerialHandshakeMode.DtrRtsXOn => Handshake.DtrRtsXOn,
            _ => Handshake.None
        };
    }

    private static Channel<ReceivedByteChunk> CreateChannel()
    {
        return Channel.CreateBounded<ReceivedByteChunk>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }
}
