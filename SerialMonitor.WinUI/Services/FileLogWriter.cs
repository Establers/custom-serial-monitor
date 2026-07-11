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
    private string _sessionFileName = string.Empty;
    private string _sessionFileTimeText = string.Empty;
    private bool _useSessionNameInFileName;
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

    public void UpdateSessionFileNaming(
        string? sanitizedSessionName,
        bool useSessionNameInFileName,
        DateTimeOffset? sessionStartedAt,
        bool requestNewFile)
    {
        var normalizedSessionName = NormalizeSessionFileName(sanitizedSessionName);
        var useSessionFileName = useSessionNameInFileName && !string.IsNullOrWhiteSpace(normalizedSessionName);
        var sessionTimeText = useSessionFileName
            ? (sessionStartedAt ?? DateTimeOffset.Now).LocalDateTime.ToString("HHmm")
            : string.Empty;

        var naming = new LogFileNamingSnapshot(useSessionFileName, normalizedSessionName, sessionTimeText);
        if (requestNewFile && IsRunning && _writerTask is not null)
        {
            if (_queue.Writer.TryWrite(FileLogWriteRequest.ForNaming(naming)))
            {
                Interlocked.Increment(ref _pendingRequestCount);
                SetLifecycleAction(useSessionFileName
                    ? $"Session log filename active: {normalizedSessionName}"
                    : "Session log filename disabled; regular filename will be used on next write.");
                return;
            }
        }

        lock (_stateGate)
        {
            ApplySessionFileNamingState(naming);
            if (requestNewFile && _isRunning)
            {
                _rotationRequested = true;
                _lastLifecycleAction = useSessionFileName
                    ? $"Session log filename active: {normalizedSessionName}"
                    : "Session log filename disabled; regular filename will be used on next write.";
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
            _queue = CreateQueue();
            Volatile.Write(ref _pendingRequestCount, 0);
            _writerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Interlocked.Increment(ref _startCount);
            SetLifecycleAction($"Starting file logging: {_directory}", raiseStatusChanged: false);
            SetRunningState(isRunning: true, clearLastError: true);
            _writerTask = Task.Run(() => ProcessAsync(_writerCancellation.Token), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordLifecycleError($"File logging start failed: {ex.Message}");
            ReportFileError($"File logging start failed: {ex.Message}");
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
                    ApplySessionFileNamingState(request.Naming.Value);
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

                var line = request.Line;
                if (line is null)
                {
                    continue;
                }

                var lineDate = line.Timestamp.LocalDateTime.ToString("yyyy-MM-dd");
                var naming = GetLogFileNamingSnapshot();
                var lineLogIdentity = CreateLogFileIdentity(lineDate, naming);
                var rotationRequested = ConsumeRotationRequest();
                if (writer is null ||
                    rotationRequested ||
                    !string.Equals(currentDate, lineDate, StringComparison.Ordinal) ||
                    !string.Equals(currentLogIdentity, lineLogIdentity, StringComparison.Ordinal))
                {
                    await FlushAndDisposeAsync(writer);
                    writer = null;

                    currentDate = lineDate;
                    currentLogIdentity = lineLogIdentity;
                    rotationIndex = 0;
                    var path = CreateLogFilePath(currentDate, rotationIndex, naming);
                    writer = CreateWriter(path);
                    currentSizeBytes = new FileInfo(path).Length;
                    SetCurrentLogFilePath(path);
                    writtenSinceFlush = 0;
                    lastFlush = DateTimeOffset.UtcNow;
                }

                // Size rotation skeleton: default 0 disables size rotation until a future UI/profile setting is added.
                var maxFileSizeBytes = MaximumFileSizeBytes;
                if (maxFileSizeBytes > 0 && currentSizeBytes >= maxFileSizeBytes)
                {
                    await FlushAndDisposeAsync(writer);
                    rotationIndex++;
                    var path = CreateLogFilePath(currentDate, rotationIndex, GetLogFileNamingSnapshot());
                    writer = CreateWriter(path);
                    currentSizeBytes = new FileInfo(path).Length;
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
            SetLifecycleAction("File writer task stopped.", raiseStatusChanged: false);
            SetRunningState(isRunning: false);
        }
    }

    private string CreateLogFilePath(string dateText, int rotationIndex, LogFileNamingSnapshot naming)
    {
        var baseName = naming.UseSessionNameInFileName
            ? $"{dateText}_{naming.SessionFileTimeText}_{naming.SessionFileName}_serial"
            : $"{dateText}_serial";

        var fileName = rotationIndex == 0
            ? $"{baseName}.log"
            : $"{baseName}_{rotationIndex:D3}.log";

        return Path.Combine(_directory, fileName);
    }

    private static string CreateLogFileIdentity(string dateText, LogFileNamingSnapshot naming)
    {
        return naming.UseSessionNameInFileName
            ? $"{dateText}_{naming.SessionFileTimeText}_{naming.SessionFileName}"
            : dateText;
    }

    private LogFileNamingSnapshot GetLogFileNamingSnapshot()
    {
        lock (_stateGate)
        {
            return new LogFileNamingSnapshot(
                _useSessionNameInFileName,
                _sessionFileName,
                _sessionFileTimeText);
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

    private static string NormalizeSessionFileName(string? sessionName)
    {
        if (string.IsNullOrWhiteSpace(sessionName))
        {
            return string.Empty;
        }

        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(sessionName.Trim().Length);
        var previousWasSpace = false;
        foreach (var character in sessionName.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }

                continue;
            }

            previousWasSpace = false;
            if (char.IsLetterOrDigit(character) ||
                character == '_' ||
                character == '-')
            {
                builder.Append(character);
                continue;
            }

            builder.Append(invalidFileNameChars.Contains(character) || char.IsControl(character) ? '_' : character);
        }

        return builder.ToString().Trim();
    }

    private void ApplySessionFileNamingState(LogFileNamingSnapshot naming)
    {
        lock (_stateGate)
        {
            _sessionFileName = naming.SessionFileName;
            _sessionFileTimeText = naming.SessionFileTimeText;
            _useSessionNameInFileName = naming.UseSessionNameInFileName;
        }
    }

    private static StreamWriter CreateWriter(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.Append,
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

    private void SetCurrentLogFilePath(string path)
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

    private readonly record struct LogFileNamingSnapshot(
        bool UseSessionNameInFileName,
        string SessionFileName,
        string SessionFileTimeText);

    private readonly record struct FileLogWriteRequest(LogLine? Line, LogFileNamingSnapshot? Naming)
    {
        public static FileLogWriteRequest ForLine(LogLine line) => new(line, null);

        public static FileLogWriteRequest ForNaming(LogFileNamingSnapshot naming) => new(null, naming);
    }
}
