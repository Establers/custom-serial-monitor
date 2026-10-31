using System.Text;
using System.Threading.Channels;
using RJCP.IO.Ports;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed class SerialService : ISerialService
{
    private const int SerialReadBufferBytes = 1024 * 1024;
    private const int MockVisualPacketOverheadBytes = 16;
    private static readonly TimeSpan ReceiveTeardownTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly SerialErrorAccumulator _serialErrors = new();
    private static readonly Channel<ReceivedByteChunk> EmptyReceivedBytes = CreateCompletedChannel();
    private ReceiveSession? _currentReceiveSession;
    private Task? _pendingReceiveTeardown;
    private long _receiveGeneration;
    private SerialConnectionState _connectionState = SerialConnectionState.Disconnected;
    private string? _lastError;
    private long _receivedByteCount;
    private long _receivedChunkCount;
    private long _writtenByteCount;
    private long _connectionErrorCount;
    private long _serialLineErrorBoundarySuppressionCount;
    private int _appliedReceiveIdleTimeoutMs;
    private int _usesNativeReceiveIdleTimeout;
    private int _appliedBaudRate;
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
    private bool _disposed;
    private int _writeGateDisposed;

    public event EventHandler<string>? Error;

    public event EventHandler? StatusChanged;

    public event Action<BridgeRxChunk>? RawBytesReceived;

    public event Action<long, ReceivedByteChunk>? CaptureBytesReceived;

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

    public long ReceiveSessionGeneration =>
        Volatile.Read(ref _currentReceiveSession)?.Generation ?? Interlocked.Read(ref _receiveGeneration);

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

    public long SerialFrameErrorCount => _serialErrors.FrameCount;

    public long SerialParityErrorCount => _serialErrors.ParityCount;

    public long SerialOverrunErrorCount => _serialErrors.OverrunCount;

    public long SerialRxOverErrorCount => _serialErrors.RxOverCount;

    public long SerialLineErrorBoundarySuppressionCount =>
        Interlocked.Read(ref _serialLineErrorBoundarySuppressionCount);

    public string LastSerialErrorSummary => _serialErrors.LastSummary;

    public int AppliedReceiveIdleTimeoutMs => Volatile.Read(ref _appliedReceiveIdleTimeoutMs);

    public bool UsesNativeReceiveIdleTimeout => Volatile.Read(ref _usesNativeReceiveIdleTimeout) != 0;

    public int AppliedBaudRate => Volatile.Read(ref _appliedBaudRate);

    public bool IsRawBridgePriorityEnabled => Volatile.Read(ref _rawBridgePriorityEnabled) != 0;

    public long BridgePriorityDroppedPipelineByteCount => Interlocked.Read(ref _bridgePriorityDroppedPipelineByteCount);

    public long BridgePriorityDroppedPipelineChunkCount => Interlocked.Read(ref _bridgePriorityDroppedPipelineChunkCount);

    public ChannelReader<ReceivedByteChunk> ReceivedBytes =>
        Volatile.Read(ref _currentReceiveSession)?.Channel.Reader ?? EmptyReceivedBytes.Reader;

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
            MockGeneratorPattern.VisualHexPackets => "Stress running: Visual HEX AA55 F1/F2/F3, 3-5 ms",
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

    public async Task ConnectAsync(
        SerialSettings settings,
        SerialReceiveOptions receiveOptions,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(receiveOptions);
        if (settings.BaudRate is < 1 or > 10_000_000)
            throw new ArgumentOutOfRangeException(nameof(settings), "Baud rate must be between 1 and 10,000,000 bps.");

        var normalizedReceiveOptions = receiveOptions.Normalize();

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await EnsureNoPendingReceiveTeardownAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (ConnectionState is SerialConnectionState.Connected or SerialConnectionState.Connecting)
            {
                return;
            }

            await StopCurrentConnectionAsync(cancellationToken, publishDisconnected: false);
            cancellationToken.ThrowIfCancellationRequested();

            Volatile.Write(ref _rawBridgePriorityEnabled, 0);
            Interlocked.Exchange(ref _bridgePriorityDroppedPipelineByteCount, 0);
            Interlocked.Exchange(ref _bridgePriorityDroppedPipelineChunkCount, 0);
            ResetSerialErrorCounters();
            Interlocked.Exchange(ref _serialLineErrorBoundarySuppressionCount, 0);
            Volatile.Write(ref _appliedReceiveIdleTimeoutMs, 0);
            Volatile.Write(ref _usesNativeReceiveIdleTimeout, 0);

            var session = new ReceiveSession(
                Interlocked.Increment(ref _receiveGeneration),
                IsMockPort(settings.PortName),
                cancellationToken);
            SetCurrentReceiveSession(session);
            SetConnectionState(SerialConnectionState.Connecting, clearLastError: true);

            if (session.IsMock)
            {
                Volatile.Write(ref _appliedBaudRate, settings.BaudRate);
                session.Worker = Task.Run(
                    () => RunMockReceiverAsync(session, settings.Clone()),
                    CancellationToken.None);
                SetConnectedIfCurrentSession(session);
                await EnsureConnectedAfterStartupAsync(session, cancellationToken);
                return;
            }

            BoundaryPreservingSerialPortStream? serialPort = null;
            Task? openTask = null;
            try
            {
                serialPort = CreateSerialPort(settings, normalizedReceiveOptions);
                session.Port = serialPort;
                // Subscribe before Open(). Some adapters can report errors as
                // soon as DTR/RTS and the driver state are applied during open.
                serialPort.ErrorReceived += OnSerialErrorReceived;
                openTask = Task.Run(serialPort.Open, cancellationToken);
                await openTask.WaitAsync(ReceiveTeardownTimeout, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ClearCurrentReceiveSession(session);
                session.RequestStop();
                await CleanupFailedReceiveSessionAsync(session, serialPort, openTask, cancellationToken);
                SetConnectionState(SerialConnectionState.Disconnected);
                throw;
            }
            catch (Exception ex)
            {
                ClearCurrentReceiveSession(session);
                session.RequestStop();
                await CleanupFailedReceiveSessionAsync(session, serialPort, openTask, cancellationToken);

                var message = $"Failed to open {settings.PortName}: {ex.Message}";
                ReportError(message, countConnectionError: true, state: SerialConnectionState.Faulted);
                throw new InvalidOperationException(message, ex);
            }

            Volatile.Write(
                ref _appliedReceiveIdleTimeoutMs,
                normalizedReceiveOptions.UseNativeIdleTimeout ? normalizedReceiveOptions.IdleTimeoutMs : 0);
            Volatile.Write(
                ref _usesNativeReceiveIdleTimeout,
                normalizedReceiveOptions.UseNativeIdleTimeout ? 1 : 0);
            Volatile.Write(ref _appliedBaudRate, serialPort!.BaudRate);
            session.Worker = Task.Factory.StartNew(
                () => RunSerialReceiver(session),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            SetConnectedIfCurrentSession(session);
            await EnsureConnectedAfterStartupAsync(session, cancellationToken);
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

    public async Task<int> ChangeBaudRateAsync(int baudRate, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (baudRate is < 1 or > 10_000_000)
            throw new ArgumentOutOfRangeException(nameof(baudRate), "Baud rate must be between 1 and 10,000,000 bps.");

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var session = Volatile.Read(ref _currentReceiveSession);
                if (!IsConnected || session is null)
                    throw new InvalidOperationException("Cannot change baud rate while disconnected.");
                cancellationToken.ThrowIfCancellationRequested();

                var applied = baudRate;
                if (session.Port is { } serialPort)
                {
                    // WriteAsync can finish before the driver has transmitted
                    // its queued bytes. Drain those bytes at the previous rate
                    // before changing DCB, without blocking the UI thread or
                    // purging RX/TX buffers. Retain the same port and RX session.
                    applied = await Task.Run(async () =>
                    {
                        var started = System.Diagnostics.Stopwatch.GetTimestamp();
                        while (serialPort.BytesToWrite > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(1))
                                throw new TimeoutException("Serial TX is still draining; baud rate was not changed.");
                            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        return SerialBaudRateUpdate.Apply(baudRate,
                            serialPort.ReadOpenPortBaudRate,
                            value => serialPort.BaudRate = value);
                    }, cancellationToken).ConfigureAwait(false);
                }

                // The driver change is the commit point. Cancellation after
                // it must not leave the effective setting unreported.
                Volatile.Write(ref _appliedBaudRate, applied);
                RaiseStatusChanged();
                return applied;
            }
            catch (SerialBaudRateUpdateException ex) when (!ex.StateRestored)
            {
                Volatile.Write(ref _appliedBaudRate, 0);
                ReportError(ex.Message, countConnectionError: true, state: SerialConnectionState.Faulted);
                if (Volatile.Read(ref _currentReceiveSession) is { } session)
                {
                    session.RequestStop();
                    session.Channel.Writer.TryComplete();
                    _ = StartPortCloseAfterWriteIdle(session, WaitForWriteIdleAsync());
                }
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ReportError("Baud rate update failed: " + ex.Message, countConnectionError: false);
                throw;
            }
            finally { _writeGate.Release(); }
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task SendAsync(TxCommand command, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(command);

        var payload = Infrastructure.TxPayloadEncoder.Encode(command.CommandText, TxSendMode.Terminal,
            command.LineEndingMode ?? TxLineEndingMode.None);
        await SendPayloadAsync(payload, $"{command.CommandText}{Environment.NewLine}", cancellationToken);
    }

    public async Task SendBytesAsync(byte[] payload, string mockEchoText, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(payload);

        await SendPayloadAsync(payload, $"{mockEchoText}{Environment.NewLine}", cancellationToken);
    }

    private async Task SendPayloadAsync(byte[] payload, string mockResponse, CancellationToken cancellationToken)
    {
        // Bind before waiting: a disconnect barrier can finish while a later
        // queued writer's continuation is still waiting on the UI dispatcher.
        var session = Volatile.Read(ref _currentReceiveSession);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsConnected || session is null || !IsCurrentReceiveSession(session))
            {
                const string message = "Write failed: the original serial session is disconnected or has been replaced.";
                ReportError(message, countConnectionError: false);
                throw new InvalidOperationException(message);
            }

            var serialPort = session.Port;
            if (serialPort is null)
            {
                var responseBytes = Encoding.UTF8.GetBytes(mockResponse);
                await PublishReceivedBytesAsync(session, responseBytes, cancellationToken, countReceived: false);
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
                if (IsCurrentReceiveSession(session))
                {
                    ReportError(message, countConnectionError: true, state: SerialConnectionState.Faulted);
                    session.RequestStop();
                    session.Channel.Writer.TryComplete();
                }

                _ = StartPortCloseAfterWriteIdle(
                    session,
                    WaitForWriteIdleAsync());
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
            if (Volatile.Read(ref _pendingReceiveTeardown) is null)
            {
                DisposeWriteGate();
            }
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
        if (!IsCurrentMockConnection())
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

        if (!IsCurrentMockConnection())
        {
            ReportError("Mock CRLF ignored: connect to MOCK first.", countConnectionError: false);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes("\r\n");
        var session = Volatile.Read(ref _currentReceiveSession)
            ?? throw new InvalidOperationException("Mock receive session is unavailable.");
        await PublishReceivedBytesAsync(session, bytes, cancellationToken, countReceived: true);
    }

    private async Task StopCurrentConnectionAsync(CancellationToken cancellationToken, bool publishDisconnected)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureNoPendingReceiveTeardownAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var session = TakeCurrentReceiveSession();
        if (session is null)
        {
            if (publishDisconnected && ConnectionState != SerialConnectionState.Disconnected)
            {
                SetConnectionState(SerialConnectionState.Disconnected);
            }

            return;
        }

        SetConnectionState(SerialConnectionState.Disconnecting);
        Volatile.Write(ref _appliedBaudRate, 0);
        Volatile.Write(ref _appliedReceiveIdleTimeoutMs, 0);
        Volatile.Write(ref _usesNativeReceiveIdleTimeout, 0);
        Volatile.Write(ref _rawBridgePriorityEnabled, 0);
        StopMockStress();

        session.RequestStop();
        var writeIdleTask = WaitForWriteIdleAsync();
        var portCloseTask = StartPortCloseAfterWriteIdle(session, writeIdleTask);
        var teardownTask = Task.WhenAll(
            portCloseTask,
            session.Worker ?? Task.CompletedTask);

        bool completed;
        try { completed = await WaitForTeardownAsync(teardownTask, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Native Close/Dispose may ignore cancellation. Keep ownership and
            // prevent reconnect until it really exits, but release this caller.
            DeferReceiveTeardown(session, teardownTask);
            throw;
        }
        if (!completed)
        {
            SetFaultedConnection("Serial disconnect timed out; the previous session is still shutting down.");
            DeferReceiveTeardown(session, teardownTask);
            return;
        }

        CompleteReceiveSession(session);

        if (publishDisconnected)
        {
            SetConnectionState(SerialConnectionState.Disconnected);
        }
    }

    private async Task EnsureNoPendingReceiveTeardownAsync(CancellationToken cancellationToken = default)
    {
        var pending = Volatile.Read(ref _pendingReceiveTeardown);
        if (pending is null)
        {
            return;
        }

        if (!await WaitForTeardownAsync(pending, cancellationToken))
        {
            SetFaultedConnection("Previous serial session is still shutting down.");
            throw new InvalidOperationException("Previous serial session is still shutting down.");
        }

        _ = Interlocked.CompareExchange(ref _pendingReceiveTeardown, null, pending);
    }

    private async Task CleanupFailedReceiveSessionAsync(
        ReceiveSession session,
        BoundaryPreservingSerialPortStream? serialPort,
        Task? openTask,
        CancellationToken cancellationToken)
    {
        var teardownTask = StartPortCloseAfterOpen(session, serialPort, openTask);
        // An open that outlived its deadline must not add another ten-second
        // cleanup wait. Closing concurrently with Open is unsafe for drivers.
        if (!teardownTask.IsCompleted && (cancellationToken.IsCancellationRequested || openTask is { IsCompleted: false }))
        {
            DeferReceiveTeardown(session, teardownTask);
            return;
        }
        try
        {
            if (await WaitForTeardownAsync(teardownTask, cancellationToken))
            {
                CompleteReceiveSession(session);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DeferReceiveTeardown(session, teardownTask);
            return;
        }

        DeferReceiveTeardown(session, teardownTask);
    }

    private void DeferReceiveTeardown(ReceiveSession session, Task teardownTask)
    {
        Volatile.Write(ref _pendingReceiveTeardown, teardownTask);
        _ = FinishTimedOutReceiveTeardownAsync(session, teardownTask);
    }

    private async Task FinishTimedOutReceiveTeardownAsync(ReceiveSession session, Task teardownTask)
    {
        try
        {
            await teardownTask.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            CompleteReceiveSession(session);
            _ = Interlocked.CompareExchange(ref _pendingReceiveTeardown, null, teardownTask);
            if (_disposed)
            {
                DisposeWriteGate();
            }
            else if (Volatile.Read(ref _currentReceiveSession) is null)
            {
                SetConnectionState(SerialConnectionState.Disconnected);
            }
        }
    }

    private Task StartPortClose(SerialPortStream? serialPort)
    {
        return serialPort is null
            ? Task.CompletedTask
            : Task.Run(() => SafeCloseAndDispose(serialPort));
    }

    private Task StartPortCloseAfterOpen(
        ReceiveSession session,
        SerialPortStream? serialPort,
        Task? openTask)
    {
        return session.StartPortRetirement(() => CloseAfterOpenAsync(serialPort, openTask));
    }

    private Task StartPortCloseAfterWriteIdle(
        ReceiveSession session,
        Task writeIdleTask)
    {
        return session.StartPortRetirement(() => CloseAfterWriteIdleAsync(session.Port, writeIdleTask));
    }

    private async Task CloseAfterOpenAsync(
        SerialPortStream? serialPort,
        Task? openTask)
    {
        if (openTask is not null)
        {
            try
            {
                await openTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        await StartPortClose(serialPort).ConfigureAwait(false);
    }

    private async Task CloseAfterWriteIdleAsync(
        SerialPortStream? serialPort,
        Task writeIdleTask)
    {
        await writeIdleTask.ConfigureAwait(false);
        await StartPortClose(serialPort).ConfigureAwait(false);
    }

    private async Task EnsureConnectedAfterStartupAsync(ReceiveSession session, CancellationToken cancellationToken)
    {
        if (IsCurrentReceiveSession(session) &&
            ConnectionState == SerialConnectionState.Connected)
        {
            return;
        }

        var message = LastError ?? "Serial receive worker faulted during connection startup.";
        session.RequestStop();
        await StopCurrentConnectionAsync(cancellationToken, publishDisconnected: false);
        throw new InvalidOperationException(message);
    }

    private static async Task WaitForWriteIdleAsyncCore(SemaphoreSlim writeGate)
    {
        await writeGate.WaitAsync(CancellationToken.None);
        writeGate.Release();
    }

    private Task WaitForWriteIdleAsync()
    {
        return WaitForWriteIdleAsyncCore(_writeGate);
    }

    private static async Task<bool> WaitForTeardownAsync(Task teardownTask, CancellationToken cancellationToken = default)
    {
        try
        {
            await teardownTask.WaitAsync(ReceiveTeardownTimeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return true;
        }
    }

    private void CompleteReceiveSession(ReceiveSession session)
    {
        // The pipeline owns the reader and drains accepted RX after disconnect.
        session.Channel.Writer.TryComplete();
        session.Dispose();
    }

    private void SetFaultedConnection(string message)
    {
        ReportError(message, countConnectionError: true, state: SerialConnectionState.Faulted);
    }

    private void SetConnectedIfCurrentSession(ReceiveSession session)
    {
        var changed = false;
        lock (_stateGate)
        {
            if (ReferenceEquals(Volatile.Read(ref _currentReceiveSession), session) &&
                _connectionState == SerialConnectionState.Connecting)
            {
                _connectionState = SerialConnectionState.Connected;
                _lastError = null;
                changed = true;
            }
        }

        if (changed)
        {
            RaiseStatusChanged();
        }
    }

    private void DisposeWriteGate()
    {
        if (Interlocked.Exchange(ref _writeGateDisposed, 1) == 0)
        {
            _writeGate.Dispose();
        }
    }

    private void RunSerialReceiver(ReceiveSession session)
    {
        var serialPort = session.Port!;
        var cancellationToken = session.CancellationToken;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var completion = serialPort.ReadNativeCompletion(cancellationToken);
                if (completion.BoundarySuppressedByLineError)
                {
                    Interlocked.Increment(ref _serialLineErrorBoundarySuppressionCount);
                }

                PublishReceivedChunkAsync(
                        session,
                        ReceivedByteChunk.CaptureAt(
                            completion.Bytes,
                            completion.CompletedTimestamp,
                            completion.EndsAtNativeIdleBoundary),
                        cancellationToken,
                        countReceived: true)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
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
            if (!cancellationToken.IsCancellationRequested && IsCurrentReceiveSession(session))
            {
                ReportError($"Serial receive failed: {ex.Message}", countConnectionError: true, state: SerialConnectionState.Faulted);
            }
        }
        finally
        {
            session.Channel.Writer.TryComplete();

            if (!cancellationToken.IsCancellationRequested && IsCurrentReceiveSession(session))
            {
                _ = StartPortCloseAfterWriteIdle(
                    session,
                    WaitForWriteIdleAsync());
            }

            session.Dispose();
        }
    }

    private async Task RunMockReceiverAsync(ReceiveSession session, SerialSettings settings)
    {
        var cancellationToken = session.CancellationToken;
        var counter = 0;
        var timedRandom = new Random(384_009_600);
        var timedGroup = 0;
        var timedPacketsInGroup = timedRandom.Next(2, 7);
        var timedPacketIndex = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!IsMockStressRunning)
                {
                    var bytes = Encoding.UTF8.GetBytes(CreateMockMessage(counter, settings));
                    await PublishReceivedBytesAsync(session, bytes, cancellationToken, countReceived: true);
                    counter++;
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                    continue;
                }

                var pattern = MockGeneratorPattern;
                if (pattern == MockGeneratorPattern.VisualHexPackets)
                {
                    timedPacketIndex++;
                    var bytes = CreateMockVisualPacket(
                        timedRandom,
                        timedGroup,
                        timedPacketIndex,
                        timedPacketsInGroup);
                    await PublishReceivedBytesAsync(session, bytes, cancellationToken, countReceived: true);

                    double timedDelayMilliseconds;
                    if (timedPacketIndex == timedPacketsInGroup)
                    {
                        timedGroup++;
                        timedPacketsInGroup = timedRandom.Next(2, 7);
                        timedPacketIndex = 0;
                        timedDelayMilliseconds = 25 + (timedRandom.NextDouble() * 15);
                    }
                    else
                    {
                        timedDelayMilliseconds = 3 + (timedRandom.NextDouble() * 2);
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(timedDelayMilliseconds), cancellationToken);
                    continue;
                }

                if (pattern is MockGeneratorPattern.NoNewlineZzz or MockGeneratorPattern.NoNewlineZzzBurst)
                {
                    var bytes = CreateMockNoNewlineChunk(pattern);
                    await PublishReceivedBytesAsync(session, bytes, cancellationToken, countReceived: true);
                    var delay = pattern == MockGeneratorPattern.NoNewlineZzzBurst
                        ? TimeSpan.FromMilliseconds(100)
                        : TimeSpan.FromMilliseconds(50);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                var stressBytes = CreateMockStressChunk();
                await PublishReceivedBytesAsync(session, stressBytes, cancellationToken, countReceived: true);

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
            if (!cancellationToken.IsCancellationRequested && IsCurrentReceiveSession(session))
            {
                ReportError($"Mock serial receive failed: {ex.Message}", countConnectionError: false, state: SerialConnectionState.Faulted);
            }
        }
        finally
        {
            session.Channel.Writer.TryComplete();
            session.Dispose();
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

    private byte[] CreateMockVisualPacket(
        Random random,
        int group,
        int packetIndex,
        int packetsInGroup)
    {
        var sequence = Interlocked.Increment(ref _mockLastGeneratedSequence);
        var length = random.Next(24, 65);
        var packet = new byte[length];
        packet[0] = 0xAA;
        packet[1] = 0x55;
        packet[2] = packetIndex == 1
            ? (byte)0xF1
            : packetIndex == packetsInGroup
                ? (byte)0xF3
                : (byte)0xF2;
        packet[3] = (byte)(group >> 24);
        packet[4] = (byte)(group >> 16);
        packet[5] = (byte)(group >> 8);
        packet[6] = (byte)group;
        packet[7] = checked((byte)packetIndex);
        packet[8] = checked((byte)packetsInGroup);
        packet[9] = checked((byte)length);
        packet[10] = (byte)(sequence >> 24);
        packet[11] = (byte)(sequence >> 16);
        packet[12] = (byte)(sequence >> 8);
        packet[13] = (byte)sequence;
        packet.AsSpan(14, length - MockVisualPacketOverheadBytes)
            .Fill((byte)(0x40 + packetIndex));
        packet[^2] = 0x55;
        packet[^1] = 0xAA;
        Interlocked.Increment(ref _mockGeneratedLineCount);
        return packet;
    }

    private static BoundaryPreservingSerialPortStream CreateSerialPort(
        SerialSettings settings,
        SerialReceiveOptions receiveOptions)
    {
        var serialPort = new BoundaryPreservingSerialPortStream(
            settings.PortName,
            settings.BaudRate,
            settings.DataBits,
            ToRjcpParity(settings.Parity),
            ToRjcpStopBits(settings.StopBits),
            SerialReadBufferBytes,
            receiveOptions.UseNativeIdleTimeout)
        {
            Handshake = ToRjcpHandshake(settings.Handshake),
            ReadBufferSize = SerialReadBufferBytes,
            WriteBufferSize = 128 * 1024,
            // ReadAsync is canceled by the connection token. An infinite
            // stream-buffer wait avoids an otherwise unnecessary 500 ms idle
            // wake-up loop and does not participate in packet grouping.
            ReadTimeout = Timeout.Infinite,
            WriteTimeout = 1000,
            DtrEnable = settings.DtrEnable,
            RtsEnable = settings.RtsEnable
        };

        WindowsSerialReadTiming.Apply(serialPort, receiveOptions);
        return serialPort;
    }

    private void SetCurrentReceiveSession(ReceiveSession session)
    {
        Volatile.Write(ref _currentReceiveSession, session);
    }

    private ReceiveSession? TakeCurrentReceiveSession()
    {
        return Interlocked.Exchange(ref _currentReceiveSession, null);
    }

    private void ClearCurrentReceiveSession(ReceiveSession session)
    {
        Interlocked.CompareExchange(ref _currentReceiveSession, null, session);
    }

    private bool IsCurrentReceiveSession(ReceiveSession session)
    {
        return ReferenceEquals(Volatile.Read(ref _currentReceiveSession), session);
    }

    private bool IsCurrentMockConnection()
    {
        return IsConnected && Volatile.Read(ref _currentReceiveSession)?.IsMock == true;
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

    private void OnSerialErrorReceived(object? sender, SerialErrorReceivedEventArgs args)
    {
        var session = Volatile.Read(ref _currentReceiveSession);
        if (session is null || !ReferenceEquals(session.Port, sender))
        {
            return;
        }

        _serialErrors.Record(
            args.EventType,
            DateTimeOffset.Now,
            ReceivedByteCount,
            ReceivedChunkCount);
        RaiseStatusChanged();
    }

    private void ResetSerialErrorCounters()
    {
        _serialErrors.Reset();
    }

    public void SetRawBridgePriorityEnabled(bool enabled)
    {
        Volatile.Write(ref _rawBridgePriorityEnabled, enabled ? 1 : 0);
        RaiseStatusChanged();
    }

    private async ValueTask PublishReceivedBytesAsync(
        ReceiveSession session,
        byte[] bytes,
        CancellationToken cancellationToken,
        bool countReceived)
    {
        await PublishReceivedChunkAsync(
            session,
            ReceivedByteChunk.Capture(bytes),
            cancellationToken,
            countReceived);
    }

    private async ValueTask PublishReceivedChunkAsync(
        ReceiveSession session,
        ReceivedByteChunk receivedChunk,
        CancellationToken cancellationToken,
        bool countReceived)
    {
        if (!IsCurrentReceiveSession(session))
        {
            return;
        }

        var sessionCancellation = session.CancellationToken;
        CancellationTokenSource? linkedCancellation = null;
        if (cancellationToken.CanBeCanceled && cancellationToken != sessionCancellation)
        {
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                sessionCancellation,
                cancellationToken);
            cancellationToken = linkedCancellation.Token;
        }
        else
        {
            cancellationToken = sessionCancellation;
        }

        var entered = false;
        try
        {
            await session.PublishGate.WaitAsync(cancellationToken);
            entered = true;
            cancellationToken.ThrowIfCancellationRequested();

            var bytes = receivedChunk.Bytes;
            var parserChunk = new ReceivedByteChunk(bytes, receivedChunk.ReceivedTimestamp,
                receivedChunk.EndsAtNativeIdleBoundary, session.PublishedBytes);
            session.PublishedBytes += bytes.LongLength;
            // Publish in every receive mode, before parser/bridge routing. An
            // observer must only offer to its own bounded queue.
            var captureObservers = CaptureBytesReceived;
            if (captureObservers is not null)
            {
                foreach (var observer in captureObservers.GetInvocationList().Cast<Action<long, ReceivedByteChunk>>())
                {
                    try { observer(session.Generation, receivedChunk); }
                    catch (Exception ex) { ReportError($"Capture RX observer failed: {ex.Message}", countConnectionError: false); }
                }
            }
            if (IsRawBridgePriorityEnabled)
            {
                PublishRawBytesReceived(new BridgeRxChunk(
                    bytes,
                    receivedChunk.ReceivedTimestamp,
                    receivedChunk.EndsAtNativeIdleBoundary,
                    AppliedReceiveIdleTimeoutMs));
                if (!session.Channel.Writer.TryWrite(parserChunk))
                {
                    Interlocked.Add(ref _bridgePriorityDroppedPipelineByteCount, bytes.Length);
                    Interlocked.Increment(ref _bridgePriorityDroppedPipelineChunkCount);
                    RaiseStatusChanged();
                }
            }
            else
            {
                await session.Channel.Writer.WriteAsync(parserChunk, cancellationToken);
            }

            if (countReceived)
            {
                AddReceivedChunk(bytes.Length);
            }
        }
        finally
        {
            if (entered) session.PublishGate.Release();
            linkedCancellation?.Dispose();
        }
    }

    private void PublishRawBytesReceived(BridgeRxChunk chunk)
    {
        var handlers = RawBytesReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<BridgeRxChunk>>())
        {
            try
            {
                handler(chunk);
            }
            catch (Exception ex)
            {
                ReportError($"Raw RX observer failed: {ex.Message}", countConnectionError: false);
            }
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

    private void SafeCloseAndDispose(SerialPortStream? serialPort)
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

    private void SafeDispose(SerialPortStream? serialPort)
    {
        if (serialPort is null)
        {
            return;
        }

        try
        {
            serialPort.ErrorReceived -= OnSerialErrorReceived;
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

    private static Channel<ReceivedByteChunk> CreateCompletedChannel()
    {
        var channel = CreateChannel();
        channel.Writer.TryComplete();
        return channel;
    }

    private sealed class ReceiveSession : IDisposable
    {
        // Serial RX and mock TX echo can publish concurrently. Serialize parser
        // offsets with admission, without holding a monitor across channel waits.
        public SemaphoreSlim PublishGate { get; } = new(1, 1);
        public long PublishedBytes { get; set; }
        private readonly CancellationTokenSource _cancellation;
        private readonly object _portLifecycleGate = new();
        private Task? _portRetirementTask;
        private int _disposed;

        public ReceiveSession(long generation, bool isMock, CancellationToken lifetimeToken)
        {
            Generation = generation;
            IsMock = isMock;
            Channel = CreateChannel();
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            CancellationToken = _cancellation.Token;
        }

        public long Generation { get; }

        public bool IsMock { get; }

        public Channel<ReceivedByteChunk> Channel { get; }

        public CancellationToken CancellationToken { get; }

        public BoundaryPreservingSerialPortStream? Port { get; set; }

        public Task? Worker { get; set; }

        public Task StartPortRetirement(Func<Task> retirementFactory)
        {
            lock (_portLifecycleGate)
            {
                return _portRetirementTask ??= retirementFactory();
            }
        }

        public void RequestStop()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _cancellation.Dispose();
        }
    }
}
