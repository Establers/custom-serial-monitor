using System.Buffers;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed class FileLogWriter : IFileLogWriter
{
    internal const int DefaultQueueCapacity = 140_000;
    private const int FlushLineInterval = 100;
    private const int InitialBatchBufferSize = 4 * 1024;
    internal const int MaximumDetachedCleanupCount = 8;
    internal const int MaximumOutstandingCleanupOperationCount = MaximumDetachedCleanupCount + 1;
    internal const int DefaultMaximumRecoveryAttempts = 12;
    internal const int MaximumConsecutiveCloseFailureCount = 3;
    internal const int IngressReserveNumerator = 5;
    internal const int IngressReserveDenominator = 4;
    private static readonly TimeSpan FlushTimeInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan DefaultIoTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DefaultShutdownDrainTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultForcedShutdownTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan DefaultRecoveryRetryInterval = TimeSpan.FromMilliseconds(25);
    internal static readonly TimeSpan DefaultRecoveryTimeBudget = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan SchedulingJitterReserve = TimeSpan.FromMilliseconds(100);
    private static readonly byte[] LineEndingBytes = Encoding.UTF8.GetBytes(Environment.NewLine);

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _cleanupGate = new();
    private readonly Func<string, FileMode, Stream> _streamFactory;
    private readonly TimeSpan _ioTimeout;
    private readonly int _flushLineInterval;
    private readonly TimeSpan _flushTimeInterval;
    private readonly TimeSpan _shutdownDrainTimeout;
    private readonly TimeSpan _forcedShutdownTimeout;
    private readonly TimeSpan _recoveryRetryInterval;
    private readonly TimeSpan _recoveryTimeBudget;
    private readonly int _maximumRecoveryAttempts;
    private readonly int _queueCapacity;
    private readonly ArrayPool<byte> _bufferPool;
    private readonly bool _deleteAbandonedExplicitFiles;
    private readonly TimeProvider _timeProvider;
    private readonly HashSet<Task> _detachedCleanupTasks = [];
    private Channel<FileLogWriteRequest> _queue;
    private CancellationTokenSource? _writerCancellation;
    private Task? _writerTask;
    private AcceptedLineTracker? _acceptedLineTracker;
    private string _directory = CreateDefaultLogDirectory();
    private string? _currentLogFilePath;
    private string? _lastLogFilePath;
    private string? _lastFileError;
    private long _writtenLineCount;
    private long _writtenByteCount;
    private long _fileErrorCount;
    private long _droppedLineCount;
    private long _writeTimeoutCount;
    private long _recoveryCount;
    private long _waitOperationCount;
    private long _batchDeadlineCreationCount;
    private int _pendingRequestCount;
    private long _startCount;
    private long _stopCount;
    private long _lifecycleErrorCount;
    private long _maximumFileSizeBytes;
    private int _consecutiveCloseFailureCount;
    private string _lastLifecycleAction = "File logging has not started.";
    private string _logFileName = string.Empty;
    private string _logRunTimeText = string.Empty;
    private bool _rotationRequested;
    private FileLogWriterState _writerState = FileLogWriterState.Stopped;
    private FileLogWriterFaultInfo? _lastFault;
    private bool _writerStopPending;
    private bool _stopCountRecordedForCurrentWriter;
    private bool _disposed;

    static FileLogWriter()
    {
        if (DefaultQueueCapacity < RequiredProtectedQueueCapacity)
        {
            throw new InvalidOperationException(
                $"The default file-log queue capacity ({DefaultQueueCapacity:N0}) is below the " +
                $"production ingress requirement ({RequiredProtectedQueueCapacity:N0}).");
        }
    }

    public FileLogWriter()
        : this(
            CreateFileStream,
            DefaultIoTimeout,
            FlushLineInterval,
            FlushTimeInterval,
            DefaultShutdownDrainTimeout,
            DefaultForcedShutdownTimeout,
            DefaultRecoveryRetryInterval,
            DefaultMaximumRecoveryAttempts,
            DefaultRecoveryTimeBudget,
            DefaultQueueCapacity,
            ArrayPool<byte>.Shared,
            deleteAbandonedExplicitFiles: true,
            TimeProvider.System)
    {
    }

    internal FileLogWriter(
        Func<string, FileMode, Stream> streamFactory,
        TimeSpan ioTimeout,
        int flushLineInterval,
        TimeSpan? flushTimeInterval = null,
        TimeSpan? shutdownDrainTimeout = null,
        TimeSpan? forcedShutdownTimeout = null,
        TimeSpan? recoveryRetryInterval = null,
        int maximumRecoveryAttempts = DefaultMaximumRecoveryAttempts,
        TimeSpan? recoveryTimeBudget = null,
        int queueCapacity = DefaultQueueCapacity,
        ArrayPool<byte>? bufferPool = null,
        bool deleteAbandonedExplicitFiles = true,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ioTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(flushLineInterval, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRecoveryAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);

        _streamFactory = streamFactory;
        _ioTimeout = ioTimeout;
        _flushLineInterval = flushLineInterval;
        _flushTimeInterval = EnsurePositive(flushTimeInterval ?? FlushTimeInterval, nameof(flushTimeInterval));
        _shutdownDrainTimeout = EnsurePositive(
            shutdownDrainTimeout ?? DefaultShutdownDrainTimeout,
            nameof(shutdownDrainTimeout));
        _forcedShutdownTimeout = EnsurePositive(
            forcedShutdownTimeout ?? DefaultForcedShutdownTimeout,
            nameof(forcedShutdownTimeout));
        _recoveryRetryInterval = EnsurePositive(
            recoveryRetryInterval ?? DefaultRecoveryRetryInterval,
            nameof(recoveryRetryInterval));
        _maximumRecoveryAttempts = maximumRecoveryAttempts;
        _recoveryTimeBudget = EnsurePositive(
            recoveryTimeBudget ?? DefaultRecoveryTimeBudget,
            nameof(recoveryTimeBudget));
        _queueCapacity = queueCapacity;
        _bufferPool = bufferPool ?? ArrayPool<byte>.Shared;
        _deleteAbandonedExplicitFiles = deleteAbandonedExplicitFiles;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _queue = CreateQueue(_queueCapacity);
    }

    public event EventHandler<string>? Error;

    public event EventHandler? StatusChanged;

    public bool IsRunning
    {
        get
        {
            lock (_stateGate)
            {
                return _writerState == FileLogWriterState.Running;
            }
        }
    }

    public FileLogWriterState State
    {
        get
        {
            lock (_stateGate)
            {
                return _writerState;
            }
        }
    }

    public FileLogWriterFaultInfo? LastFault
    {
        get
        {
            lock (_stateGate)
            {
                return _lastFault;
            }
        }
    }

    public bool CanAutoRecover => LastFault?.CanAutoRecover == true;

    public string LogDirectory
    {
        get
        {
            lock (_stateGate)
            {
                return _directory;
            }
        }
    }

    public string? CurrentLogFilePath
    {
        get
        {
            lock (_stateGate)
            {
                return _currentLogFilePath;
            }
        }
    }

    public string? LastLogFilePath
    {
        get
        {
            lock (_stateGate)
            {
                return _lastLogFilePath;
            }
        }
    }

    public string? LastFileError
    {
        get
        {
            lock (_stateGate)
            {
                return _lastFileError;
            }
        }
    }

    public long WrittenLineCount => Interlocked.Read(ref _writtenLineCount);

    public long WrittenByteCount => Interlocked.Read(ref _writtenByteCount);

    public long FileErrorCount => Interlocked.Read(ref _fileErrorCount);

    public long DroppedLineCount => Interlocked.Read(ref _droppedLineCount);

    public long WriteTimeoutCount => Interlocked.Read(ref _writeTimeoutCount);

    public long RecoveryCount => Interlocked.Read(ref _recoveryCount);

    internal long WaitOperationCount => Interlocked.Read(ref _waitOperationCount);

    internal long BatchDeadlineCreationCount => Interlocked.Read(ref _batchDeadlineCreationCount);

    internal int DetachedCleanupCount
    {
        get
        {
            lock (_cleanupGate)
            {
                _detachedCleanupTasks.RemoveWhere(static task => task.IsCompleted);
                return _detachedCleanupTasks.Count;
            }
        }
    }

    public int PendingRequestCount => Volatile.Read(ref _pendingRequestCount);

    public long StartCount => Interlocked.Read(ref _startCount);

    public long StopCount => Interlocked.Read(ref _stopCount);

    public long LifecycleErrorCount => Interlocked.Read(ref _lifecycleErrorCount);

    internal static int MaximumWireRecordsPerSecond =>
        SerialPortPolicy.MaximumSupportedBaudRate /
        SerialPortPolicy.MinimumSupportedBitsPerCharacter;

    internal static int ReservedIngressRecordsPerSecond => checked(
        (MaximumWireRecordsPerSecond * IngressReserveNumerator + IngressReserveDenominator - 1) /
        IngressReserveDenominator);

    internal static TimeSpan ProtectedIngressWindow =>
        DefaultIoTimeout + DefaultRecoveryTimeBudget + SchedulingJitterReserve;

    internal static int RequiredProtectedQueueCapacity => checked((int)Math.Ceiling(
        ReservedIngressRecordsPerSecond * ProtectedIngressWindow.TotalSeconds));

    public string LastLifecycleAction
    {
        get
        {
            lock (_stateGate)
            {
                return _lastLifecycleAction;
            }
        }
    }

    public long MaximumFileSizeBytes
    {
        get => Interlocked.Read(ref _maximumFileSizeBytes);
        set => Interlocked.Exchange(ref _maximumFileSizeBytes, Math.Max(0, value));
    }

    public void UpdateLogFileName(string? exactLogFileName, bool requestNewFile)
    {
        var normalizedLogFileName = LogFileNamePolicy.Validate(exactLogFileName);
        var naming = new LogFileNamingSnapshot(normalizedLogFileName);
        if (requestNewFile && IsRunning && _writerTask is not null)
        {
            Interlocked.Increment(ref _pendingRequestCount);
            if (_queue.Writer.TryWrite(FileLogWriteRequest.ForNaming(naming)))
            {
                SetLifecycleAction(string.IsNullOrWhiteSpace(normalizedLogFileName)
                    ? "Log file name cleared; creating a new timestamped log."
                    : $"Log file name active: {normalizedLogFileName}");
                return;
            }

            Interlocked.Decrement(ref _pendingRequestCount);
        }

        lock (_stateGate)
        {
            ApplyLogFileNamingState(naming);
            if (requestNewFile && _writerState == FileLogWriterState.Running)
            {
                _rotationRequested = true;
                _lastLifecycleAction = string.IsNullOrWhiteSpace(normalizedLogFileName)
                    ? "Log file name cleared; creating a new timestamped log."
                    : $"Log file name active: {normalizedLogFileName}";
            }
        }

        RaiseStatusChanged();
    }

    public async Task StartAsync(string directory, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();

            if (_writerTask is not null && !_writerTask.IsCompleted)
            {
                if (_writerStopPending)
                {
                    const string message =
                        "File logging cannot restart while the previous writer is still terminating.";
                    SetLifecycleAction(message);
                    throw new FileWriterStillStoppingException(message);
                }

                SetLifecycleAction("Start ignored: file logging is already running.");
                return;
            }

            if (_writerTask is not null)
            {
                FinalizeCompletedWriter(_writerTask, "Previous file writer cleanup completed.");
            }

            if (DetachedCleanupCount >= MaximumOutstandingCleanupOperationCount)
            {
                var exception = new InvalidOperationException(
                    $"File logging cannot restart while {MaximumOutstandingCleanupOperationCount} " +
                    "stream cleanup operations are still pending.");
                RecordWriterFault(exception, FileLogWriterFaultCategory.CleanupLimit);
                throw exception;
            }

            if (Volatile.Read(ref _consecutiveCloseFailureCount) >= MaximumConsecutiveCloseFailureCount)
            {
                var exception = new InvalidOperationException(
                    $"File logging cannot restart after {MaximumConsecutiveCloseFailureCount} " +
                    "consecutive stream close failures on this writer instance.");
                RecordWriterFault(exception, FileLogWriterFaultCategory.CloseFailureLimit);
                throw exception;
            }

            var allowExistingExplicitFileRecovery =
                State == FileLogWriterState.Faulted && CanAutoRecover;
            _directory = string.IsNullOrWhiteSpace(directory) ? CreateDefaultLogDirectory() : directory;

            var openedAt = DateTimeOffset.Now;
            lock (_stateGate)
            {
                _logRunTimeText = openedAt.LocalDateTime.ToString("HHmmss");
            }
            SetCurrentLogFilePath(null);
            _queue = CreateQueue(_queueCapacity);
            Volatile.Write(ref _pendingRequestCount, 0);
            var acceptedLineTracker = new AcceptedLineTracker();
            _acceptedLineTracker = acceptedLineTracker;
            _writerStopPending = false;
            _stopCountRecordedForCurrentWriter = false;
            var openCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Increment(ref _pendingRequestCount);
            if (!_queue.Writer.TryWrite(FileLogWriteRequest.ForOpen(
                    openedAt,
                    openCompletion,
                    allowExistingExplicitFileRecovery)))
            {
                Interlocked.Decrement(ref _pendingRequestCount);
                throw new InvalidOperationException("Could not queue the initial serial log file open request.");
            }

            _writerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Interlocked.Increment(ref _startCount);
            SetLifecycleAction($"Starting file logging: {_directory}", raiseStatusChanged: false);
            SetWriterState(FileLogWriterState.Starting, clearLastError: true);
            _writerTask = Task.Run(
                () => ProcessAsync(_queue.Reader, acceptedLineTracker, _writerCancellation.Token),
                CancellationToken.None);
            await openCompletion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopWriterAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not FileWriterStillStoppingException)
        {
            if (State != FileLogWriterState.Faulted)
            {
                RecordWriterFault(ex);
            }

            RecordLifecycleError($"File logging start failed: {ex.Message}");
            ReportFileError($"File logging start failed: {ex.Message}");
            await StopWriterAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public bool TryEnqueue(LogLine line)
    {
        var acceptedLineTracker = _acceptedLineTracker;
        if (!IsRunning || _writerTask is null || acceptedLineTracker is null)
        {
            if (State == FileLogWriterState.Faulted || _writerTask is not null)
            {
                RecordDroppedLine("File writer is not running. Dropped log lines");
            }

            return false;
        }

        if (!acceptedLineTracker.TryAccept())
        {
            RecordDroppedLine("File writer is stopping. Dropped log lines");
            return false;
        }

        Interlocked.Increment(ref _pendingRequestCount);
        if (_queue.Writer.TryWrite(FileLogWriteRequest.ForLine(line)))
        {
            return true;
        }

        Interlocked.Decrement(ref _pendingRequestCount);
        if (acceptedLineTracker.RollBackAcceptance())
        {
            RecordDroppedLine("File log queue is full. Dropped log lines");
        }

        return false;
    }

    private void RecordDroppedLine(string reason)
    {
        var dropped = Interlocked.Increment(ref _droppedLineCount);
        if (dropped == 1 || dropped % 1000 == 0)
        {
            ReportFileError($"{reason}: {dropped:N0}");
        }
        else
        {
            RaiseStatusChanged();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed && _writerTask is null)
        {
            return;
        }

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopWriterAsync(cancellationToken);
        }
        finally
        {
            _lifecycleGate.Release();
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
            await StopWriterAsync(CancellationToken.None);
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }

    private async Task StopWriterAsync(CancellationToken cancellationToken)
    {
        var writerTask = _writerTask;
        if (writerTask is null)
        {
            if (State is not FileLogWriterState.Stopped and not FileLogWriterState.Faulted)
            {
                SetWriterState(FileLogWriterState.Stopped);
            }

            SetLifecycleAction("Stop ignored: file logging is not running.");
            return;
        }

        var firstStopRequest = !_writerStopPending;
        if (firstStopRequest)
        {
            _writerStopPending = true;
            if (State != FileLogWriterState.Faulted)
            {
                SetWriterState(FileLogWriterState.Stopping);
            }
            SetLifecycleAction("Stopping file logging.");
            _acceptedLineTracker?.StopAccepting();
            _queue.Writer.TryComplete();
        }

        var stopWasCanceled = false;
        var writerCompleted = writerTask.IsCompleted;

        if (!writerCompleted && firstStopRequest)
        {
            try
            {
                writerCompleted = await WaitForWriterCompletionAsync(
                    writerTask,
                    _shutdownDrainTimeout,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopWasCanceled = true;
            }
        }

        if (!writerCompleted)
        {
            if (firstStopRequest && !stopWasCanceled)
            {
                ReportFileError(
                    $"File logging did not drain within {_shutdownDrainTimeout.TotalSeconds:0.###} seconds. " +
                    "Canceling recovery and dropping accepted lines that cannot be made durable.");
            }

            _writerCancellation?.Cancel();
            writerCompleted = await WaitForWriterCompletionAsync(
                writerTask,
                _forcedShutdownTimeout,
                CancellationToken.None);
        }

        writerCompleted |= writerTask.IsCompleted;

        if (writerCompleted)
        {
            FinalizeCompletedWriter(writerTask, "Stopped file logging.");
        }
        else
        {
            RecordOutstandingAcceptedLinesAsDropped(
                _acceptedLineTracker,
                "File writer did not stop after cancellation");
            RecordStopCountOnce();
            SetCurrentLogFilePath(null);
            SetLifecycleAction(
                $"File writer is still terminating after the {_forcedShutdownTimeout.TotalSeconds:0.###} " +
                "second cancellation window. Restart is disabled until it exits.",
                raiseStatusChanged: false);
            if (State != FileLogWriterState.Faulted)
            {
                SetWriterState(FileLogWriterState.Stopping);
            }
            ObserveDetachedTask(writerTask);
        }

        if (stopWasCanceled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static async Task<bool> WaitForWriterCompletionAsync(
        Task writerTask,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await writerTask.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private void FinalizeCompletedWriter(Task writerTask, string lifecycleMessage)
    {
        if (!ReferenceEquals(_writerTask, writerTask) || !writerTask.IsCompleted)
        {
            return;
        }

        _ = writerTask.Exception;
        _writerCancellation?.Dispose();
        _writerCancellation = null;
        _writerTask = null;
        _acceptedLineTracker = null;
        _writerStopPending = false;
        RecordStopCountOnce();
        SetLifecycleAction(lifecycleMessage, raiseStatusChanged: false);
        if (State != FileLogWriterState.Faulted)
        {
            SetWriterState(FileLogWriterState.Stopped);
        }
    }

    private void RecordStopCountOnce()
    {
        if (_stopCountRecordedForCurrentWriter)
        {
            return;
        }

        _stopCountRecordedForCurrentWriter = true;
        Interlocked.Increment(ref _stopCount);
    }

    private async Task ProcessAsync(
        ChannelReader<FileLogWriteRequest> reader,
        AcceptedLineTracker acceptedLineTracker,
        CancellationToken cancellationToken)
    {
        var state = new ActiveWriterState();
        var pendingBatch = new PooledBatchBuffer(_bufferPool, InitialBatchBufferSize);
        long? pendingBatchStartedTimestamp = null;
        CancellationTokenSource? batchDeadlineCancellation = null;

        async Task FlushPendingAsync()
        {
            pendingBatch = await FlushPendingBatchWithRecoveryAsync(
                state,
                pendingBatch,
                acceptedLineTracker,
                cancellationToken);
        }

        async Task TerminatePartialBeforeCloseAsync()
        {
            await FlushPendingAsync();
            ClearBatchDeadline();
            if (!state.PartialFraming.IsOpen)
            {
                return;
            }

            pendingBatch.AppendPartialBoundary(state.PartialFraming);
            await FlushPendingAsync();
            ClearBatchDeadline();
        }

        void StartBatchDeadline()
        {
            if (pendingBatchStartedTimestamp.HasValue)
            {
                return;
            }

            pendingBatchStartedTimestamp = Stopwatch.GetTimestamp();
        }

        void ClearBatchDeadline()
        {
            pendingBatchStartedTimestamp = null;
            batchDeadlineCancellation?.Dispose();
            batchDeadlineCancellation = null;
        }

        try
        {
            while (true)
            {
                while (reader.TryRead(out var request))
                {
                    Interlocked.Decrement(ref _pendingRequestCount);
                    if (request.Naming.HasValue)
                    {
                        await TerminatePartialBeforeCloseAsync();
                        await CloseActiveStreamAsync(state, cancellationToken);
                        ApplyLogFileNamingState(request.Naming.Value);
                        state.Reset();
                        RaiseStatusChanged();
                        continue;
                    }

                    if (request.OpenedAt.HasValue)
                    {
                        try
                        {
                            var openedDate = request.OpenedAt.Value.LocalDateTime.ToString("yyyy-MM-dd");
                            var openNaming = GetLogFileNamingSnapshot();
                            PrepareWriterState(
                                state,
                                openedDate,
                                rotationIndex: 0,
                                openNaming,
                                request.AllowExistingExplicitFileRecovery);
                            await OpenRecoveryWriterUntilAvailableAsync(
                                state,
                                new RecoveryBudget(_maximumRecoveryAttempts, _recoveryTimeBudget, _timeProvider),
                                cancellationToken);
                            SetWriterState(
                                FileLogWriterState.Running,
                                clearLastFault: true);
                            request.OpenCompletion?.TrySetResult(state.Path);
                        }
                        catch (Exception ex)
                        {
                            request.OpenCompletion?.TrySetException(ex);
                            throw;
                        }

                        continue;
                    }

                    var line = request.Line;
                    if (line is null)
                    {
                        continue;
                    }

                    var lineDate = line.Timestamp.LocalDateTime.ToString("yyyy-MM-dd");
                    var naming = GetLogFileNamingSnapshot();
                    var lineLogIdentity = CreateLogFileIdentity(naming);
                    var rotationRequested = ConsumeRotationRequest();
                    if (rotationRequested ||
                        !string.Equals(state.LogIdentity, lineLogIdentity, StringComparison.Ordinal))
                    {
                        await TerminatePartialBeforeCloseAsync();
                        await CloseActiveStreamAsync(state, cancellationToken);
                        PrepareWriterState(state, lineDate, rotationIndex: 0, naming);
                    }
                    else if (string.IsNullOrEmpty(state.DateText))
                    {
                        PrepareWriterState(state, lineDate, rotationIndex: 0, naming);
                    }

                    // A value of 0 disables optional size-based rotation.
                    var maxFileSizeBytes = MaximumFileSizeBytes;
                    var pendingSizeBytes = pendingBatch.Length;
                    if (maxFileSizeBytes > 0 && state.SizeBytes + pendingSizeBytes >= maxFileSizeBytes)
                    {
                        await FlushPendingAsync();
                        ClearBatchDeadline();
                        if (state.SizeBytes >= maxFileSizeBytes)
                        {
                            await TerminatePartialBeforeCloseAsync();
                            await CloseActiveStreamAsync(state, cancellationToken);
                            PrepareWriterState(
                                state,
                                state.DateText,
                                state.RotationIndex + 1,
                                state.Naming);
                        }
                    }

                    pendingBatch.AppendLine(
                        line,
                        pendingBatch.HasContent
                            ? pendingBatch.EndingPartialFraming
                            : state.PartialFraming);
                    StartBatchDeadline();
                    if (pendingBatch.LineCount >= _flushLineInterval)
                    {
                        await FlushPendingAsync();
                        ClearBatchDeadline();
                        RaiseStatusChanged();
                    }
                    else if (pendingBatchStartedTimestamp.HasValue &&
                             Stopwatch.GetElapsedTime(pendingBatchStartedTimestamp.Value) >= _flushTimeInterval)
                    {
                        await FlushPendingAsync();
                        ClearBatchDeadline();
                        RaiseStatusChanged();
                    }
                }

                if (pendingBatchStartedTimestamp.HasValue)
                {
                    var remaining = _flushTimeInterval -
                        Stopwatch.GetElapsedTime(pendingBatchStartedTimestamp.Value);
                    if (remaining <= TimeSpan.Zero)
                    {
                        await FlushPendingAsync();
                        ClearBatchDeadline();
                        RaiseStatusChanged();
                        continue;
                    }

                    if (batchDeadlineCancellation is null)
                    {
                        batchDeadlineCancellation =
                            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        batchDeadlineCancellation.CancelAfter(remaining);
                        Interlocked.Increment(ref _batchDeadlineCreationCount);
                    }

                    Interlocked.Increment(ref _waitOperationCount);
                    try
                    {
                        if (!await reader.WaitToReadAsync(batchDeadlineCancellation!.Token))
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                        when (!cancellationToken.IsCancellationRequested && batchDeadlineCancellation!.IsCancellationRequested)
                    {
                        await FlushPendingAsync();
                        ClearBatchDeadline();
                        RaiseStatusChanged();
                    }

                    continue;
                }

                Interlocked.Increment(ref _waitOperationCount);
                if (!await reader.WaitToReadAsync(cancellationToken))
                {
                    break;
                }
            }

            await TerminatePartialBeforeCloseAsync();
        }
        catch (FileIoOperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                RetireFailedStream(state, ex);
            }
            catch (Exception cleanupException)
            {
                ReportFileError(
                    $"File writer could not track the canceled {ex.Action} cleanup: {cleanupException.Message}");
            }

            RecordOutstandingAcceptedLinesAsDropped(
                acceptedLineTracker,
                $"File writer was canceled during an in-flight {ex.Action}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RecordOutstandingAcceptedLinesAsDropped(
                acceptedLineTracker,
                "File writer was canceled before queued lines were durable");
        }
        catch (Exception ex)
        {
            RecordWriterFault(ex);
            ReportFileError($"File logging failed: {ex.Message}");
            RecordOutstandingAcceptedLinesAsDropped(
                acceptedLineTracker,
                "File writer stopped before queued lines were durable");
        }
        finally
        {
            ClearBatchDeadline();
            try
            {
                await CloseActiveStreamAsync(state, cancellationToken);
            }
            catch (Exception ex)
            {
                ReportFileError($"File writer final stream cleanup failed: {ex.Message}");
                RecordOutstandingAcceptedLinesAsDropped(
                    acceptedLineTracker,
                    "File writer stopped during final stream cleanup");
            }
            finally
            {
                pendingBatch.Dispose();
                _queue.Writer.TryComplete();
                Volatile.Write(ref _pendingRequestCount, 0);
                SetCurrentLogFilePath(null);
                SetLifecycleAction("File writer task stopped.", raiseStatusChanged: false);
                if (State != FileLogWriterState.Faulted)
                {
                    SetWriterState(FileLogWriterState.Stopped);
                }
            }
        }
    }

    private static void PrepareWriterState(
        ActiveWriterState state,
        string dateText,
        int rotationIndex,
        LogFileNamingSnapshot naming,
        bool allowExistingExplicitFileRecovery = false)
    {
        state.Stream = null;
        state.DateText = dateText;
        state.LogIdentity = CreateLogFileIdentity(naming);
        state.Path = string.Empty;
        state.SizeBytes = 0;
        state.RotationIndex = rotationIndex;
        state.Naming = naming;
        state.AllowExistingExplicitFileRecovery = allowExistingExplicitFileRecovery;
        state.PartialFraming = PartialFileFramingState.Closed;
    }

    private async Task<PooledBatchBuffer> FlushPendingBatchWithRecoveryAsync(
        ActiveWriterState state,
        PooledBatchBuffer pendingBatch,
        AcceptedLineTracker acceptedLineTracker,
        CancellationToken cancellationToken)
    {
        if (!pendingBatch.HasContent)
        {
            return pendingBatch;
        }

        RecoveryBudget? recoveryBudget = state.Stream is null
            ? new RecoveryBudget(_maximumRecoveryAttempts, _recoveryTimeBudget, _timeProvider)
            : null;
        long? initialAttemptStartedTimestamp = recoveryBudget is null
            ? Stopwatch.GetTimestamp()
            : null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.Stream is null)
            {
                recoveryBudget ??= new RecoveryBudget(
                    _maximumRecoveryAttempts,
                    _recoveryTimeBudget,
                    _timeProvider);
                await OpenRecoveryWriterUntilAvailableAsync(state, recoveryBudget, cancellationToken);
            }

            try
            {
                if (pendingBatch.Length > 0)
                {
                    await RunIoWithTimeoutAsync(
                        token => state.Stream!.WriteAsync(pendingBatch.Memory, token).AsTask(),
                        "write",
                        cancellationToken,
                        pendingBatch,
                        recoveryBudget,
                        initialAttemptStartedTimestamp);
                    await RunIoWithTimeoutAsync(
                        token => state.Stream!.FlushAsync(token),
                        "flush",
                        cancellationToken,
                        recoveryBudget: recoveryBudget,
                        initialAttemptStartedTimestamp: initialAttemptStartedTimestamp);
                }

                recoveryBudget?.ThrowIfExpired("committing the recovered batch");

                if (acceptedLineTracker.Commit(pendingBatch.LineCount))
                {
                    state.SizeBytes += pendingBatch.Length;
                    Interlocked.Add(ref _writtenLineCount, pendingBatch.LineCount);
                    Interlocked.Add(ref _writtenByteCount, pendingBatch.Length);
                }

                state.PartialFraming = pendingBatch.EndingPartialFraming;

                if (pendingBatch.TryResetForReuse())
                {
                    return pendingBatch;
                }

                pendingBatch.Dispose();
                return new PooledBatchBuffer(_bufferPool, InitialBatchBufferSize);
            }
            catch (Exception ex) when (IsRecoverableIoFailure(ex, cancellationToken))
            {
                recoveryBudget ??= new RecoveryBudget(
                    _maximumRecoveryAttempts,
                    _recoveryTimeBudget,
                    _timeProvider);
                initialAttemptStartedTimestamp = null;
                if (ex is FileIoTimeoutException)
                {
                    Interlocked.Increment(ref _writeTimeoutCount);
                }

                Interlocked.Increment(ref _recoveryCount);
                ReportFileError(
                    $"File {GetIoAction(ex)} stalled or failed for '{state.Path}'. " +
                    $"Opening a recovery segment and retrying {pendingBatch.LineCount:N0} uncommitted line(s): {ex.Message}");
                RetireFailedStream(state, ex);
                recoveryBudget.RecordFailure(ex, "write/flush");
                state.RotationIndex++;
                state.SizeBytes = 0;
                if (!pendingBatch.TryReencode(PartialFileFramingState.Closed))
                {
                    var replacement = pendingBatch.CloneReencoded(PartialFileFramingState.Closed);
                    pendingBatch.Dispose();
                    pendingBatch = replacement;
                }
            }
        }
    }

    private async Task OpenRecoveryWriterUntilAvailableAsync(
        ActiveWriterState state,
        RecoveryBudget recoveryBudget,
        CancellationToken cancellationToken)
    {
        var failedAttempts = 0;
        while (state.Stream is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            recoveryBudget.ThrowIfExpired("opening a recovery segment");
            try
            {
                await OpenWriterAsync(
                    state,
                    state.DateText,
                    state.RotationIndex,
                    state.Naming,
                    state.AllowExistingExplicitFileRecovery,
                    cancellationToken,
                    recoveryBudget);
            }
            catch (Exception ex) when (IsRecoverableOpenFailure(ex, cancellationToken))
            {
                failedAttempts++;
                Interlocked.Increment(ref _recoveryCount);
                ReportFileError(
                    $"Could not open file recovery segment (attempt {failedAttempts:N0}). " +
                    $"Queued lines remain in memory and will be retried: {ex.Message}");
                recoveryBudget.RecordFailure(ex, "open");
                await Task.Delay(
                    recoveryBudget.ClampDelay(_recoveryRetryInterval),
                    cancellationToken);
                recoveryBudget.ThrowIfExpired("waiting to retry file open");
            }
        }
    }

    private async Task OpenWriterAsync(
        ActiveWriterState state,
        string dateText,
        int rotationIndex,
        LogFileNamingSnapshot naming,
        bool allowExistingExplicitFileRecovery,
        CancellationToken cancellationToken,
        RecoveryBudget recoveryBudget)
    {
        recoveryBudget.ThrowIfExpired("starting file open");
        if (DetachedCleanupCount >= MaximumOutstandingCleanupOperationCount)
        {
            throw CreateStreamCleanupLimitException("starting a new file open");
        }

        var ownershipToken = Guid.NewGuid();
        var openTask = Task.Run(
            () => CreateNewWriter(
                dateText,
                rotationIndex,
                naming,
                ownershipToken,
                allowExistingExplicitFileRecovery),
            CancellationToken.None);
        OpenedWriter openedWriter;
        var effectiveTimeout = recoveryBudget.ClampIoTimeout(_ioTimeout, "waiting for file open");
        try
        {
            openedWriter = await openTask.WaitAsync(effectiveTimeout, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            Interlocked.Increment(ref _writeTimeoutCount);
            var cleanupResult = TryScheduleLateOpenCleanup(openTask);
            if (cleanupResult != CleanupScheduleResult.Scheduled)
            {
                if (cleanupResult == CleanupScheduleResult.Rejected)
                {
                    ObserveDetachedTask(openTask);
                }

                throw CreateStreamCleanupLimitException("an isolated file open timed out");
            }

            AdvancePastAbandonedExplicitCreateNew(state, naming, rotationIndex);
            throw new FileOpenTimeoutException(effectiveTimeout, openTask, ex);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            var cleanupResult = TryScheduleLateOpenCleanup(openTask);
            if (cleanupResult != CleanupScheduleResult.Scheduled)
            {
                if (cleanupResult == CleanupScheduleResult.Rejected)
                {
                    ObserveDetachedTask(openTask);
                }

                throw CreateStreamCleanupLimitException("canceling an isolated file open");
            }

            throw new FileOpenOperationCanceledException(openTask, cancellationToken, ex);
        }

        try
        {
            recoveryBudget.ThrowIfExpired("accepting a completed file open");
        }
        catch
        {
            var cleanupResult = TryScheduleLateOpenCleanup(openTask);
            if (cleanupResult != CleanupScheduleResult.Scheduled)
            {
                if (cleanupResult == CleanupScheduleResult.Rejected)
                {
                    ObserveDetachedTask(openTask);
                }

                throw CreateStreamCleanupLimitException("retiring a file open completed after its deadline");
            }

            AdvancePastAbandonedExplicitCreateNew(state, naming, rotationIndex);
            throw;
        }

        state.Stream = openedWriter.Stream;
        state.Path = openedWriter.Path;
        state.RotationIndex = openedWriter.RotationIndex;
        state.AllowExistingExplicitFileRecovery = false;
        SetCurrentLogFilePath(openedWriter.Path);
    }

    private static void AdvancePastAbandonedExplicitCreateNew(
        ActiveWriterState state,
        LogFileNamingSnapshot naming,
        int attemptedRotationIndex)
    {
        if (attemptedRotationIndex != 0 || string.IsNullOrWhiteSpace(naming.LogFileName))
        {
            return;
        }

        // The isolated CreateNew may already own the exact explicit path even though its
        // factory call has not returned. Keep that late result under bounded cleanup and
        // move this recovery incident to the first numbered segment. An explicit file
        // that existed before the attempt never reaches this transition and remains a
        // deterministic collision.
        state.RotationIndex = Math.Max(state.RotationIndex, 1);
    }

    private async Task RunIoWithTimeoutAsync(
        Func<CancellationToken, Task> operation,
        string action,
        CancellationToken cancellationToken,
        PooledBatchBuffer? referencedBatch = null,
        RecoveryBudget? recoveryBudget = null,
        long? initialAttemptStartedTimestamp = null)
    {
        recoveryBudget?.ThrowIfExpired($"starting file {action}");
        if (DetachedCleanupCount >= MaximumOutstandingCleanupOperationCount)
        {
            throw CreateStreamCleanupLimitException($"starting a new {action}");
        }

        var effectiveTimeout = recoveryBudget?.ClampIoTimeout(
            _ioTimeout,
            $"waiting for file {action}") ?? _ioTimeout;
        if (initialAttemptStartedTimestamp.HasValue)
        {
            var remaining = _ioTimeout - Stopwatch.GetElapsedTime(initialAttemptStartedTimestamp.Value);
            if (remaining <= TimeSpan.Zero)
            {
                throw new FileIoTimeoutException(
                    action,
                    _ioTimeout,
                    Task.CompletedTask,
                    new TimeoutException("The initial batch I/O deadline expired."));
            }

            effectiveTimeout = effectiveTimeout <= remaining ? effectiveTimeout : remaining;
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        referencedBatch?.AddOperationReference();
        Task operationTask;
        try
        {
            operationTask = Task.Run(
                () => operation(timeoutCancellation.Token),
                CancellationToken.None);
        }
        catch
        {
            referencedBatch?.ReleaseOperationReference();
            throw;
        }

        if (referencedBatch is not null)
        {
            _ = operationTask.ContinueWith(
                static (_, state) => ((PooledBatchBuffer)state!).ReleaseOperationReference(),
                referencedBatch,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        try
        {
            await operationTask.WaitAsync(effectiveTimeout, cancellationToken);
            recoveryBudget?.ThrowIfExpired($"completing file {action}");
            if (initialAttemptStartedTimestamp.HasValue &&
                Stopwatch.GetElapsedTime(initialAttemptStartedTimestamp.Value) >= _ioTimeout)
            {
                throw new FileIoTimeoutException(
                    action,
                    _ioTimeout,
                    operationTask,
                    new TimeoutException("The initial batch I/O deadline expired."));
            }
        }
        catch (TimeoutException ex)
        {
            timeoutCancellation.Cancel();
            throw new FileIoTimeoutException(action, effectiveTimeout, operationTask, ex);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            timeoutCancellation.Cancel();
            throw new FileIoOperationCanceledException(
                action,
                operationTask,
                cancellationToken,
                ex);
        }
        catch (OperationCanceledException ex)
            when (!cancellationToken.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
        {
            throw new FileIoTimeoutException(action, effectiveTimeout, operationTask, ex);
        }
    }

    private async Task CloseActiveStreamAsync(ActiveWriterState state, CancellationToken cancellationToken)
    {
        var stream = state.Stream;
        state.Stream = null;
        state.PartialFraming = PartialFileFramingState.Closed;
        if (stream is null)
        {
            return;
        }

        Task disposeTask;
        try
        {
            if (DetachedCleanupCount >= MaximumOutstandingCleanupOperationCount)
            {
                throw CreateStreamCleanupLimitException("starting an active close");
            }

            disposeTask = Task.Run(
                () => stream.DisposeAsync().AsTask(),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            ReportFileError($"File close could not start for '{state.Path}': {ex.Message}");
            throw new IOException($"File close could not start for '{state.Path}'.", ex);
        }

        try
        {
            await disposeTask.WaitAsync(_ioTimeout, cancellationToken);
            Volatile.Write(ref _consecutiveCloseFailureCount, 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cleanupResult = TryScheduleStreamCleanup(
                stream,
                disposeTask,
                disposeAfterPendingOperation: false);
            if (cleanupResult != CleanupScheduleResult.Scheduled)
            {
                if (cleanupResult == CleanupScheduleResult.Rejected)
                {
                    ObserveDetachedTask(disposeTask);
                }

                throw CreateStreamCleanupLimitException("canceling an active close");
            }

        }
        catch (TimeoutException)
        {
            Interlocked.Increment(ref _writeTimeoutCount);
            ReportFileError(
                $"File close made no progress for {_ioTimeout.TotalSeconds:0.###} seconds at '{state.Path}'.");
            var cleanupResult = TryScheduleStreamCleanup(
                stream,
                disposeTask,
                disposeAfterPendingOperation: false);
            if (cleanupResult != CleanupScheduleResult.Scheduled)
            {
                if (cleanupResult == CleanupScheduleResult.Rejected)
                {
                    ObserveDetachedTask(disposeTask);
                }

                throw CreateStreamCleanupLimitException("an active close timed out");
            }

        }
        catch (Exception ex)
        {
            // Awaiting the original dispose task observes its exception. Never call DisposeAsync twice.
            ReportFileError($"File close failed for '{state.Path}': {ex.Message}");
            RecordCloseFailureOrThrow($"File close failed for '{state.Path}'.", ex);
        }
    }

    private void RecordCloseFailureOrThrow(
        string message,
        Exception? innerException = null)
    {
        var failureCount = Interlocked.Increment(ref _consecutiveCloseFailureCount);
        if (failureCount < MaximumConsecutiveCloseFailureCount)
        {
            return;
        }

        throw new CloseFailureLimitException(
            $"{message} The writer reached its limit of " +
            $"{MaximumConsecutiveCloseFailureCount} consecutive close failures.",
            innerException);
    }

    private static StreamCleanupLimitException CreateStreamCleanupLimitException(string context) =>
        new(
            $"File stream cleanup limit reached while {context}. " +
            $"The session is stopping with at most {MaximumOutstandingCleanupOperationCount} unsettled cleanup operations.");

    private void RetireFailedStream(ActiveWriterState state, Exception failure)
    {
        var stream = state.Stream;
        state.Stream = null;
        state.PartialFraming = PartialFileFramingState.Closed;
        if (stream is null)
        {
            return;
        }

        var pendingOperation = failure switch
        {
            FileIoTimeoutException timeout => timeout.PendingOperation,
            FileIoOperationCanceledException canceled => canceled.PendingOperation,
            _ => Task.CompletedTask
        };
        var cleanupResult = TryScheduleStreamCleanup(
            stream,
            pendingOperation,
            disposeAfterPendingOperation: true);
        if (cleanupResult != CleanupScheduleResult.Scheduled)
        {
            if (cleanupResult == CleanupScheduleResult.Rejected)
            {
                ObserveDetachedTask(pendingOperation);
            }

            throw CreateStreamCleanupLimitException(
                $"retiring a failed stream after {failure.GetType().Name}");
        }
    }

    private CleanupScheduleResult TryScheduleStreamCleanup(
        Stream stream,
        Task pendingOperation,
        bool disposeAfterPendingOperation) =>
        TryScheduleCleanup(
            () => CleanupStreamAsync(stream, pendingOperation, disposeAfterPendingOperation));

    private CleanupScheduleResult TryScheduleLateOpenCleanup(Task<OpenedWriter> openTask) =>
        TryScheduleCleanup(() => CleanupLateOpenAsync(openTask));

    private CleanupScheduleResult TryScheduleCleanup(Func<Task> cleanup)
    {
        Task cleanupTask;
        bool scheduledAsFinal;
        var startCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_cleanupGate)
        {
            _detachedCleanupTasks.RemoveWhere(static task => task.IsCompleted);
            if (_detachedCleanupTasks.Count >= MaximumOutstandingCleanupOperationCount)
            {
                return CleanupScheduleResult.Rejected;
            }

            cleanupTask = Task.Run(async () =>
            {
                await startCleanup.Task.ConfigureAwait(false);
                await cleanup().ConfigureAwait(false);
            }, CancellationToken.None);
            _detachedCleanupTasks.Add(cleanupTask);
            scheduledAsFinal = _detachedCleanupTasks.Count == MaximumOutstandingCleanupOperationCount;
        }

        startCleanup.TrySetResult();

        _ = cleanupTask.ContinueWith(
            completedTask =>
            {
                _ = completedTask.Exception;
                lock (_cleanupGate)
                {
                    _detachedCleanupTasks.Remove(completedTask);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return scheduledAsFinal
            ? CleanupScheduleResult.ScheduledFinal
            : CleanupScheduleResult.Scheduled;
    }

    private async Task CleanupLateOpenAsync(Task<OpenedWriter> openTask)
    {
        OpenedWriter openedWriter;
        try
        {
            openedWriter = await openTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportFileError($"Retired file open completed with an error: {ex.Message}");
            return;
        }

        byte[]? ownershipMarker = null;
        if (openedWriter.AbandonedOwnership is { } ownership)
        {
            ownershipMarker = TryMarkAbandonedEmptyFile(openedWriter.Stream, openedWriter.Path, ownership);
        }

        await DisposeRetiredStreamAsync(openedWriter.Stream).ConfigureAwait(false);
        if (ownershipMarker is not null)
        {
            TryDeleteMarkedAbandonedFile(openedWriter.Path, ownershipMarker);
        }
    }

    private async Task CleanupStreamAsync(
        Stream stream,
        Task pendingOperation,
        bool disposeAfterPendingOperation)
    {
        try
        {
            await pendingOperation.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportFileError($"Retired file stream operation completed with an error: {ex.Message}");
        }

        if (!disposeAfterPendingOperation)
        {
            return;
        }

        await DisposeRetiredStreamAsync(stream).ConfigureAwait(false);
    }

    private async Task DisposeRetiredStreamAsync(Stream stream)
    {
        try
        {
            await Task.Run(
                    () => stream.DisposeAsync().AsTask(),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportFileError($"Retired file stream cleanup failed: {ex.Message}");
        }
    }

    private byte[]? TryMarkAbandonedEmptyFile(
        Stream stream,
        string path,
        AbandonedFileOwnership ownership)
    {
        try
        {
            if (!stream.CanSeek || !stream.CanWrite || stream.Length != 0 ||
                !File.Exists(path) || new FileInfo(path).Length != 0)
            {
                return null;
            }

            var marker = Encoding.ASCII.GetBytes(
                $"SerialMonitor abandoned CreateNew ownership {ownership.Token:N}");
            stream.Position = 0;
            stream.Write(marker);
            stream.Flush();
            return stream.Length == marker.Length ? marker : null;
        }
        catch (Exception ex)
        {
            ReportFileError(
                $"Could not mark abandoned explicit-name file '{path}' for owned cleanup: {ex.Message}");
            return null;
        }
    }

    private void TryDeleteMarkedAbandonedFile(string path, byte[] ownershipMarker)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(path))
            {
                return;
            }

            using var verificationHandle = CreateFile(
                path,
                GenericRead | DeleteAccess,
                FileShareRead | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileAttributeNormal,
                IntPtr.Zero);
            if (verificationHandle.IsInvalid)
            {
                throw new IOException(
                    $"Could not open owned-file cleanup handle; Windows error {Marshal.GetLastPInvokeError()}.");
            }

            if (RandomAccess.GetLength(verificationHandle) != ownershipMarker.Length)
            {
                return;
            }

            var actualMarker = new byte[ownershipMarker.Length];
            if (RandomAccess.Read(verificationHandle, actualMarker, fileOffset: 0) != ownershipMarker.Length)
            {
                return;
            }

            if (!actualMarker.AsSpan().SequenceEqual(ownershipMarker))
            {
                return;
            }

            var disposition = new FileDispositionInfo { DeleteFile = true };
            if (!SetFileInformationByHandle(
                    verificationHandle,
                    FileInfoByHandleClass.FileDispositionInfo,
                    ref disposition,
                    (uint)Marshal.SizeOf<FileDispositionInfo>()))
            {
                throw new IOException(
                    $"Windows refused owned-file deletion with error {Marshal.GetLastPInvokeError()}.");
            }
        }
        catch (Exception ex)
        {
            ReportFileError(
                $"Could not remove abandoned explicit-name file '{path}': {ex.Message}");
        }
    }

    private void RecordOutstandingAcceptedLinesAsDropped(AcceptedLineTracker? acceptedLineTracker, string reason)
    {
        var abandoned = acceptedLineTracker?.Abandon() ?? 0;
        if (abandoned <= 0)
        {
            return;
        }

        Interlocked.Add(ref _droppedLineCount, abandoned);
        ReportFileError($"{reason}: {abandoned:N0} accepted line(s) were not saved.");
    }

    private static bool IsRecoverableIoFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is not PathTooLongException and
        not DeterministicFileOpenException and
        not RecoveryBudgetExceededException and
        not StreamCleanupLimitException &&
        exception is IOException or UnauthorizedAccessException;

    private static bool IsRecoverableOpenFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is not PathTooLongException and
        not DeterministicFileOpenException and
        not RecoveryBudgetExceededException and
        not StreamCleanupLimitException &&
        exception is IOException or UnauthorizedAccessException;

    private static string GetIoAction(Exception exception) => exception switch
    {
        FileIoTimeoutException timeout => timeout.Action,
        FileOpenTimeoutException => "open",
        _ => "I/O"
    };

    private OpenedWriter CreateNewWriter(
        string dateText,
        int rotationIndex,
        LogFileNamingSnapshot naming,
        Guid ownershipToken,
        bool allowExistingExplicitFileRecovery)
    {
        if (File.Exists(_directory))
        {
            throw new DeterministicFileOpenException(
                $"The configured log directory is occupied by a file: {_directory}");
        }

        Directory.CreateDirectory(_directory);
        if (!string.IsNullOrWhiteSpace(naming.LogFileName))
        {
            if (rotationIndex == 0)
            {
                var explicitPath = CreateLogFilePath(dateText, rotationIndex, duplicateIndex: 0, naming);
                EnsureCandidateIsNotDirectory(explicitPath);
                if (File.Exists(explicitPath))
                {
                    if (!allowExistingExplicitFileRecovery)
                    {
                        throw new DeterministicFileOpenException($"Log file already exists: {explicitPath}");
                    }

                    rotationIndex = 1;
                }
                else
                {
                    return CreateOpenedWriter(
                        explicitPath,
                        ownershipToken,
                        canDeleteIfAbandoned: _deleteAbandonedExplicitFiles,
                        rotationIndex);
                }
            }

            for (var duplicateIndex = 0; duplicateIndex < 10_000; duplicateIndex++)
            {
                var path = CreateLogFilePath(dateText, rotationIndex, duplicateIndex, naming);
                EnsureCandidateIsNotDirectory(path);
                try
                {
                    return CreateOpenedWriter(
                        path,
                        ownershipToken,
                        canDeleteIfAbandoned: false,
                        rotationIndex);
                }
                catch (IOException) when (File.Exists(path))
                {
                }
            }

            throw new DeterministicFileOpenException(
                "Could not create a unique rotated serial log file after checking 10,000 names.");
        }

        for (var duplicateIndex = 0; duplicateIndex < 10_000; duplicateIndex++)
        {
            var path = CreateLogFilePath(dateText, rotationIndex, duplicateIndex, naming);
            EnsureCandidateIsNotDirectory(path);
            try
            {
                return CreateOpenedWriter(
                    path,
                    ownershipToken,
                    canDeleteIfAbandoned: false,
                    rotationIndex);
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }

        throw new DeterministicFileOpenException(
            "Could not create a unique timestamped serial log file after checking 10,000 names.");
    }

    private OpenedWriter CreateOpenedWriter(
        string path,
        Guid ownershipToken,
        bool canDeleteIfAbandoned,
        int rotationIndex)
    {
        var stream = CreateWriter(path, FileMode.CreateNew);
        return new OpenedWriter(
            stream,
            path,
            canDeleteIfAbandoned ? new AbandonedFileOwnership(ownershipToken) : null,
            rotationIndex);
    }

    private static void EnsureCandidateIsNotDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            throw new DeterministicFileOpenException(
                $"The serial log file path is occupied by a directory: {path}");
        }
    }

    private string CreateLogFilePath(
        string dateText,
        int rotationIndex,
        int duplicateIndex,
        LogFileNamingSnapshot naming)
    {
        if (!string.IsNullOrWhiteSpace(naming.LogFileName))
        {
            if (rotationIndex == 0)
            {
                return Path.Combine(_directory, naming.LogFileName);
            }

            var extension = Path.GetExtension(naming.LogFileName);
            var stem = Path.GetFileNameWithoutExtension(naming.LogFileName);
            var explicitDuplicatePart = duplicateIndex == 0 ? string.Empty : $"_dup{duplicateIndex:D3}";
            return Path.Combine(_directory, $"{stem}_{rotationIndex:D3}{explicitDuplicatePart}{extension}");
        }

        string runTimeText;
        lock (_stateGate)
        {
            runTimeText = _logRunTimeText;
        }

        var rotationPart = rotationIndex == 0 ? string.Empty : $"_{rotationIndex:D3}";
        var duplicatePart = duplicateIndex == 0 ? string.Empty : $"_dup{duplicateIndex:D3}";
        var fileName = $"{dateText}_{runTimeText}_serial{rotationPart}{duplicatePart}.log";
        return Path.Combine(_directory, fileName);
    }

    private static string CreateLogFileIdentity(LogFileNamingSnapshot naming)
    {
        return string.IsNullOrWhiteSpace(naming.LogFileName)
            ? "automatic"
            : $"explicit|{naming.LogFileName}";
    }

    private LogFileNamingSnapshot GetLogFileNamingSnapshot()
    {
        lock (_stateGate)
        {
            return new LogFileNamingSnapshot(
                _logFileName);
        }
    }

    private bool ConsumeRotationRequest()
    {
        lock (_stateGate)
        {
            if (!_rotationRequested)
            {
                return false;
            }

            _rotationRequested = false;
            return true;
        }
    }

    private void ApplyLogFileNamingState(LogFileNamingSnapshot naming)
    {
        lock (_stateGate)
        {
            _logFileName = naming.LogFileName;
        }
    }

    private Stream CreateWriter(string path, FileMode fileMode) => _streamFactory(path, fileMode);

    private static Stream CreateFileStream(string path, FileMode fileMode)
    {
        return new FileStream(
            path,
            fileMode,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private void SetWriterState(
        FileLogWriterState state,
        bool clearLastError = false,
        bool clearLastFault = false)
    {
        lock (_stateGate)
        {
            _writerState = state;
            if (clearLastError)
            {
                _lastFileError = null;
            }

            if (clearLastFault)
            {
                _lastFault = null;
            }

            _lastLifecycleAction = state switch
            {
                FileLogWriterState.Starting => "File logging starting.",
                FileLogWriterState.Running => "File logging running.",
                FileLogWriterState.Stopping => "File logging stopping.",
                FileLogWriterState.Faulted => _lastLifecycleAction,
                _ => _lastLifecycleAction
            };
        }

        RaiseStatusChanged();
    }

    private void RecordWriterFault(
        Exception exception,
        FileLogWriterFaultCategory? categoryOverride = null)
    {
        var category = categoryOverride ?? exception switch
        {
            DeterministicFileOpenException or PathTooLongException or ArgumentException =>
                FileLogWriterFaultCategory.DeterministicConfiguration,
            StreamCleanupLimitException => FileLogWriterFaultCategory.CleanupLimit,
            CloseFailureLimitException => FileLogWriterFaultCategory.CloseFailureLimit,
            RecoveryBudgetExceededException or UnauthorizedAccessException or IOException =>
                FileLogWriterFaultCategory.RetryableIo,
            _ => FileLogWriterFaultCategory.Unexpected
        };
        var canAutoRestart = category == FileLogWriterFaultCategory.RetryableIo;
        lock (_stateGate)
        {
            _writerState = FileLogWriterState.Faulted;
            _lastFault = new FileLogWriterFaultInfo(
                category,
                exception.Message,
                exception.GetType().Name,
                DateTimeOffset.UtcNow,
                canAutoRestart);
            _lastLifecycleAction = canAutoRestart
                ? $"File logging faulted and may be restarted: {exception.Message}"
                : $"File logging faulted and requires user action: {exception.Message}";
        }

        RaiseStatusChanged();
    }

    private void SetCurrentLogFilePath(string? path)
    {
        lock (_stateGate)
        {
            _currentLogFilePath = path;
            if (!string.IsNullOrWhiteSpace(path))
            {
                _lastLogFilePath = path;
            }
        }

        RaiseStatusChanged();
    }

    private void SetLifecycleAction(string message, bool raiseStatusChanged = true)
    {
        lock (_stateGate)
        {
            _lastLifecycleAction = message;
        }

        if (raiseStatusChanged)
        {
            RaiseStatusChanged();
        }
    }

    private void RecordLifecycleError(string message)
    {
        Interlocked.Increment(ref _lifecycleErrorCount);
        lock (_stateGate)
        {
            _lastLifecycleAction = message;
        }
    }

    private void ReportFileError(string message)
    {
        Interlocked.Increment(ref _fileErrorCount);
        lock (_stateGate)
        {
            _lastFileError = message;
        }

        SafeRaiseError(message);
        RaiseStatusChanged();
    }

    private void SafeRaiseError(string message)
    {
        try
        {
            Error?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            RecordLifecycleError($"FileLogWriter Error subscriber failed: {ex.Message}");
        }
    }

    private void RaiseStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            RecordLifecycleError($"FileLogWriter StatusChanged subscriber failed: {ex.Message}");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static string CreateDefaultLogDirectory()
    {
        return Path.Combine(AppContext.BaseDirectory, "logs");
    }

    private static Channel<FileLogWriteRequest> CreateQueue(int capacity)
    {
        return Channel.CreateBounded<FileLogWriteRequest>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    private static TimeSpan EnsurePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The interval must be positive.");
        }

        return value;
    }

    private static void ObserveDetachedTask(Task task)
    {
        _ = task.ContinueWith(
            completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private sealed class ActiveWriterState
    {
        public Stream? Stream { get; set; }

        public string DateText { get; set; } = string.Empty;

        public string LogIdentity { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public long SizeBytes { get; set; }

        public int RotationIndex { get; set; }

        public LogFileNamingSnapshot Naming { get; set; }

        public bool AllowExistingExplicitFileRecovery { get; set; }

        public PartialFileFramingState PartialFraming { get; set; } = PartialFileFramingState.Closed;

        public void Reset()
        {
            Stream = null;
            DateText = string.Empty;
            LogIdentity = string.Empty;
            Path = string.Empty;
            SizeBytes = 0;
            RotationIndex = 0;
            Naming = default;
            AllowExistingExplicitFileRecovery = false;
            PartialFraming = PartialFileFramingState.Closed;
        }
    }

    private readonly record struct PartialFileFramingState(bool IsOpen, LogRuleMatchMode ContentMode)
    {
        public static PartialFileFramingState Closed { get; } = new(false, LogRuleMatchMode.Terminal);
    }

    private enum FileInfoByHandleClass
    {
        FileDispositionInfo = 4
    }

    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        FileInfoByHandleClass fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    private sealed class PooledBatchBuffer : IDisposable
    {
        private readonly ArrayPool<byte> _pool;
        private readonly List<LogLine> _lines = new();
        private byte[]? _buffer;
        private int _referenceCount = 1;
        private int _ownerReleased;
        private bool _appendPartialBoundary;

        public PooledBatchBuffer(ArrayPool<byte> pool, int initialCapacity)
        {
            _pool = pool;
            _buffer = pool.Rent(initialCapacity);
        }

        public int Length { get; private set; }

        public int LineCount => _lines.Count;

        public bool HasContent => Length > 0 || LineCount > 0;

        public PartialFileFramingState EndingPartialFraming { get; private set; } =
            PartialFileFramingState.Closed;

        public ReadOnlyMemory<byte> Memory => new(GetBuffer(), 0, Length);

        public void AppendLine(LogLine line, PartialFileFramingState startingPartialFraming)
        {
            ArgumentNullException.ThrowIfNull(line);
            if (Volatile.Read(ref _referenceCount) != 1 || Volatile.Read(ref _ownerReleased) != 0)
            {
                throw new InvalidOperationException("Cannot mutate a pooled batch while an I/O operation owns it.");
            }

            if (_lines.Count == 0 && Length == 0)
            {
                EndingPartialFraming = startingPartialFraming;
            }

            _lines.Add(line);
            EncodeLine(line);
        }

        public void AppendPartialBoundary(PartialFileFramingState startingPartialFraming)
        {
            if (Volatile.Read(ref _referenceCount) != 1 || Volatile.Read(ref _ownerReleased) != 0)
            {
                throw new InvalidOperationException("Cannot mutate a pooled batch while an I/O operation owns it.");
            }

            if (_lines.Count == 0 && Length == 0)
            {
                EndingPartialFraming = startingPartialFraming;
            }

            _appendPartialBoundary = true;
            if (EndingPartialFraming.IsOpen)
            {
                AppendBytes(LineEndingBytes);
                EndingPartialFraming = PartialFileFramingState.Closed;
            }
        }

        public bool TryReencode(PartialFileFramingState startingPartialFraming)
        {
            if (Volatile.Read(ref _ownerReleased) != 0 || Volatile.Read(ref _referenceCount) != 1)
            {
                return false;
            }

            Reencode(startingPartialFraming);
            return true;
        }

        public PooledBatchBuffer CloneReencoded(PartialFileFramingState startingPartialFraming)
        {
            var replacement = new PooledBatchBuffer(_pool, Math.Max(InitialBatchBufferSize, Length));
            replacement._lines.AddRange(_lines);
            replacement._appendPartialBoundary = _appendPartialBoundary;
            replacement.Reencode(startingPartialFraming);
            return replacement;
        }

        public void AddOperationReference()
        {
            while (true)
            {
                var current = Volatile.Read(ref _referenceCount);
                if (current <= 0)
                {
                    throw new ObjectDisposedException(nameof(PooledBatchBuffer));
                }

                if (Interlocked.CompareExchange(ref _referenceCount, current + 1, current) == current)
                {
                    return;
                }
            }
        }

        public void ReleaseOperationReference() => ReleaseReference();

        public bool TryResetForReuse()
        {
            if (Volatile.Read(ref _ownerReleased) != 0 || Volatile.Read(ref _referenceCount) != 1)
            {
                return false;
            }

            Length = 0;
            _lines.Clear();
            _appendPartialBoundary = false;
            EndingPartialFraming = PartialFileFramingState.Closed;
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ownerReleased, 1) == 0)
            {
                ReleaseReference();
            }
        }

        private void EnsureCapacity(int requiredLength)
        {
            var current = GetBuffer();
            if (requiredLength <= current.Length)
            {
                return;
            }

            var doubledLength = current.Length <= Array.MaxLength / 2
                ? current.Length * 2
                : Array.MaxLength;
            var requestedLength = Math.Max(requiredLength, doubledLength);
            if (requestedLength > Array.MaxLength)
            {
                throw new IOException($"A file-log write batch is too large: {requiredLength:N0} bytes.");
            }

            var replacement = _pool.Rent(requestedLength);
            current.AsSpan(0, Length).CopyTo(replacement);
            _buffer = replacement;
            _pool.Return(current);
        }

        private void Reencode(PartialFileFramingState startingPartialFraming)
        {
            Length = 0;
            EndingPartialFraming = startingPartialFraming;
            foreach (var line in _lines)
            {
                EncodeLine(line);
            }

            if (_appendPartialBoundary && EndingPartialFraming.IsOpen)
            {
                AppendBytes(LineEndingBytes);
                EndingPartialFraming = PartialFileFramingState.Closed;
            }
        }

        private void EncodeLine(LogLine line)
        {
            if (line.IsPartialRxTerminator)
            {
                if (EndingPartialFraming.IsOpen)
                {
                    AppendBytes(LineEndingBytes);
                    EndingPartialFraming = PartialFileFramingState.Closed;
                }

                return;
            }

            if (line.IsPartialRxSegment && line.Direction == LogDirection.Rx)
            {
                if (!EndingPartialFraming.IsOpen)
                {
                    AppendUtf8(line.Formatted);
                    EndingPartialFraming = new PartialFileFramingState(true, line.ContentMode);
                    return;
                }

                if (EndingPartialFraming.ContentMode == LogRuleMatchMode.Hex &&
                    line.DisplayText.Length > 0)
                {
                    AppendUtf8(" ");
                }

                AppendUtf8(line.DisplayText);
                return;
            }

            if (EndingPartialFraming.IsOpen)
            {
                AppendBytes(LineEndingBytes);
            }

            AppendUtf8WithLineEnding(line.Formatted);
            EndingPartialFraming = PartialFileFramingState.Closed;
        }

        private void AppendUtf8(string text)
        {
            var encodedLength = Encoding.UTF8.GetByteCount(text);
            var requiredLength = checked(Length + encodedLength);
            EnsureCapacity(requiredLength);
            Length += Encoding.UTF8.GetBytes(
                text.AsSpan(),
                GetBuffer().AsSpan(Length, encodedLength));
        }

        private void AppendUtf8WithLineEnding(string text)
        {
            var encodedLength = Encoding.UTF8.GetByteCount(text);
            var requiredLength = checked(Length + encodedLength + LineEndingBytes.Length);
            EnsureCapacity(requiredLength);
            var buffer = GetBuffer();
            Length += Encoding.UTF8.GetBytes(
                text.AsSpan(),
                buffer.AsSpan(Length, encodedLength));
            LineEndingBytes.CopyTo(buffer, Length);
            Length += LineEndingBytes.Length;
        }

        private void AppendBytes(ReadOnlySpan<byte> bytes)
        {
            var requiredLength = checked(Length + bytes.Length);
            EnsureCapacity(requiredLength);
            bytes.CopyTo(GetBuffer().AsSpan(Length, bytes.Length));
            Length += bytes.Length;
        }

        private byte[] GetBuffer() =>
            Volatile.Read(ref _buffer) ?? throw new ObjectDisposedException(nameof(PooledBatchBuffer));

        private void ReleaseReference()
        {
            if (Interlocked.Decrement(ref _referenceCount) != 0)
            {
                return;
            }

            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null)
            {
                _pool.Return(buffer);
            }
        }
    }

    private sealed class RecoveryBudget
    {
        private readonly int _maximumAttempts;
        private readonly TimeSpan _timeBudget;
        private readonly TimeProvider _timeProvider;
        private readonly long _startedTimestamp;
        private int _failureCount;

        public RecoveryBudget(int maximumAttempts, TimeSpan timeBudget, TimeProvider timeProvider)
        {
            _maximumAttempts = maximumAttempts;
            _timeBudget = timeBudget;
            _timeProvider = timeProvider;
            _startedTimestamp = timeProvider.GetTimestamp();
        }

        public TimeSpan Remaining
        {
            get
            {
                var remaining = _timeBudget - _timeProvider.GetElapsedTime(_startedTimestamp);
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        public void RecordFailure(Exception failure, string action)
        {
            _failureCount++;
            if (_failureCount >= _maximumAttempts)
            {
                throw new RecoveryBudgetExceededException(
                    action,
                    _failureCount,
                    _maximumAttempts,
                    _timeBudget,
                    failure);
            }

            ThrowIfExpired(action);
        }

        public TimeSpan ClampDelay(TimeSpan requestedDelay)
        {
            var remaining = GetRemainingOrThrow("delaying file recovery");

            return requestedDelay <= remaining ? requestedDelay : remaining;
        }

        public TimeSpan ClampIoTimeout(TimeSpan ioTimeout, string action)
        {
            var remaining = GetRemainingOrThrow(action);
            return ioTimeout <= remaining ? ioTimeout : remaining;
        }

        public void ThrowIfExpired(string action) => _ = GetRemainingOrThrow(action);

        private TimeSpan GetRemainingOrThrow(string action)
        {
            var remaining = Remaining;
            if (remaining > TimeSpan.Zero)
            {
                return remaining;
            }

            throw new RecoveryBudgetExceededException(
                action,
                _failureCount,
                _maximumAttempts,
                _timeBudget,
                new TimeoutException("The absolute recovery deadline expired."));
        }
    }

    internal sealed class AcceptedLineTracker
    {
        private readonly object _gate = new();
        private long _outstandingLineCount;
        private bool _accepting = true;
        private bool _abandoned;

        public bool TryAccept()
        {
            lock (_gate)
            {
                if (!_accepting || _abandoned)
                {
                    return false;
                }

                _outstandingLineCount++;
                return true;
            }
        }

        public bool RollBackAcceptance()
        {
            lock (_gate)
            {
                if (_abandoned)
                {
                    return false;
                }

                _outstandingLineCount--;
                return true;
            }
        }

        public bool Commit(int lineCount)
        {
            lock (_gate)
            {
                if (_abandoned)
                {
                    return false;
                }

                _outstandingLineCount -= lineCount;
                return true;
            }
        }

        public void StopAccepting()
        {
            lock (_gate)
            {
                _accepting = false;
            }
        }

        public long Abandon()
        {
            lock (_gate)
            {
                _accepting = false;
                if (_abandoned)
                {
                    return 0;
                }

                _abandoned = true;
                var abandoned = _outstandingLineCount;
                _outstandingLineCount = 0;
                return abandoned;
            }
        }
    }

    private sealed class FileIoTimeoutException : IOException
    {
        public FileIoTimeoutException(string action, TimeSpan timeout, Task pendingOperation, Exception innerException)
            : base($"File {action} made no progress for {timeout.TotalSeconds:0.###} seconds.", innerException)
        {
            Action = action;
            PendingOperation = pendingOperation;
        }

        public string Action { get; }

        public Task PendingOperation { get; }
    }

    private sealed class FileIoOperationCanceledException : OperationCanceledException
    {
        public FileIoOperationCanceledException(
            string action,
            Task pendingOperation,
            CancellationToken cancellationToken,
            Exception innerException)
            : base(
                $"File {action} was canceled while the underlying operation was still in flight.",
                innerException,
                cancellationToken)
        {
            Action = action;
            PendingOperation = pendingOperation;
        }

        public string Action { get; }

        public Task PendingOperation { get; }
    }

    private sealed class FileOpenTimeoutException : IOException
    {
        public FileOpenTimeoutException(
            TimeSpan timeout,
            Task<OpenedWriter> pendingOperation,
            Exception innerException)
            : base($"File open made no progress for {timeout.TotalSeconds:0.###} seconds.", innerException)
        {
            PendingOperation = pendingOperation;
        }

        public Task<OpenedWriter> PendingOperation { get; }
    }

    private sealed class FileOpenOperationCanceledException : OperationCanceledException
    {
        public FileOpenOperationCanceledException(
            Task<OpenedWriter> pendingOperation,
            CancellationToken cancellationToken,
            Exception innerException)
            : base(
                "File open was canceled while the isolated operation was still in flight.",
                innerException,
                cancellationToken)
        {
            PendingOperation = pendingOperation;
        }

        public Task<OpenedWriter> PendingOperation { get; }
    }

    private sealed class DeterministicFileOpenException : IOException
    {
        public DeterministicFileOpenException(string message)
            : base(message)
        {
        }
    }

    private sealed class RecoveryBudgetExceededException : IOException
    {
        public RecoveryBudgetExceededException(
            string action,
            int failureCount,
            int maximumAttempts,
            TimeSpan timeBudget,
            Exception innerException)
            : base(
                $"File {action} recovery stopped after {failureCount:N0} failure(s); " +
                $"the limit is {maximumAttempts:N0} attempts within {timeBudget.TotalSeconds:0.###} seconds.",
                innerException)
        {
        }
    }

    private sealed class StreamCleanupLimitException : IOException
    {
        public StreamCleanupLimitException(string message)
            : base(message)
        {
        }
    }

    private sealed class CloseFailureLimitException : IOException
    {
        public CloseFailureLimitException(string message, Exception? innerException)
            : base(message, innerException)
        {
        }
    }

    private sealed class FileWriterStillStoppingException : InvalidOperationException
    {
        public FileWriterStillStoppingException(string message)
            : base(message)
        {
        }
    }

    private enum CleanupScheduleResult
    {
        Scheduled,
        ScheduledFinal,
        Rejected
    }

    private readonly record struct AbandonedFileOwnership(Guid Token);

    private readonly record struct OpenedWriter(
        Stream Stream,
        string Path,
        AbandonedFileOwnership? AbandonedOwnership,
        int RotationIndex);

    private readonly record struct LogFileNamingSnapshot(string LogFileName);

    private readonly record struct FileLogWriteRequest(
        LogLine? Line,
        LogFileNamingSnapshot? Naming,
        DateTimeOffset? OpenedAt,
        TaskCompletionSource<string>? OpenCompletion,
        bool AllowExistingExplicitFileRecovery)
    {
        public static FileLogWriteRequest ForLine(LogLine line) => new(line, null, null, null, false);

        public static FileLogWriteRequest ForNaming(LogFileNamingSnapshot naming) =>
            new(null, naming, null, null, false);

        public static FileLogWriteRequest ForOpen(
            DateTimeOffset openedAt,
            TaskCompletionSource<string> completion,
            bool allowExistingExplicitFileRecovery) =>
            new(null, null, openedAt, completion, allowExistingExplicitFileRecovery);
    }
}
