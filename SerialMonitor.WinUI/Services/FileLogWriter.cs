using System.Text;
using System.Threading.Channels;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed class FileLogWriter : IFileLogWriter
{
    private const int QueueCapacity = 100_000;
    private const int FlushLineInterval = 100;
    private static readonly TimeSpan FlushTimeInterval = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateGate = new();
    private Channel<FileLogWriteRequest> _queue = CreateQueue();
    private CancellationTokenSource? _writerCancellation;
    private Task? _writerTask;
    private string _directory = CreateDefaultLogDirectory();
    private string? _currentLogFilePath;
    private string? _lastFileError;
    private long _writtenLineCount;
    private long _writtenByteCount;
    private long _fileErrorCount;
    private long _droppedLineCount;
    private int _pendingRequestCount;
    private long _startCount;
    private long _stopCount;
    private long _lifecycleErrorCount;
    private long _maximumFileSizeBytes;
    private string _lastLifecycleAction = "File logging has not started.";
    private string _logFileName = string.Empty;
    private string _logRunTimeText = string.Empty;
    private bool _rotationRequested;
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

    public int PendingRequestCount => Volatile.Read(ref _pendingRequestCount);

    public long StartCount => Interlocked.Read(ref _startCount);

    public long StopCount => Interlocked.Read(ref _stopCount);

    public long LifecycleErrorCount => Interlocked.Read(ref _lifecycleErrorCount);

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
            if (_queue.Writer.TryWrite(FileLogWriteRequest.ForNaming(naming)))
            {
                Interlocked.Increment(ref _pendingRequestCount);
                SetLifecycleAction(string.IsNullOrWhiteSpace(normalizedLogFileName)
                    ? "Log file name cleared; creating a new timestamped log."
                    : $"Log file name active: {normalizedLogFileName}");
                return;
            }
        }

        lock (_stateGate)
        {
            ApplyLogFileNamingState(naming);
            if (requestNewFile && _isRunning)
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
                SetLifecycleAction("Start ignored: file logging is already running.");
                return;
            }

            if (_writerTask is not null)
            {
                await StopWriterAsync(CancellationToken.None);
            }

            _directory = string.IsNullOrWhiteSpace(directory) ? CreateDefaultLogDirectory() : directory;
            Directory.CreateDirectory(_directory);
            var naming = GetLogFileNamingSnapshot();
            if (!string.IsNullOrWhiteSpace(naming.LogFileName))
            {
                var explicitPath = CreateLogFilePath(string.Empty, rotationIndex: 0, duplicateIndex: 0, naming);
                if (File.Exists(explicitPath) || Directory.Exists(explicitPath))
                {
                    throw new IOException($"Log file already exists: {explicitPath}");
                }
            }

            var openedAt = DateTimeOffset.Now;
            lock (_stateGate)
            {
                _logRunTimeText = openedAt.LocalDateTime.ToString("HHmmss");
            }
            SetCurrentLogFilePath(null);
            _queue = CreateQueue();
            Volatile.Write(ref _pendingRequestCount, 0);
            var openCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_queue.Writer.TryWrite(FileLogWriteRequest.ForOpen(openedAt, openCompletion)))
            {
                throw new InvalidOperationException("Could not queue the initial serial log file open request.");
            }

            Interlocked.Increment(ref _pendingRequestCount);
            _writerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Interlocked.Increment(ref _startCount);
            SetLifecycleAction($"Starting file logging: {_directory}", raiseStatusChanged: false);
            SetRunningState(isRunning: true, clearLastError: true);
            _writerTask = Task.Run(() => ProcessAsync(_writerCancellation.Token), CancellationToken.None);
            await openCompletion.Task.WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
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
        if (!IsRunning || _writerTask is null)
        {
            return false;
        }

        if (_queue.Writer.TryWrite(FileLogWriteRequest.ForLine(line)))
        {
            Interlocked.Increment(ref _pendingRequestCount);
            return true;
        }

        RecordDroppedLine("File log queue is full. Dropped log lines");
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
            if (IsRunning)
            {
                SetRunningState(isRunning: false);
            }

            SetLifecycleAction("Stop ignored: file logging is not running.");
            return;
        }

        SetLifecycleAction("Stopping file logging.");
        _queue.Writer.TryComplete();

        try
        {
            await writerTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        _writerCancellation?.Dispose();
        _writerCancellation = null;
        _writerTask = null;
        Interlocked.Increment(ref _stopCount);
        SetLifecycleAction("Stopped file logging.", raiseStatusChanged: false);
        SetRunningState(isRunning: false);
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        StreamWriter? writer = null;
        var currentDate = string.Empty;
        var currentLogIdentity = string.Empty;
        var currentSizeBytes = 0L;
        var rotationIndex = 0;
        var writtenSinceFlush = 0;
        var lastFlush = DateTimeOffset.UtcNow;

        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                Interlocked.Decrement(ref _pendingRequestCount);
                if (request.Naming.HasValue)
                {
                    ApplyLogFileNamingState(request.Naming.Value);
                    await FlushAndDisposeAsync(writer);
                    writer = null;
                    currentDate = string.Empty;
                    currentLogIdentity = string.Empty;
                    currentSizeBytes = 0;
                    rotationIndex = 0;
                    writtenSinceFlush = 0;
                    lastFlush = DateTimeOffset.UtcNow;
                    RaiseStatusChanged();
                    continue;
                }

                if (request.OpenedAt.HasValue)
                {
                    try
                    {
                        var openedDate = request.OpenedAt.Value.LocalDateTime.ToString("yyyy-MM-dd");
                        var openNaming = GetLogFileNamingSnapshot();
                        writer = CreateNewWriter(openedDate, rotationIndex: 0, openNaming, out var path);
                        currentDate = openedDate;
                        currentLogIdentity = CreateLogFileIdentity(openNaming);
                        currentSizeBytes = 0;
                        rotationIndex = 0;
                        writtenSinceFlush = 0;
                        lastFlush = DateTimeOffset.UtcNow;
                        SetCurrentLogFilePath(path);
                        request.OpenCompletion?.TrySetResult(path);
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
                if (writer is null ||
                    rotationRequested ||
                    !string.Equals(currentLogIdentity, lineLogIdentity, StringComparison.Ordinal))
                {
                    await FlushAndDisposeAsync(writer);
                    writer = null;

                    currentDate = lineDate;
                    currentLogIdentity = lineLogIdentity;
                    rotationIndex = 0;
                    writer = CreateNewWriter(currentDate, rotationIndex, naming, out var path);
                    currentSizeBytes = 0;
                    SetCurrentLogFilePath(path);
                    writtenSinceFlush = 0;
                    lastFlush = DateTimeOffset.UtcNow;
                }

                // A value of 0 disables optional size-based rotation.
                var maxFileSizeBytes = MaximumFileSizeBytes;
                if (maxFileSizeBytes > 0 && currentSizeBytes >= maxFileSizeBytes)
                {
                    await FlushAndDisposeAsync(writer);
                    rotationIndex++;
                    writer = CreateNewWriter(currentDate, rotationIndex, GetLogFileNamingSnapshot(), out var path);
                    currentSizeBytes = 0;
                    SetCurrentLogFilePath(path);
                    writtenSinceFlush = 0;
                    lastFlush = DateTimeOffset.UtcNow;
                }

                var formatted = line.Formatted;
                await writer!.WriteLineAsync(formatted);

                var bytesWritten = Encoding.UTF8.GetByteCount(formatted) + Encoding.UTF8.GetByteCount(Environment.NewLine);
                currentSizeBytes += bytesWritten;
                Interlocked.Increment(ref _writtenLineCount);
                Interlocked.Add(ref _writtenByteCount, bytesWritten);
                writtenSinceFlush++;

                if (writtenSinceFlush >= FlushLineInterval || DateTimeOffset.UtcNow - lastFlush >= FlushTimeInterval)
                {
                    await writer.FlushAsync();
                    writtenSinceFlush = 0;
                    lastFlush = DateTimeOffset.UtcNow;
                    RaiseStatusChanged();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ReportFileError($"File logging failed: {ex.Message}");
        }
        finally
        {
            await FlushAndDisposeAsync(writer);
            Volatile.Write(ref _pendingRequestCount, 0);
            SetCurrentLogFilePath(null);
            SetLifecycleAction("File writer task stopped.", raiseStatusChanged: false);
            SetRunningState(isRunning: false);
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
            path = CreateLogFilePath(dateText, rotationIndex, duplicateIndex: 0, naming);
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

        throw new IOException("Could not create a unique timestamped serial log file.");
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
            return Path.Combine(_directory, $"{stem}_{rotationIndex:D3}{extension}");
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

    private static StreamWriter CreateWriter(string path, FileMode fileMode)
    {
        var stream = new FileStream(
            path,
            fileMode,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024);
    }

    private static async Task FlushAndDisposeAsync(StreamWriter? writer)
    {
        if (writer is null)
        {
            return;
        }

        await writer.FlushAsync();
        await writer.DisposeAsync();
    }

    private void SetRunningState(bool isRunning, bool clearLastError = false)
    {
        lock (_stateGate)
        {
            _isRunning = isRunning;
            if (clearLastError)
            {
                _lastFileError = null;
            }

            _lastLifecycleAction = isRunning ? "File logging running." : _lastLifecycleAction;
        }

        RaiseStatusChanged();
    }

    private void SetCurrentLogFilePath(string? path)
    {
        lock (_stateGate)
        {
            _currentLogFilePath = path;
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

    private static Channel<FileLogWriteRequest> CreateQueue()
    {
        return Channel.CreateBounded<FileLogWriteRequest>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    private readonly record struct LogFileNamingSnapshot(string LogFileName);

    private readonly record struct FileLogWriteRequest(
        LogLine? Line,
        LogFileNamingSnapshot? Naming,
        DateTimeOffset? OpenedAt,
        TaskCompletionSource<string>? OpenCompletion)
    {
        public static FileLogWriteRequest ForLine(LogLine line) => new(line, null, null, null);

        public static FileLogWriteRequest ForNaming(LogFileNamingSnapshot naming) => new(null, naming, null, null);

        public static FileLogWriteRequest ForOpen(
            DateTimeOffset openedAt,
            TaskCompletionSource<string> completion) => new(null, null, openedAt, completion);
    }
}
