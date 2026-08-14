using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using RJCP.IO.Ports;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed class SerialService : ISerialService
{
    private const int SerialReadBufferBytes = 1024 * 1024;
    private const int MockVisualPacketOverheadBytes = 16;
    internal const int MaximumOutstandingReceiveSessionCount = 4;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _pendingConnectGate = new();
    private readonly object _receiveSessionGate = new();
    private readonly SerialErrorAccumulator _serialErrors = new();
    private readonly Func<SerialSettings, SerialReceiveOptions, ISerialPortConnection> _portFactory;
    private readonly Func<ReceivedByteChunk, CancellationToken, ValueTask>? _beforePublishAsync;
    private readonly Action<long>? _afterSessionErrorCurrentCheck;
    private readonly Func<BridgeRxChunk, ValueTask>? _beforeRawPublishAsync;
    private readonly Func<long, CancellationToken, ValueTask>? _beforeConnectedCommitAsync;
    private readonly Action<long>? _receiveWorkerCommitWaitEntered;
    private readonly BoundedPortLifecycle<ISerialPortConnection> _portLifecycle;
    private readonly TimeSpan _stopTimeout;
    private Channel<ReceivedByteChunk> _receivedBytes = CreateChannel();
    private CancellationTokenSource? _pendingConnectCancellation;
    private ReceiveSession? _currentReceiveSession;
    private readonly HashSet<ReceiveSession> _detachedReceiveSessions = [];
    private long _connectGeneration;
    private SerialConnectionState _connectionState = SerialConnectionState.Disconnected;
    private string? _lastError;
    private long _receivedByteCount;
    private long _receivedChunkCount;
    private long _writtenByteCount;
    private long _connectionErrorCount;
    private long _serialLineErrorBoundarySuppressionCount;
    private int _appliedReceiveIdleTimeoutMs;
    private int _usesNativeReceiveIdleTimeout;
    private int _rawBridgePriorityEnabled;
    private long _bridgePriorityDroppedPipelineByteCount;
    private long _bridgePriorityDroppedPipelineChunkCount;
    private long _mockGeneratedLineCount;
    private long _mockLastGeneratedSequence;
    private long _mockLastAcceptedSequence;
    private long _mockNoNewlineEmittedBytes;
    private long _mockNoNewlineAcceptedBytes;
    private long _mockGeneratedButNotAcceptedLineCount;
    private int _mockStressLinesPerSecond = 10;
    private int _mockStressBurstSize = 1;
    private int _mockGeneratorPattern = (int)MockGeneratorPattern.NormalLines;
    private bool _mockStressInjectEvents = true;
    private bool _mockStressInjectInvalidBytes;
    private bool _mockStressRunning;
    private bool _isMockConnection;
    private string _lastReceiveStopMode = "not started";
    private bool _disposed;

    public SerialService()
        : this(
            CreateSerialPort,
            BoundedPortLifecycle<ISerialPortConnection>.DefaultOpenTimeout,
            BoundedPortLifecycle<ISerialPortConnection>.DefaultCleanupTimeout,
            BoundedPortLifecycle<ISerialPortConnection>.DefaultMaximumOutstandingOperations)
    {
    }

    internal SerialService(
        Func<SerialSettings, SerialReceiveOptions, ISerialPortConnection> portFactory,
        TimeSpan openTimeout,
        TimeSpan stopTimeout,
        int maximumOutstandingOperations = BoundedPortLifecycle<ISerialPortConnection>.DefaultMaximumOutstandingOperations,
        Func<ReceivedByteChunk, CancellationToken, ValueTask>? beforePublishAsync = null,
        Action<long>? afterSessionErrorCurrentCheck = null,
        Func<BridgeRxChunk, ValueTask>? beforeRawPublishAsync = null,
        Func<long, CancellationToken, ValueTask>? beforeConnectedCommitAsync = null,
        Action<long>? receiveWorkerCommitWaitEntered = null)
    {
        ArgumentNullException.ThrowIfNull(portFactory);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stopTimeout, TimeSpan.Zero);
        _portFactory = portFactory;
        _beforePublishAsync = beforePublishAsync;
        _afterSessionErrorCurrentCheck = afterSessionErrorCurrentCheck;
        _beforeRawPublishAsync = beforeRawPublishAsync;
        _beforeConnectedCommitAsync = beforeConnectedCommitAsync;
        _receiveWorkerCommitWaitEntered = receiveWorkerCommitWaitEntered;
        _stopTimeout = stopTimeout;
        _portLifecycle = new BoundedPortLifecycle<ISerialPortConnection>(
            openTimeout,
            stopTimeout,
            maximumOutstandingOperations);
    }

    internal int OutstandingPortOperationCount => _portLifecycle.OutstandingOperationCount;

    internal int OutstandingReceiveSessionCount
    {
        get
        {
            lock (_receiveSessionGate)
            {
                return _detachedReceiveSessions.Count;
            }
        }
    }

    internal string LastReceiveStopMode
    {
        get
        {
            lock (_stateGate)
            {
                return _lastReceiveStopMode;
            }
        }
    }

    internal long MockGeneratedButNotAcceptedLineCount =>
        Interlocked.Read(ref _mockGeneratedButNotAcceptedLineCount);

    public event EventHandler<string>? Error;

    public event EventHandler? StatusChanged;

    public event Action<BridgeRxChunk>? RawBytesReceived;

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

    public long SerialFrameErrorCount => _serialErrors.FrameCount;

    public long SerialParityErrorCount => _serialErrors.ParityCount;

    public long SerialOverrunErrorCount => _serialErrors.OverrunCount;

    public long SerialRxOverErrorCount => _serialErrors.RxOverCount;

    public long SerialLineErrorBoundarySuppressionCount =>
        Interlocked.Read(ref _serialLineErrorBoundarySuppressionCount);

    public string LastSerialErrorSummary => _serialErrors.LastSummary;

    public int AppliedReceiveIdleTimeoutMs => Volatile.Read(ref _appliedReceiveIdleTimeoutMs);

    public bool UsesNativeReceiveIdleTimeout => Volatile.Read(ref _usesNativeReceiveIdleTimeout) != 0;

    public bool IsRawBridgePriorityEnabled => Volatile.Read(ref _rawBridgePriorityEnabled) != 0;

    public long ReceiveSessionGeneration =>
        Volatile.Read(ref _currentReceiveSession)?.Generation ?? 0;

    public long BridgePriorityDroppedPipelineByteCount => Interlocked.Read(ref _bridgePriorityDroppedPipelineByteCount);

    public long BridgePriorityDroppedPipelineChunkCount => Interlocked.Read(ref _bridgePriorityDroppedPipelineChunkCount);

    public ChannelReader<ReceivedByteChunk> ReceivedBytes => Volatile.Read(ref _receivedBytes).Reader;

    public bool IsMockStressRunning => Volatile.Read(ref _mockStressRunning);

    public int MockStressLinesPerSecond => Volatile.Read(ref _mockStressLinesPerSecond);

    public int MockStressBurstSize => Volatile.Read(ref _mockStressBurstSize);

    public bool MockStressInjectEvents => Volatile.Read(ref _mockStressInjectEvents);

    public bool MockStressInjectInvalidBytes => Volatile.Read(ref _mockStressInjectInvalidBytes);

    public long MockGeneratedLineCount => Interlocked.Read(ref _mockGeneratedLineCount);

    public long MockLastGeneratedSequence => Interlocked.Read(ref _mockLastGeneratedSequence);

    internal long MockLastAcceptedSequence => Interlocked.Read(ref _mockLastAcceptedSequence);

    public MockGeneratorPattern MockGeneratorPattern => NormalizeMockGeneratorPattern(
        (MockGeneratorPattern)Volatile.Read(ref _mockGeneratorPattern));

    public bool IsMockNoNewlineActive => IsMockStressRunning &&
        MockGeneratorPattern is MockGeneratorPattern.NoNewlineZzz or MockGeneratorPattern.NoNewlineZzzBurst;

    public long MockNoNewlineEmittedBytes => Interlocked.Read(ref _mockNoNewlineEmittedBytes);

    internal long MockNoNewlineAcceptedBytes => Interlocked.Read(ref _mockNoNewlineAcceptedBytes);

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

        var normalizedReceiveOptions = receiveOptions.Normalize();
        var (operationCancellation, generation) = BeginConnect(cancellationToken);
        var operationToken = operationCancellation.Token;
        var lifecycleEntered = false;
        try
        {
            await _lifecycleGate.WaitAsync(operationToken);
            lifecycleEntered = true;
            ThrowIfDisposed();

            if (ConnectionState is SerialConnectionState.Connected or SerialConnectionState.Connecting)
            {
                return;
            }

            await StopCurrentConnectionAsync(CancellationToken.None, publishDisconnected: false);
            EnsureReceiveSessionCapacity();

            Volatile.Write(ref _rawBridgePriorityEnabled, 0);
            Interlocked.Exchange(ref _bridgePriorityDroppedPipelineByteCount, 0);
            Interlocked.Exchange(ref _bridgePriorityDroppedPipelineChunkCount, 0);
            ResetSerialErrorCounters();
            Interlocked.Exchange(ref _serialLineErrorBoundarySuppressionCount, 0);
            Volatile.Write(ref _appliedReceiveIdleTimeoutMs, 0);
            Volatile.Write(ref _usesNativeReceiveIdleTimeout, 0);

            var receivedBytes = CreateChannel();
            Volatile.Write(ref _receivedBytes, receivedBytes);
            SetConnectionState(SerialConnectionState.Connecting, clearLastError: true);

            if (IsMockPort(settings.PortName))
            {
                ThrowIfConnectIsStale(generation, operationToken);
                // The caller token owns only this connect attempt. Once the
                // generation commits, receive lifetime is owned by
                // BeginDisconnect/DisconnectAsync.
                var session = new ReceiveSession(
                    generation,
                    receivedBytes,
                    port: null,
                    isMock: true,
                    appliedReceiveIdleTimeoutMs: 0);
                SetCurrentReceiveSession(session);
                _isMockConnection = true;
                session.Worker = Task.Run(
                    () => RunMockReceiverAsync(session, settings.Clone()),
                    CancellationToken.None);
                if (_beforeConnectedCommitAsync is not null)
                {
                    await _beforeConnectedCommitAsync(generation, operationToken);
                }

                CommitConnected(session, generation, operationToken);
                return;
            }

            _isMockConnection = false;
            ISerialPortConnection serialPort;
            EventHandler<SerialErrorReceivedEventArgs> serialErrorHandler =
                (_, args) => OnSerialErrorReceived(generation, args);
            try
            {
                serialPort = await _portLifecycle.OpenAsync(
                    () => _portFactory(settings.Clone(), normalizedReceiveOptions),
                    candidate =>
                    {
                        // Subscribe before Open(). Some adapters can report errors as
                        // soon as DTR/RTS and the driver state are applied during open.
                        candidate.ErrorReceived += serialErrorHandler;
                    },
                    operationToken);
            }
            catch (OperationCanceledException)
            {
                receivedBytes.Writer.TryComplete();
                throw;
            }
            catch (Exception ex)
            {
                receivedBytes.Writer.TryComplete();

                var message = $"Failed to open {settings.PortName}: {ex.Message}";
                ReportError(message, countConnectionError: true, state: SerialConnectionState.Faulted);
                throw new InvalidOperationException(message, ex);
            }

            // Transfer ownership immediately after OpenAsync hands it off. Any
            // later setup/cancellation failure is then retired by the common
            // StopCurrentConnectionAsync catch path.
            var serialSession = new ReceiveSession(
                generation,
                receivedBytes,
                serialPort,
                isMock: false,
                appliedReceiveIdleTimeoutMs: normalizedReceiveOptions.UseNativeIdleTimeout
                    ? normalizedReceiveOptions.IdleTimeoutMs
                    : 0,
                serialErrorHandler: serialErrorHandler);
            SetCurrentReceiveSession(serialSession);
            Volatile.Write(
                ref _appliedReceiveIdleTimeoutMs,
                normalizedReceiveOptions.UseNativeIdleTimeout ? normalizedReceiveOptions.IdleTimeoutMs : 0);
            Volatile.Write(
                ref _usesNativeReceiveIdleTimeout,
                normalizedReceiveOptions.UseNativeIdleTimeout ? 1 : 0);
            serialSession.Worker = Task.Factory.StartNew(
                () => RunSerialReceiver(serialSession, serialPort),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            if (_beforeConnectedCommitAsync is not null)
            {
                await _beforeConnectedCommitAsync(generation, operationToken);
            }

            CommitConnected(serialSession, generation, operationToken);
        }
        catch
        {
            if (lifecycleEntered)
            {
                await StopCurrentConnectionAsync(CancellationToken.None, publishDisconnected: false);
                RestoreDisconnectedAfterFailedConnect();
            }

            throw;
        }
        finally
        {
            EndConnect(operationCancellation);
            if (lifecycleEntered)
            {
                _lifecycleGate.Release();
            }
        }
    }

    public void CancelPendingConnect()
    {
        CancellationTokenSource? cancellation;
        lock (_pendingConnectGate)
        {
            _connectGeneration++;
            cancellation = _pendingConnectCancellation;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        BeginDisconnect();
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
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsConnected)
            {
                const string message = "Write failed: serial service is disconnected.";
                ReportError(message, countConnectionError: false);
                throw new InvalidOperationException(message);
            }

            var session = Volatile.Read(ref _currentReceiveSession);
            var serialPort = session?.Port;
            if (session is null)
            {
                throw new InvalidOperationException("Write failed: the serial receive session is unavailable.");
            }

            if (serialPort is null)
            {
                var responseBytes = Encoding.UTF8.GetBytes(mockResponse);
                await PublishReceivedBytesAsync(session, responseBytes, countReceived: false);
                AddWrittenBytes(payload.Length);
                return;
            }

            try
            {
                await serialPort.WriteAsync(payload.AsMemory(), cancellationToken);
                AddWrittenBytes(payload.Length);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var message = $"Serial write failed: {ex.Message}";
                ReportSessionError(session, message, countConnectionError: true);
                session.RequestReadStop();
                RetireSessionPort(session);
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

        CancelPendingConnect();
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
        Interlocked.Exchange(ref _mockLastAcceptedSequence, 0);
        Interlocked.Exchange(ref _mockNoNewlineEmittedBytes, 0);
        Interlocked.Exchange(ref _mockNoNewlineAcceptedBytes, 0);
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
        var session = Volatile.Read(ref _currentReceiveSession)
            ?? throw new InvalidOperationException("Mock receive session is unavailable.");
        cancellationToken.ThrowIfCancellationRequested();
        await PublishReceivedBytesAsync(session, bytes, countReceived: true);
    }

    private async Task StopCurrentConnectionAsync(CancellationToken cancellationToken, bool publishDisconnected)
    {
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
        _isMockConnection = false;
        Volatile.Write(ref _appliedReceiveIdleTimeoutMs, 0);
        Volatile.Write(ref _usesNativeReceiveIdleTimeout, 0);
        StopMockStress();
        session.RequestReadStop();

        var stopStartedAt = Stopwatch.GetTimestamp();
        var callerCanceled = false;
        var serialPort = session.DetachPort();
        if (serialPort is not null)
        {
            var remaining = RemainingStopTime(stopStartedAt);
            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    await RetirePortAsync(serialPort, remaining, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    callerCanceled = true;
                }
            }
            else if (!_portLifecycle.TryScheduleRetire(serialPort))
            {
                ReportError(
                    "Serial port cleanup limit is exhausted.",
                    countConnectionError: true,
                    state: SerialConnectionState.Faulted);
            }
        }
        var receiveTask = session.Worker;
        var graceful = receiveTask is null || receiveTask.IsCompleted;
        var workerRemaining = RemainingStopTime(stopStartedAt);
        if (!graceful && receiveTask is not null && workerRemaining > TimeSpan.Zero)
        {
            try
            {
                await receiveTask.WaitAsync(workerRemaining, cancellationToken);
                graceful = true;
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
                callerCanceled = cancellationToken.IsCancellationRequested;
            }
        }

        if (!graceful && receiveTask?.IsCompleted == true)
        {
            graceful = true;
        }

        if (graceful)
        {
            _ = receiveTask?.Exception;
            session.Channel.Writer.TryComplete();
            session.Dispose();
            SetLastReceiveStopMode("graceful drain");
        }
        else
        {
            session.ForceAbort();
            TrackDetachedReceiveSession(session);
            SetLastReceiveStopMode("forced abort after bounded receive drain timeout");
            ReportError(
                "Serial receive drain exceeded its bounded deadline; the old session was force-aborted and remains tracked until its worker exits.",
                countConnectionError: true,
                state: SerialConnectionState.Faulted);
        }

        if (publishDisconnected)
        {
            SetConnectionState(SerialConnectionState.Disconnected);
        }

        if (callerCanceled)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private void RunSerialReceiver(ReceiveSession session, ISerialPortConnection serialPort)
    {
        var readStopToken = session.ReadStopToken;
        try
        {
            _receiveWorkerCommitWaitEntered?.Invoke(session.Generation);
            session.WaitUntilCommittedAsync().GetAwaiter().GetResult();
            while (!readStopToken.IsCancellationRequested)
            {
                var completion = serialPort.ReadNativeCompletion(readStopToken);
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
                        countReceived: true)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException) when (readStopToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!readStopToken.IsCancellationRequested && !session.ForceAbortToken.IsCancellationRequested)
            {
                ReportSessionError(session, $"Serial receive failed: {ex.Message}", countConnectionError: true);
            }
        }
        finally
        {
            session.Channel.Writer.TryComplete();

            if (!readStopToken.IsCancellationRequested)
            {
                RetireSessionPort(session);
            }
        }
    }

    private async Task RunMockReceiverAsync(ReceiveSession session, SerialSettings settings)
    {
        var readStopToken = session.ReadStopToken;
        var counter = 0;
        var timedRandom = new Random(384_009_600);
        var timedGroup = 0;
        var timedPacketsInGroup = timedRandom.Next(2, 7);
        var timedPacketIndex = 0;

        try
        {
            _receiveWorkerCommitWaitEntered?.Invoke(session.Generation);
            await session.WaitUntilCommittedAsync();
            while (!readStopToken.IsCancellationRequested)
            {
                if (!IsMockStressRunning)
                {
                    var bytes = Encoding.UTF8.GetBytes(CreateMockMessage(counter, settings));
                    await PublishReceivedBytesAsync(session, bytes, countReceived: true);
                    counter++;
                    await Task.Delay(TimeSpan.FromMilliseconds(500), readStopToken);
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
                    await PublishReceivedBytesAsync(session, bytes, countReceived: true);
                    Interlocked.Exchange(ref _mockLastAcceptedSequence, MockLastGeneratedSequence);

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

                    await Task.Delay(TimeSpan.FromMilliseconds(timedDelayMilliseconds), readStopToken);
                    continue;
                }

                if (pattern is MockGeneratorPattern.NoNewlineZzz or MockGeneratorPattern.NoNewlineZzzBurst)
                {
                    var bytes = CreateMockNoNewlineChunk(pattern);
                    await PublishReceivedBytesAsync(session, bytes, countReceived: true);
                    Interlocked.Exchange(ref _mockLastAcceptedSequence, MockLastGeneratedSequence);
                    Interlocked.Add(ref _mockNoNewlineAcceptedBytes, bytes.Length);
                    var delay = pattern == MockGeneratorPattern.NoNewlineZzzBurst
                        ? TimeSpan.FromMilliseconds(100)
                        : TimeSpan.FromMilliseconds(50);
                    await Task.Delay(delay, readStopToken);
                    continue;
                }

                var stressBytes = CreateMockStressChunk();
                var generatedSequence = MockLastGeneratedSequence;
                var generatedLineCount = Math.Max(1, MockStressBurstSize);
                Interlocked.Add(ref _mockGeneratedButNotAcceptedLineCount, generatedLineCount);
                try
                {
                    await PublishReceivedBytesAsync(session, stressBytes, countReceived: true);
                    Interlocked.Exchange(ref _mockLastAcceptedSequence, generatedSequence);
                }
                finally
                {
                    Interlocked.Add(ref _mockGeneratedButNotAcceptedLineCount, -generatedLineCount);
                }

                var linesPerSecond = Math.Max(1, MockStressLinesPerSecond);
                var burstSize = Math.Max(1, MockStressBurstSize);
                var delayMilliseconds = Math.Max(1, (int)Math.Round(1000.0 * burstSize / linesPerSecond));
                await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds), readStopToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!readStopToken.IsCancellationRequested && !session.ForceAbortToken.IsCancellationRequested)
            {
                ReportSessionError(session, $"Mock serial receive failed: {ex.Message}", countConnectionError: false);
            }
        }
        finally
        {
            session.Channel.Writer.TryComplete();
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

    private static ISerialPortConnection CreateSerialPort(
        SerialSettings settings,
        SerialReceiveOptions receiveOptions)
    {
        BoundaryPreservingSerialPortStream? serialPort = null;
        try
        {
            serialPort = new BoundaryPreservingSerialPortStream(
                settings.PortName,
                settings.BaudRate,
                settings.DataBits,
                ToRjcpParity(settings.Parity),
                ToRjcpStopBits(settings.StopBits),
                SerialReadBufferBytes,
                receiveOptions.UseNativeIdleTimeout);
            serialPort.Handshake = ToRjcpHandshake(settings.Handshake);
            serialPort.ReadBufferSize = SerialReadBufferBytes;
            serialPort.WriteBufferSize = 128 * 1024;
            // ReadAsync is canceled by the connection token. An infinite
            // stream-buffer wait avoids an otherwise unnecessary 500 ms idle
            // wake-up loop and does not participate in packet grouping.
            serialPort.ReadTimeout = Timeout.Infinite;
            serialPort.WriteTimeout = 1000;
            serialPort.DtrEnable = settings.DtrEnable;
            serialPort.RtsEnable = settings.RtsEnable;

            WindowsSerialReadTiming.Apply(serialPort, receiveOptions);
            return new SerialPortConnectionAdapter(serialPort);
        }
        catch
        {
            try { serialPort?.Close(); } catch { }
            try { serialPort?.Dispose(); } catch { }
            throw;
        }
    }

    public void BeginDisconnect()
    {
        CancelPendingConnect();
        StopMockStress();
        // Only prevent another native read. A read that already produced bytes
        // owns its publish and the worker completes this session's channel after
        // that publish finishes. ForceAbort is reserved for the bounded stop
        // deadline and never targets a later session.
        Volatile.Read(ref _currentReceiveSession)?.RequestReadStop();
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

    private void OnSerialErrorReceived(long generation, SerialErrorReceivedEventArgs args)
    {
        var ownsCurrentSession = Volatile.Read(ref _currentReceiveSession)?.Generation == generation;
        if (!ownsCurrentSession)
        {
            lock (_pendingConnectGate)
            {
                if (_connectGeneration != generation || _pendingConnectCancellation is null)
                {
                    return;
                }
            }
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
        lock (_stateGate)
        {
            var effectiveEnabled = enabled && _currentReceiveSession is not null;
            Volatile.Write(ref _rawBridgePriorityEnabled, effectiveEnabled ? 1 : 0);
            _currentReceiveSession?.SetRawBridgePriorityEnabled(effectiveEnabled);
        }

        RaiseStatusChanged();
    }

    private async ValueTask PublishReceivedBytesAsync(
        ReceiveSession session,
        byte[] bytes,
        bool countReceived)
    {
        await PublishReceivedChunkAsync(
            session,
            ReceivedByteChunk.Capture(bytes),
            countReceived);
    }

    private async ValueTask PublishReceivedChunkAsync(
        ReceiveSession session,
        ReceivedByteChunk receivedChunk,
        bool countReceived)
    {
        if (_beforePublishAsync is not null)
        {
            await _beforePublishAsync(receivedChunk, session.ForceAbortToken);
        }

        var bytes = receivedChunk.Bytes;
        session.ForceAbortToken.ThrowIfCancellationRequested();
        if (session.IsRawBridgePriorityEnabled)
        {
            var bridgeChunk = new BridgeRxChunk(
                bytes,
                receivedChunk.ReceivedTimestamp,
                receivedChunk.EndsAtNativeIdleBoundary,
                session.AppliedReceiveIdleTimeoutMs)
            {
                SourceSerialSessionGeneration = session.Generation
            };
            if (_beforeRawPublishAsync is not null)
            {
                await _beforeRawPublishAsync(bridgeChunk);
            }

            PublishRawBytesReceived(bridgeChunk);
            if (!session.Channel.Writer.TryWrite(receivedChunk))
            {
                Interlocked.Add(ref _bridgePriorityDroppedPipelineByteCount, bytes.Length);
                Interlocked.Increment(ref _bridgePriorityDroppedPipelineChunkCount);
                RaiseStatusChanged();
            }
        }
        else
        {
            await session.Channel.Writer.WriteAsync(receivedChunk, session.ForceAbortToken);
        }

        if (countReceived)
        {
            AddReceivedChunk(bytes.Length);
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

    private void ReportSessionError(ReceiveSession session, string message, bool countConnectionError)
    {
        // This preliminary read is diagnostic/test instrumentation only. The
        // ownership decision and state mutation are linearized together below.
        _ = ReferenceEquals(Volatile.Read(ref _currentReceiveSession), session);
        _afterSessionErrorCurrentCheck?.Invoke(session.Generation);

        bool isCurrent;
        lock (_stateGate)
        {
            isCurrent = ReferenceEquals(_currentReceiveSession, session);
            if (isCurrent)
            {
                _lastError = message;
                _connectionState = SerialConnectionState.Faulted;
            }
        }

        if (countConnectionError)
        {
            Interlocked.Increment(ref _connectionErrorCount);
        }

        Error?.Invoke(
            this,
            isCurrent ? message : $"Detached serial session {session.Generation}: {message}");
        RaiseStatusChanged();
    }

    private void SetLastReceiveStopMode(string mode)
    {
        lock (_stateGate)
        {
            _lastReceiveStopMode = mode;
        }

        RaiseStatusChanged();
    }

    private void EnsureReceiveSessionCapacity()
    {
        lock (_receiveSessionGate)
        {
            if (_detachedReceiveSessions.Count >= MaximumOutstandingReceiveSessionCount)
            {
                throw new InvalidOperationException(
                    $"Serial receive cleanup capacity is exhausted " +
                    $"({_detachedReceiveSessions.Count}/{MaximumOutstandingReceiveSessionCount}); " +
                    "wait for an earlier receive worker to exit before reconnecting.");
            }
        }
    }

    private void SetCurrentReceiveSession(ReceiveSession session)
    {
        lock (_stateGate)
        {
            if (_currentReceiveSession is not null)
            {
                throw new InvalidOperationException("A serial receive session is already installed.");
            }

            Volatile.Write(ref _currentReceiveSession, session);
        }
    }

    private ReceiveSession? TakeCurrentReceiveSession()
    {
        lock (_stateGate)
        {
            var session = _currentReceiveSession;
            session?.SetRawBridgePriorityEnabled(false);
            Volatile.Write(ref _rawBridgePriorityEnabled, 0);
            Volatile.Write(ref _currentReceiveSession, null);
            return session;
        }
    }

    private void TrackDetachedReceiveSession(ReceiveSession session)
    {
        var worker = session.Worker;
        if (worker is null || worker.IsCompleted)
        {
            _ = worker?.Exception;
            session.Dispose();
            return;
        }

        lock (_receiveSessionGate)
        {
            _detachedReceiveSessions.Add(session);
        }

        _ = worker.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                lock (_receiveSessionGate)
                {
                    _detachedReceiveSessions.Remove(session);
                }

                session.Dispose();
                RaiseStatusChanged();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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

    private void RestoreDisconnectedAfterFailedConnect()
    {
        lock (_stateGate)
        {
            if (_currentReceiveSession is not null ||
                _connectionState is not (SerialConnectionState.Connecting or SerialConnectionState.Disconnecting))
            {
                return;
            }

            // Connecting means no session was installed; Disconnecting means
            // the installed session completed cleanup without reporting a
            // fault. Preserve Faulted so cleanup failures remain visible.
            _connectionState = SerialConnectionState.Disconnected;
        }

        RaiseStatusChanged();
    }

    private void RaiseStatusChanged()
    {
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RetireSessionPort(ReceiveSession session)
    {
        var serialPort = session.DetachPort();
        if (serialPort is null)
        {
            return;
        }

        if (!_portLifecycle.TryScheduleRetire(serialPort))
        {
            ReportSessionError(
                session,
                "Serial port cleanup limit is exhausted; no further connection is safe until an earlier OS operation finishes.",
                countConnectionError: true);
        }
    }

    private async Task RetirePortAsync(
        ISerialPortConnection serialPort,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!await _portLifecycle.RetireAsync(serialPort, timeout, cancellationToken).ConfigureAwait(false))
        {
            ReportError(
                "Serial port cleanup exceeded its bounded deadline or lifecycle-operation limit.",
                countConnectionError: true,
                state: SerialConnectionState.Faulted);
        }
    }

    private TimeSpan RemainingStopTime(long startedAt) =>
        _stopTimeout - Stopwatch.GetElapsedTime(startedAt);

    private (CancellationTokenSource Cancellation, long Generation) BeginConnect(
        CancellationToken cancellationToken)
    {
        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_pendingConnectGate)
        {
            if (_pendingConnectCancellation is not null)
            {
                operationCancellation.Dispose();
                throw new InvalidOperationException("A serial connection attempt is already in progress.");
            }

            _pendingConnectCancellation = operationCancellation;
            return (operationCancellation, ++_connectGeneration);
        }
    }

    private void EndConnect(CancellationTokenSource operationCancellation)
    {
        lock (_pendingConnectGate)
        {
            if (ReferenceEquals(_pendingConnectCancellation, operationCancellation))
            {
                _pendingConnectCancellation = null;
            }
        }

        operationCancellation.Dispose();
    }

    private void ThrowIfConnectIsStale(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_pendingConnectGate)
        {
            if (generation != _connectGeneration || _pendingConnectCancellation is null)
            {
                throw new OperationCanceledException("The serial connection attempt was superseded.", cancellationToken);
            }
        }
    }

    private void CommitConnected(
        ReceiveSession session,
        long generation,
        CancellationToken cancellationToken)
    {
        lock (_pendingConnectGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _connectGeneration || _pendingConnectCancellation is null)
            {
                throw new OperationCanceledException("The serial connection attempt was superseded.", cancellationToken);
            }

            lock (_stateGate)
            {
                if (!ReferenceEquals(_currentReceiveSession, session))
                {
                    throw new OperationCanceledException(
                        "The serial receive session was replaced before commit.",
                        cancellationToken);
                }

                _connectionState = SerialConnectionState.Connected;
                _lastError = null;
            }

            session.CommitStarted();

            // Publish while generation ownership is still held. A concurrent
            // cancel therefore linearizes strictly before this event (no
            // publish) or after it (disconnect subsequently publishes Off).
            RaiseStatusChanged();
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

    private sealed class ReceiveSession : IDisposable
    {
        private readonly CancellationTokenSource _readStop = new();
        private readonly CancellationTokenSource _forceAbort = new();
        private readonly EventHandler<SerialErrorReceivedEventArgs>? _serialErrorHandler;
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ISerialPortConnection? _port;
        private int _disposed;
        private int _rawBridgePriorityEnabled;

        public ReceiveSession(
            long generation,
            Channel<ReceivedByteChunk> channel,
            ISerialPortConnection? port,
            bool isMock,
            int appliedReceiveIdleTimeoutMs,
            EventHandler<SerialErrorReceivedEventArgs>? serialErrorHandler = null)
        {
            Generation = generation;
            Channel = channel;
            _port = port;
            IsMock = isMock;
            AppliedReceiveIdleTimeoutMs = appliedReceiveIdleTimeoutMs;
            _serialErrorHandler = serialErrorHandler;
        }

        public long Generation { get; }

        public Channel<ReceivedByteChunk> Channel { get; }

        public bool IsMock { get; }

        public int AppliedReceiveIdleTimeoutMs { get; }

        public Task? Worker { get; set; }

        public CancellationToken ReadStopToken => _readStop.Token;

        public CancellationToken ForceAbortToken => _forceAbort.Token;

        public ISerialPortConnection? Port => Volatile.Read(ref _port);

        public bool IsRawBridgePriorityEnabled => Volatile.Read(ref _rawBridgePriorityEnabled) != 0;

        public Task WaitUntilCommittedAsync() => _started.Task.WaitAsync(ReadStopToken);

        public void CommitStarted() => _started.TrySetResult();

        public void RequestReadStop()
        {
            try { _readStop.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void ForceAbort()
        {
            RequestReadStop();
            try { _forceAbort.Cancel(); }
            catch (ObjectDisposedException) { }
            Channel.Writer.TryComplete(new OperationCanceledException("Serial receive session was force-aborted."));
        }

        public ISerialPortConnection? DetachPort()
        {
            var port = Interlocked.Exchange(ref _port, null);
            if (port is not null && _serialErrorHandler is not null)
            {
                try { port.ErrorReceived -= _serialErrorHandler; }
                catch { }
            }

            return port;
        }

        public void SetRawBridgePriorityEnabled(bool enabled) =>
            Volatile.Write(ref _rawBridgePriorityEnabled, enabled ? 1 : 0);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _readStop.Dispose();
            _forceAbort.Dispose();
        }
    }
}
