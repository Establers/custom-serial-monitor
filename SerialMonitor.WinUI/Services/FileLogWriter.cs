using System.Text;
using System.Threading.Channels;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed class FileLogWriter : IFileLogWriter
{
    private const int QueueCapacity = 100_000;
    private const long QueueByteCapacity = 64L * 1024 * 1024;
    private const int FlushLineInterval = 100;
    private const int MaximumRecoveryAttempts = 3;
    private const int MaximumLateOperationCount = 4;
    private static readonly TimeSpan DefaultFileIoTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FlushTimeInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FlushCheckInterval = TimeSpan.FromMilliseconds(100);

    private readonly Func<string, FileMode, Stream> _streamFactory;
    private readonly TimeSpan _fileIoTimeout;
    private readonly TimeSpan _shutdownTimeout;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _queueGate = new();
    private readonly object _lateOperationGate = new();
    private readonly HashSet<StreamWriter> _lateWriters = new();
    private readonly HashSet<Task> _lateOperations = new();
    private Channel<FileLogWriteRequest> _queue = CreateQueue();
    private CancellationTokenSource? _writerCancellation;
    private Task? _writerTask;
    private string _directory = CreateDefaultLogDirectory();
    private string? _currentLogFilePath;
    private string? _lastLogFilePath;
    private string? _lastFileError;
    private FileLogWriterState _state = FileLogWriterState.Stopped;
    private FileLogWriterFaultInfo? _lastFault;
    private long _acceptedLineCount;
    private long _durableLineCount;
    private long _durableByteCount;
    private long _uncertainLineCount;
    private long _abandonedLineCount;
    private long _fileErrorCount;
    private long _droppedLineCount;
    private long _recoveryCount;
    private int _pendingRequestCount;
    private long _pendingByteCount;
    private long _startCount;
    private long _stopCount;
    private long _lifecycleErrorCount;
    private long _fileIoTimeoutCount;
    private long _maximumFileSizeBytes;
    private string _lastLifecycleAction = "File logging has not started.";
    private string _logFileName = string.Empty;
    private string _logRunTimeText = string.Empty;
    private bool _rotationRequested;
    private bool _disposed;

    public FileLogWriter(
        Func<string, FileMode, Stream>? streamFactory = null,
        TimeSpan? fileIoTimeout = null,
        TimeSpan? shutdownTimeout = null)
    {
        _streamFactory = streamFactory ?? OpenFileStream;
        _fileIoTimeout = ValidateTimeout(fileIoTimeout ?? DefaultFileIoTimeout, nameof(fileIoTimeout));
        _shutdownTimeout = ValidateTimeout(shutdownTimeout ?? DefaultShutdownTimeout, nameof(shutdownTimeout));
    }

    public event EventHandler<string>? Error;

    public event EventHandler? StatusChanged;

    public bool IsRunning => State is FileLogWriterState.Starting or FileLogWriterState.Running;

    public FileLogWriterState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
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

    public long WrittenLineCount => DurableLineCount;

    public long AcceptedLineCount => Interlocked.Read(ref _acceptedLineCount);

    public long DurableLineCount => Interlocked.Read(ref _durableLineCount);

    public long WrittenByteCount => DurableByteCount;

    public long DurableByteCount => Interlocked.Read(ref _durableByteCount);

    public long UncertainLineCount => Interlocked.Read(ref _uncertainLineCount);

    public long AbandonedLineCount => Interlocked.Read(ref _abandonedLineCount);

    public long FileErrorCount => Interlocked.Read(ref _fileErrorCount);

    public long DroppedLineCount => Interlocked.Read(ref _droppedLineCount);

    public long RecoveryCount => Interlocked.Read(ref _recoveryCount);

    public int PendingRequestCount => Volatile.Read(ref _pendingRequestCount);

    public long StartCount => Interlocked.Read(ref _startCount);

    public long StopCount => Interlocked.Read(ref _stopCount);

    public long LifecycleErrorCount => Interlocked.Read(ref _lifecycleErrorCount);

    public long FileIoTimeoutCount => Interlocked.Read(ref _fileIoTimeoutCount);

    public int PendingLateOperationCount
    {
        get
        {
            lock (_lateOperationGate)
            {
                return _lateOperations.Count;
            }
        }
    }

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

        if (requestNewFile && TryQueueNamingChange(naming))
        {
            SetLifecycleAction(string.IsNullOrWhiteSpace(normalizedLogFileName)
                ? "Log file name cleared; creating a new timestamped log."
                : $"Log file name active: {normalizedLogFileName}");
            return;
        }

        lock (_stateGate)
        {
            _logFileName = naming.LogFileName;
            if (requestNewFile && IsRunning)
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

            if (_writerTask is not null)
            {
                if (!_writerTask.IsCompleted)
                {
                    SetLifecycleAction("Start ignored: file logging is already running.");
                    return;
                }

                await StopWriterCoreAsync(CancellationToken.None, preserveFault: false);
            }

            var targetDirectory = string.IsNullOrWhiteSpace(directory)
                ? CreateDefaultLogDirectory()
                : directory;
            var naming = GetLogFileNamingSnapshot();
            await RunBlockingFileOperationAsync(
                () =>
                {
                    Directory.CreateDirectory(targetDirectory);
                    if (!string.IsNullOrWhiteSpace(naming.LogFileName))
                    {
                        var explicitPath = CreateLogFilePath(
                            string.Empty,
                            rotationIndex: 0,
                            duplicateIndex: 0,
                            naming,
                            targetDirectory);
                        if (File.Exists(explicitPath) || Directory.Exists(explicitPath))
                        {
                            throw new IOException($"Log file already exists: {explicitPath}");
                        }
                    }
                },
                "directory setup",
                cancellationToken);

            var openedAt = DateTimeOffset.Now;
            lock (_stateGate)
            {
                _directory = targetDirectory;
                _logRunTimeText = openedAt.LocalDateTime.ToString("HHmmss");
                _lastFault = null;
                _lastFileError = null;
            }

            SetCurrentLogFilePath(null);
            var openCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_queueGate)
            {
                _queue = CreateQueue();
                Volatile.Write(ref _pendingRequestCount, 0);
                Interlocked.Exchange(ref _pendingByteCount, 0);
                if (!_queue.Writer.TryWrite(FileLogWriteRequest.ForOpen(openedAt, openCompletion)))
                {
                    throw new InvalidOperationException("Could not queue the initial serial log file open request.");
                }

                Interlocked.Increment(ref _pendingRequestCount);
            }

            _writerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Interlocked.Increment(ref _startCount);
            SetLifecycleAction($"Starting file logging: {targetDirectory}", raiseStatusChanged: false);
            SetState(FileLogWriterState.Starting);
            _writerTask = Task.Run(() => ProcessAsync(_writerCancellation.Token), CancellationToken.None);

            await openCompletion.Task.WaitAsync(_fileIoTimeout, cancellationToken);
            if (State == FileLogWriterState.Starting)
            {
                SetState(FileLogWriterState.Running);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (State != FileLogWriterState.Faulted)
            {
                SetFault(ex, ClassifyFault(ex));
            }

            RecordLifecycleError($"File logging start failed: {ex.Message}");
            await StopWriterCoreAsync(CancellationToken.None, preserveFault: true);
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public bool TryEnqueue(LogLine line)
    {
        var request = FileLogWriteRequest.ForLine(line);
        var state = State;
        if (state is not (FileLogWriterState.Starting or FileLogWriterState.Running))
        {
            if (state == FileLogWriterState.Faulted)
            {
                RecordDroppedLine("File log writer is faulted");
            }

            return false;
        }

        var queued = false;
        lock (_queueGate)
        {
            if (Volatile.Read(ref _pendingRequestCount) < QueueCapacity &&
                Interlocked.Read(ref _pendingByteCount) + request.ByteCount <= QueueByteCapacity &&
                _queue.Writer.TryWrite(request))
            {
                Interlocked.Increment(ref _pendingRequestCount);
                Interlocked.Add(ref _pendingByteCount, request.ByteCount);
                Interlocked.Increment(ref _acceptedLineCount);
                queued = true;
            }
        }

        if (!queued)
        {
            RecordDroppedLine("File log queue is full");
        }

        return queued;
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
            await StopWriterCoreAsync(cancellationToken, preserveFault: false);
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
            await StopWriterCoreAsync(CancellationToken.None, preserveFault: false);
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }

    private async Task StopWriterCoreAsync(CancellationToken cancellationToken, bool preserveFault)
    {
        var writerTask = _writerTask;
        if (writerTask is null)
        {
            if (!preserveFault)
            {
                SetState(FileLogWriterState.Stopped);
            }

            SetLifecycleAction("Stop ignored: file logging is not running.");
            return;
        }

        if (State != FileLogWriterState.Faulted)
        {
            SetLifecycleAction("Stopping file logging.");
            SetState(FileLogWriterState.Stopping);
        }

        _queue.Writer.TryComplete();
        var cancellationRequested = false;
        var stopped = false;
        try
        {
            await writerTask.WaitAsync(_shutdownTimeout, cancellationToken);
            stopped = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationRequested = true;
            _writerCancellation?.Cancel();
            stopped = await WaitForWriterCompletionAsync(writerTask);
        }
        catch (TimeoutException)
        {
            _writerCancellation?.Cancel();
            stopped = await WaitForWriterCompletionAsync(writerTask);
            if (!stopped)
            {
                SetFault(
                    new TimeoutException($"File writer did not stop within {_shutdownTimeout}."),
                    FileLogWriterFaultCategory.RetryableIo);
                SetLifecycleAction("File writer stop timed out; the writer remains quarantined.");
                return;
            }
        }

        if (!stopped)
        {
            return;
        }

        _writerCancellation?.Dispose();
        _writerCancellation = null;
        _writerTask = null;
        Interlocked.Increment(ref _stopCount);
        if (!preserveFault && State != FileLogWriterState.Faulted)
        {
            SetState(FileLogWriterState.Stopped);
        }

        SetLifecycleAction("Stopped file logging.", raiseStatusChanged: false);
        RaiseStatusChanged();

        if (cancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private async Task<bool> WaitForWriterCompletionAsync(Task writerTask)
    {
        try
        {
            await writerTask.WaitAsync(_shutdownTimeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        StreamWriter? writer = null;
        var currentDate = string.Empty;
        var currentLogIdentity = string.Empty;
        var currentSizeBytes = 0L;
        var rotationIndex = 0;
        var batch = new List<LogLine>(FlushLineInterval);
        var batchBytes = 0L;
        DateTimeOffset? batchStartedAt = null;

        async Task FlushBatchAsync()
        {
            if (batch.Count == 0)
            {
                return;
            }

            await RunFileOperationAsync(
                writer!,
                "flush",
                operationCancellation => writer!.FlushAsync(operationCancellation),
                cancellationToken);
            Interlocked.Add(ref _durableLineCount, batch.Count);
            Interlocked.Add(ref _durableByteCount, batchBytes);
            batch.Clear();
            batchBytes = 0;
            batchStartedAt = null;
            RaiseStatusChanged();
        }

        async Task RecoverBatchAsync(Exception failure)
        {
            if (failure is FileIoTimeoutException)
            {
                throw failure;
            }

            if (batch.Count == 0 || writer is null)
            {
                throw failure;
            }

            var retryLines = batch.ToArray();
            var retryBytes = batchBytes;
            var recoveryNaming = GetLogFileNamingSnapshot();
            Interlocked.Add(ref _uncertainLineCount, retryLines.Length);
            ReportFileError($"File I/O failed; retrying {retryLines.Length:N0} accepted line(s): {failure.Message}");
            AbandonWriter(writer);
            writer = null;

            Exception? lastFailure = failure;
            for (var attempt = 0; attempt < MaximumRecoveryAttempts; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << (attempt - 1))), cancellationToken);
                    }

                    var nextRotationIndex = rotationIndex + 1;
                    var recovery = await CreateNewWriterAsync(
                        currentDate,
                        nextRotationIndex,
                        recoveryNaming,
                        cancellationToken);
                    var recoveryWriter = recovery.Writer;
                    var recoveryPath = recovery.Path;
                    try
                    {
                        foreach (var retryLine in retryLines)
                        {
                            await RunFileOperationAsync(
                                recoveryWriter,
                                "write",
                                operationCancellation => recoveryWriter.WriteLineAsync(
                                    retryLine.Formatted.AsMemory(),
                                    operationCancellation),
                                cancellationToken);
                        }

                        await RunFileOperationAsync(
                            recoveryWriter,
                            "flush",
                            operationCancellation => recoveryWriter.FlushAsync(operationCancellation),
                            cancellationToken);
                    }
                    catch
                    {
                        AbandonWriter(recoveryWriter);
                        throw;
                    }

                    writer = recoveryWriter;
                    rotationIndex = nextRotationIndex;
                    currentLogIdentity = CreateLogFileIdentity(recoveryNaming);
                    currentSizeBytes = retryBytes;
                    batch.Clear();
                    batchBytes = 0;
                    batchStartedAt = null;
                    Interlocked.Add(ref _durableLineCount, retryLines.Length);
                    Interlocked.Add(ref _durableByteCount, retryBytes);
                    Interlocked.Increment(ref _recoveryCount);
                    SetCurrentLogFilePath(recoveryPath);
                    SetLifecycleAction("Recovered file logging in a new segment.");
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (FileIoTimeoutException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastFailure = ex;
                }
            }

            batch.Clear();
            batchBytes = 0;
            batchStartedAt = null;
            Interlocked.Add(ref _abandonedLineCount, retryLines.Length);
            throw new IOException("File logging recovery failed after bounded retries.", lastFailure);
        }

        async Task FlushBatchWithRecoveryAsync()
        {
            if (batch.Count == 0)
            {
                return;
            }

            try
            {
                await FlushBatchAsync();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await RecoverBatchAsync(ex);
            }
        }

        async Task HandleRequestAsync(FileLogWriteRequest request)
        {
            if (request.Naming.HasValue)
            {
                ApplyLogFileNamingState(request.Naming.Value);
                await FlushBatchWithRecoveryAsync();
                await DisposeWriterAsync(writer, cancellationToken);
                writer = null;
                currentDate = string.Empty;
                currentLogIdentity = string.Empty;
                currentSizeBytes = 0;
                rotationIndex = 0;
                return;
            }

            if (request.OpenedAt.HasValue)
            {
                try
                {
                    var openedDate = request.OpenedAt.Value.LocalDateTime.ToString("yyyy-MM-dd");
                    var openNaming = GetLogFileNamingSnapshot();
                    var opened = await CreateNewWriterAsync(
                        openedDate,
                        rotationIndex: 0,
                        openNaming,
                        cancellationToken);
                    writer = opened.Writer;
                    var path = opened.Path;
                    currentDate = openedDate;
                    currentLogIdentity = CreateLogFileIdentity(openNaming);
                    currentSizeBytes = 0;
                    rotationIndex = 0;
                    SetCurrentLogFilePath(path);
                    request.OpenCompletion?.TrySetResult(path);
                }
                catch (Exception ex)
                {
                    request.OpenCompletion?.TrySetException(ex);
                    throw;
                }

                return;
            }

            var line = request.Line;
            if (line is null)
            {
                return;
            }

            var lineWasAddedToBatch = false;
            try
            {
                var lineDate = line.Timestamp.LocalDateTime.ToString("yyyy-MM-dd");
                var naming = GetLogFileNamingSnapshot();
                var lineLogIdentity = CreateLogFileIdentity(naming);
                var rotationRequested = ConsumeRotationRequest();
                if (writer is null ||
                    rotationRequested ||
                    !string.Equals(currentLogIdentity, lineLogIdentity, StringComparison.Ordinal))
                {
                    await FlushBatchWithRecoveryAsync();
                    await DisposeWriterAsync(writer, cancellationToken);
                    var opened = await CreateNewWriterAsync(
                        lineDate,
                        rotationIndex: 0,
                        naming,
                        cancellationToken);
                    writer = opened.Writer;
                    var path = opened.Path;
                    currentDate = lineDate;
                    currentLogIdentity = lineLogIdentity;
                    currentSizeBytes = 0;
                    rotationIndex = 0;
                    SetCurrentLogFilePath(path);
                }

                var maxFileSizeBytes = MaximumFileSizeBytes;
                if (maxFileSizeBytes > 0 && currentSizeBytes >= maxFileSizeBytes)
                {
                    await FlushBatchWithRecoveryAsync();
                    await DisposeWriterAsync(writer, cancellationToken);
                    rotationIndex++;
                    var rotated = await CreateNewWriterAsync(
                        currentDate,
                        rotationIndex,
                        GetLogFileNamingSnapshot(),
                        cancellationToken);
                    writer = rotated.Writer;
                    var path = rotated.Path;
                    currentSizeBytes = 0;
                    SetCurrentLogFilePath(path);
                }

                var formatted = line.Formatted;
                var bytesWritten = Encoding.UTF8.GetByteCount(formatted) + Encoding.UTF8.GetByteCount(Environment.NewLine);
                batch.Add(line);
                lineWasAddedToBatch = true;
                batchBytes += bytesWritten;
                batchStartedAt ??= DateTimeOffset.UtcNow;
                try
                {
                    await RunFileOperationAsync(
                        writer!,
                        "write",
                        operationCancellation => writer!.WriteLineAsync(
                            formatted.AsMemory(),
                            operationCancellation),
                        cancellationToken);
                    currentSizeBytes += bytesWritten;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    await RecoverBatchAsync(ex);
                }

                if (batch.Count >= FlushLineInterval)
                {
                    await FlushBatchWithRecoveryAsync();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                if (!lineWasAddedToBatch)
                {
                    Interlocked.Increment(ref _abandonedLineCount);
                }

                throw;
            }
        }

        try
        {
            using var flushTimer = new PeriodicTimer(FlushCheckInterval);
            var readTask = _queue.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var timerTask = flushTimer.WaitForNextTickAsync(cancellationToken).AsTask();

            while (true)
            {
                var completed = await Task.WhenAny(readTask, timerTask);
                if (ReferenceEquals(completed, timerTask))
                {
                    if (!await timerTask)
                    {
                        break;
                    }

                    if (batchStartedAt.HasValue && DateTimeOffset.UtcNow - batchStartedAt.Value >= FlushTimeInterval)
                    {
                        await FlushBatchWithRecoveryAsync();
                    }

                    timerTask = flushTimer.WaitForNextTickAsync(cancellationToken).AsTask();
                    continue;
                }

                if (!await readTask)
                {
                    break;
                }

                while (TryReadRequest(out var request))
                {
                    await HandleRequestAsync(request);
                }

                readTask = _queue.Reader.WaitToReadAsync(cancellationToken).AsTask();
            }

            await FlushBatchWithRecoveryAsync();

            await DisposeWriterAsync(writer, cancellationToken);
            writer = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _queue.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            if (State != FileLogWriterState.Faulted)
            {
                SetFault(ex, ClassifyFault(ex));
            }

            _queue.Writer.TryComplete(ex);
        }
        finally
        {
            AbandonWriter(writer);
            if (batch.Count > 0)
            {
                Interlocked.Add(ref _abandonedLineCount, batch.Count);
                batch.Clear();
            }

            DrainAcceptedRequests();
            SetCurrentLogFilePath(null);
            if (State != FileLogWriterState.Faulted)
            {
                SetState(FileLogWriterState.Stopped);
            }

            SetLifecycleAction("File writer task stopped.", raiseStatusChanged: false);
            RaiseStatusChanged();
        }
    }

    private StreamWriter CreateNewWriter(
        string dateText,
        int rotationIndex,
        LogFileNamingSnapshot naming,
        out string path)
    {
        if (!string.IsNullOrWhiteSpace(naming.LogFileName))
        {
            if (rotationIndex == 0)
            {
                path = CreateLogFilePath(dateText, rotationIndex: 0, duplicateIndex: 0, naming);
                return CreateWriter(path, FileMode.CreateNew);
            }

            for (var duplicateIndex = 0; duplicateIndex < 10_000; duplicateIndex++)
            {
                path = CreateLogFilePath(dateText, rotationIndex, duplicateIndex, naming);
                try
                {
                    return CreateWriter(path, FileMode.CreateNew);
                }
                catch (IOException) when (File.Exists(path))
                {
                }
            }

            throw new IOException("Could not create a unique rotated serial log file.");
        }

        for (var duplicateIndex = 0; duplicateIndex < 10_000; duplicateIndex++)
        {
            path = CreateLogFilePath(dateText, rotationIndex, duplicateIndex, naming);
            try
            {
                return CreateWriter(path, FileMode.CreateNew);
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }

        throw new IOException("Could not create a unique timestamped serial log file.");
    }

    private async Task<(StreamWriter Writer, string Path)> CreateNewWriterAsync(
        string dateText,
        int rotationIndex,
        LogFileNamingSnapshot naming,
        CancellationToken cancellationToken)
    {
        EnsureLateOperationCapacity();
        var openTask = Task.Run(() =>
        {
            var writer = CreateNewWriter(dateText, rotationIndex, naming, out var path);
            return (Writer: writer, Path: path);
        }, CancellationToken.None);

        try
        {
            return await openTask.WaitAsync(_fileIoTimeout, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            Interlocked.Increment(ref _fileIoTimeoutCount);
            TrackLateWriterCreation(openTask);
            throw new FileIoTimeoutException(
                $"File open timed out after {_fileIoTimeout}.",
                ex);
        }
        catch (OperationCanceledException) when (!openTask.IsCompleted)
        {
            TrackLateWriterCreation(openTask);
            throw;
        }
    }

    private string CreateLogFilePath(
        string dateText,
        int rotationIndex,
        int duplicateIndex,
        LogFileNamingSnapshot naming,
        string? directory = null)
    {
        directory ??= LogDirectory;
        if (!string.IsNullOrWhiteSpace(naming.LogFileName))
        {
            if (rotationIndex == 0)
            {
                return Path.Combine(directory, naming.LogFileName);
            }

            var extension = Path.GetExtension(naming.LogFileName);
            var stem = Path.GetFileNameWithoutExtension(naming.LogFileName);
            var explicitDuplicatePart = duplicateIndex == 0 ? string.Empty : $"_dup{duplicateIndex:D3}";
            return Path.Combine(directory, $"{stem}_{rotationIndex:D3}{explicitDuplicatePart}{extension}");
        }

        string runTimeText;
        lock (_stateGate)
        {
            runTimeText = _logRunTimeText;
        }

        var rotationPart = rotationIndex == 0 ? string.Empty : $"_{rotationIndex:D3}";
        var duplicatePart = duplicateIndex == 0 ? string.Empty : $"_dup{duplicateIndex:D3}";
        var fileName = $"{dateText}_{runTimeText}_serial{rotationPart}{duplicatePart}.log";
        return Path.Combine(directory, fileName);
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
            return new LogFileNamingSnapshot(_logFileName);
        }
    }

    private bool TryQueueNamingChange(LogFileNamingSnapshot naming)
    {
        lock (_queueGate)
        {
            if (State is not (FileLogWriterState.Starting or FileLogWriterState.Running))
            {
                return false;
            }

            if (!_queue.Writer.TryWrite(FileLogWriteRequest.ForNaming(naming)))
            {
                return false;
            }

            Interlocked.Increment(ref _pendingRequestCount);
            return true;
        }
    }

    private bool TryReadRequest(out FileLogWriteRequest request)
    {
        lock (_queueGate)
        {
            if (!_queue.Reader.TryRead(out request))
            {
                return false;
            }

            Interlocked.Decrement(ref _pendingRequestCount);
            Interlocked.Add(ref _pendingByteCount, -request.ByteCount);
            return true;
        }
    }

    private void DrainAcceptedRequests()
    {
        while (TryReadRequest(out var request))
        {
            if (request.Line is not null)
            {
                Interlocked.Increment(ref _abandonedLineCount);
            }

            request.OpenCompletion?.TrySetException(new IOException("File logging stopped before the request was processed."));
        }

        Volatile.Write(ref _pendingRequestCount, 0);
        Interlocked.Exchange(ref _pendingByteCount, 0);
    }

    private void ApplyLogFileNamingState(LogFileNamingSnapshot naming)
    {
        lock (_stateGate)
        {
            _logFileName = naming.LogFileName;
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

    private StreamWriter CreateWriter(string path, FileMode fileMode)
    {
        var stream = _streamFactory(path, fileMode);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024);
    }

    private async Task RunBlockingFileOperationAsync(
        Action operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        EnsureLateOperationCapacity();
        var operationTask = Task.Run(operation, CancellationToken.None);
        try
        {
            await operationTask.WaitAsync(_fileIoTimeout, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            Interlocked.Increment(ref _fileIoTimeoutCount);
            TrackLateTask(operationTask);
            throw new FileIoTimeoutException(
                $"File {operationName} timed out after {_fileIoTimeout}.",
                ex);
        }
        catch (OperationCanceledException) when (!operationTask.IsCompleted)
        {
            TrackLateTask(operationTask);
            throw;
        }
    }

    private static Stream OpenFileStream(string path, FileMode fileMode)
    {
        return new FileStream(
            path,
            fileMode,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private async Task DisposeWriterAsync(StreamWriter? writer, CancellationToken cancellationToken)
    {
        if (writer is not null)
        {
            await RunFileOperationAsync(
                writer,
                "close",
                _ => writer.DisposeAsync().AsTask(),
                cancellationToken);
        }
    }

    private void AbandonWriter(StreamWriter? writer)
    {
        if (writer is null || IsLateWriter(writer))
        {
            return;
        }

        try
        {
            EnsureLateOperationCapacity();
        }
        catch (FileIoTimeoutException)
        {
            // ponytail: the late-operation ceiling is deliberate; release this
            // last stream on a pool thread instead of blocking the writer worker.
            _ = Task.Run(() => DisposeAbandonedWriter(writer));
            return;
        }

        var cleanupTask = Task.Run(() => DisposeAbandonedWriter(writer));
        lock (_lateOperationGate)
        {
            _lateOperations.Add(cleanupTask);
            _lateWriters.Add(writer);
        }

        _ = CompleteAbandonedWriterAsync(writer, cleanupTask);
    }

    private static void DisposeAbandonedWriter(StreamWriter writer)
    {
        try
        {
            writer.BaseStream.Dispose();
        }
        catch
        {
        }
    }

    private async Task CompleteAbandonedWriterAsync(StreamWriter writer, Task cleanupTask)
    {
        try
        {
            await cleanupTask.ConfigureAwait(false);
        }
        finally
        {
            lock (_lateOperationGate)
            {
                _lateOperations.Remove(cleanupTask);
                _lateWriters.Remove(writer);
            }
        }
    }

    private async Task RunFileOperationAsync(
        StreamWriter writer,
        string operationName,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        EnsureLateOperationCapacity();
        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task operationTask;
        try
        {
            operationTask = operation(operationCancellation.Token);
        }
        catch
        {
            operationCancellation.Dispose();
            throw;
        }

        var lateWriterTracked = false;
        try
        {
            await operationTask.WaitAsync(_fileIoTimeout, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            Interlocked.Increment(ref _fileIoTimeoutCount);
            operationCancellation.Cancel();
            TrackLateWriter(writer, operationTask, operationCancellation);
            lateWriterTracked = true;
            throw new FileIoTimeoutException(
                $"File {operationName} timed out after {_fileIoTimeout}.",
                ex);
        }
        catch (OperationCanceledException) when (!operationTask.IsCompleted)
        {
            operationCancellation.Cancel();
            TrackLateWriter(writer, operationTask, operationCancellation);
            lateWriterTracked = true;
            throw;
        }
        finally
        {
            if (!lateWriterTracked)
            {
                operationCancellation.Dispose();
            }
        }
    }

    private void EnsureLateOperationCapacity()
    {
        lock (_lateOperationGate)
        {
            if (_lateOperations.Count >= MaximumLateOperationCount)
            {
                throw new FileIoTimeoutException(
                    $"File writer has {_lateOperations.Count} late I/O operation(s); refusing another operation.");
            }
        }
    }

    private void TrackLateWriter(
        StreamWriter writer,
        Task operationTask,
        CancellationTokenSource operationCancellation)
    {
        lock (_lateOperationGate)
        {
            _lateOperations.Add(operationTask);
            _lateWriters.Add(writer);
        }

        _ = CompleteLateWriterAsync(writer, operationTask, operationCancellation);
    }

    private async Task CompleteLateWriterAsync(
        StreamWriter writer,
        Task operationTask,
        CancellationTokenSource operationCancellation)
    {
        try
        {
            await operationTask.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            try
            {
                writer.BaseStream.Dispose();
            }
            catch
            {
            }

            lock (_lateOperationGate)
            {
                _lateOperations.Remove(operationTask);
                _lateWriters.Remove(writer);
            }

            operationCancellation.Dispose();
        }
    }

    private void TrackLateWriterCreation(Task<(StreamWriter Writer, string Path)> openTask)
    {
        lock (_lateOperationGate)
        {
            _lateOperations.Add(openTask);
        }

        _ = CompleteLateWriterCreationAsync(openTask);
    }

    private async Task CompleteLateWriterCreationAsync(Task<(StreamWriter Writer, string Path)> openTask)
    {
        StreamWriter? writer = null;
        try
        {
            writer = (await openTask.ConfigureAwait(false)).Writer;
            AbandonWriter(writer);
        }
        catch
        {
        }
        finally
        {
            lock (_lateOperationGate)
            {
                _lateOperations.Remove(openTask);
            }
        }
    }

    private void TrackLateTask(Task operationTask)
    {
        lock (_lateOperationGate)
        {
            _lateOperations.Add(operationTask);
        }

        _ = ObserveLateTaskAsync(operationTask);
    }

    private async Task ObserveLateTaskAsync(Task operationTask)
    {
        try
        {
            await operationTask.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            lock (_lateOperationGate)
            {
                _lateOperations.Remove(operationTask);
            }
        }
    }

    private bool IsLateWriter(StreamWriter writer)
    {
        lock (_lateOperationGate)
        {
            return _lateWriters.Contains(writer);
        }
    }

    private void SetState(FileLogWriterState state)
    {
        lock (_stateGate)
        {
            _state = state;
            if (state == FileLogWriterState.Starting)
            {
                _lastFault = null;
            }
        }

        RaiseStatusChanged();
    }

    private void SetFault(Exception exception, FileLogWriterFaultCategory category)
    {
        var message = $"File logging faulted: {exception.Message}";
        lock (_stateGate)
        {
            _state = FileLogWriterState.Faulted;
            _lastFault = new FileLogWriterFaultInfo(
                category,
                message,
                exception.GetType().Name,
                DateTimeOffset.Now,
                category == FileLogWriterFaultCategory.RetryableIo);
            _lastLifecycleAction = message;
        }

        ReportFileError(message);
    }

    private static FileLogWriterFaultCategory ClassifyFault(Exception exception)
    {
        return exception is ArgumentException or UnauthorizedAccessException
            ? FileLogWriterFaultCategory.DeterministicConfiguration
            : FileLogWriterFaultCategory.RetryableIo;
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

    private static Channel<FileLogWriteRequest> CreateQueue()
    {
        return Channel.CreateBounded<FileLogWriteRequest>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    private static TimeSpan ValidateTimeout(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Timeout must be positive and finite.");
        }

        return value;
    }

    private sealed class FileIoTimeoutException : IOException
    {
        public FileIoTimeoutException(string message)
            : base(message)
        {
        }

        public FileIoTimeoutException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private readonly record struct LogFileNamingSnapshot(string LogFileName);

    private readonly record struct FileLogWriteRequest(
        LogLine? Line,
        LogFileNamingSnapshot? Naming,
        DateTimeOffset? OpenedAt,
        TaskCompletionSource<string>? OpenCompletion,
        long ByteCount)
    {
        public static FileLogWriteRequest ForLine(LogLine line)
        {
            var byteCount = Encoding.UTF8.GetByteCount(line.Formatted) + Encoding.UTF8.GetByteCount(Environment.NewLine);
            return new(line, null, null, null, byteCount);
        }

        public static FileLogWriteRequest ForNaming(LogFileNamingSnapshot naming) => new(null, naming, null, null, 0);

        public static FileLogWriteRequest ForOpen(
            DateTimeOffset openedAt,
            TaskCompletionSource<string> completion) => new(null, null, openedAt, completion, 0);
    }
}
