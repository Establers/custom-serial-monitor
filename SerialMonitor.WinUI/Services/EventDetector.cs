using System.Text;
using System.Threading.Channels;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed class EventDetector : IEventDetector
{
    private const int InputQueueCapacity = 100_000;
    private const int OutputQueueCapacity = 20_000;
    private const int EventLogQueueCapacity = 50_000;
    private const int MaxPendingContextCaptures = 1_000;
    private const int FlushLineInterval = 50;
    private static readonly TimeSpan FlushTimeInterval = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateGate = new();
    private Channel<LogLine> _input = CreateInputQueue();
    private Channel<DetectedEvent> _events = CreateEventQueue();
    private Channel<DetectedEventContext> _completedContexts = CreateEventContextQueue();
    private Channel<EventContextCapture> _eventLogQueue = CreateEventLogQueue();
    private CancellationTokenSource? _cancellation;
    private Task? _detectorTask;
    private Task? _eventLogWriterTask;
    private LogRuleMatcher.CompiledEventRule[] _rules = Array.Empty<LogRuleMatcher.CompiledEventRule>();
    private EventContextSettings _contextSettings = new();
    private readonly Queue<LogLine> _beforeContextBuffer = new();
    private readonly List<EventContextCapture> _pendingContextCaptures = new();
    private string _logDirectory = CreateDefaultLogDirectory();
    private string? _currentEventLogFilePath;
    private string? _lastDetectedEventText;
    private string? _lastError;
    private string? _lastRuleEvaluationError;
    private string? _lastHexRuleMatchName;
    private string? _lastHexRuleMatchBytesPreview;
    private string _sessionFileName = string.Empty;
    private string _sessionFileTimeText = string.Empty;
    private bool _useSessionNameInFileName;
    private bool _rotationRequested;
    private bool _eventLogWritingEnabled;
    private int _eventLogCloseRequested;
    private long _detectedEventCount;
    private long _eventLogWrittenCount;
    private long _errorCount;
    private long _droppedInputLineCount;
    private long _droppedOutputEventCount;
    private long _contextCapturesStartedCount;
    private long _contextCapturesCompletedCount;
    private long _contextCaptureDroppedCount;
    private long _contextCaptureFailedCount;
    private long _ruleEvaluationErrorCount;
    private int _compiledTextRuleCount;
    private int _compiledHexRuleCount;
    private int _invalidCompiledRuleCount;
    private int _activePendingContextCount;
    private bool _isRunning;
    private bool _disposed;

    public event EventHandler<string>? Error;

    public event EventHandler? StatusChanged;

    public ChannelReader<DetectedEvent> DetectedEvents => _events.Reader;

    public ChannelReader<DetectedEventContext> CompletedEventContexts => _completedContexts.Reader;

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

    public bool EventLogWritingEnabled
    {
        get
        {
            lock (_stateGate)
            {
                return _eventLogWritingEnabled;
            }
        }
    }

    public int EventRuleCount => Volatile.Read(ref _rules).Length;

    public int CompiledTextRuleCount => Volatile.Read(ref _compiledTextRuleCount);

    public int CompiledHexRuleCount => Volatile.Read(ref _compiledHexRuleCount);

    public int InvalidCompiledRuleCount => Volatile.Read(ref _invalidCompiledRuleCount);

    public long DetectedEventCount => Interlocked.Read(ref _detectedEventCount);

    public long EventLogWrittenCount => Interlocked.Read(ref _eventLogWrittenCount);

    public long ErrorCount => Interlocked.Read(ref _errorCount);

    public long DroppedInputLineCount => Interlocked.Read(ref _droppedInputLineCount);

    public long DroppedOutputEventCount => Interlocked.Read(ref _droppedOutputEventCount);

    public long ContextCapturesStartedCount => Interlocked.Read(ref _contextCapturesStartedCount);

    public long ContextCapturesCompletedCount => Interlocked.Read(ref _contextCapturesCompletedCount);

    public int ActivePendingContextCount => Volatile.Read(ref _activePendingContextCount);

    public long ContextCaptureDroppedCount => Interlocked.Read(ref _contextCaptureDroppedCount);

    public long ContextCaptureFailedCount => Interlocked.Read(ref _contextCaptureFailedCount);

    public long RuleEvaluationErrorCount => Interlocked.Read(ref _ruleEvaluationErrorCount);

    public string? LastRuleEvaluationError
    {
        get
        {
            lock (_stateGate)
            {
                return _lastRuleEvaluationError;
            }
        }
    }

    public string? LastHexRuleMatchName
    {
        get
        {
            lock (_stateGate)
            {
                return _lastHexRuleMatchName;
            }
        }
    }

    public string? LastHexRuleMatchBytesPreview
    {
        get
        {
            lock (_stateGate)
            {
                return _lastHexRuleMatchBytesPreview;
            }
        }
    }

    public string? LastDetectedEventText
    {
        get
        {
            lock (_stateGate)
            {
                return _lastDetectedEventText;
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

    public string? CurrentEventLogFilePath
    {
        get
        {
            lock (_stateGate)
            {
                return _currentEventLogFilePath;
            }
        }
    }

    public async Task StartAsync(
        IReadOnlyList<EventRule> rules,
        EventContextSettings contextSettings,
        string logDirectory,
        bool eventLogWritingEnabled,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(contextSettings);

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await StopCurrentAsync(CancellationToken.None);

            UpdateRules(rules);
            Volatile.Write(ref _contextSettings, NormalizeContextSettings(contextSettings));
            _logDirectory = string.IsNullOrWhiteSpace(logDirectory) ? CreateDefaultLogDirectory() : logDirectory;
            _eventLogWritingEnabled = eventLogWritingEnabled;
            if (_eventLogWritingEnabled)
            {
                Directory.CreateDirectory(_logDirectory);
            }

            Interlocked.Exchange(ref _eventLogCloseRequested, 0);
            _beforeContextBuffer.Clear();
            _pendingContextCaptures.Clear();
            SetActivePendingContextCount();
            _input = CreateInputQueue();
            _events = CreateEventQueue();
            _completedContexts = CreateEventContextQueue();
            _eventLogQueue = CreateEventLogQueue();
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            SetRunning(true, clearLastError: true);
            _detectorTask = Task.Run(() => ProcessEventsAsync(_cancellation.Token), CancellationToken.None);
            _eventLogWriterTask = Task.Run(() => ProcessEventLogAsync(_cancellation.Token), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportError($"Event detector start failed: {ex.Message}");
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public bool TryEnqueue(LogLine line)
    {
        if (!IsRunning || _detectorTask is null)
        {
            return false;
        }

        if (_input.Writer.TryWrite(line))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedInputLineCount);
        RaiseStatusChanged();
        return false;
    }

    public void UpdateRules(IReadOnlyList<EventRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var snapshot = rules
            .Where(rule => rule is not null)
            .Select(LogRuleMatcher.Compile)
            .ToArray();
        Volatile.Write(ref _rules, snapshot);
        Volatile.Write(ref _compiledTextRuleCount, snapshot.Count(rule => rule.IsTextRule));
        Volatile.Write(ref _compiledHexRuleCount, snapshot.Count(rule => rule.IsHexRule));
        Volatile.Write(ref _invalidCompiledRuleCount, snapshot.Count(rule => rule.IsInvalid));
        var invalidRule = snapshot.FirstOrDefault(rule => rule.IsInvalid);
        if (invalidRule?.CompileError is not null)
        {
            RecordRuleEvaluationError(invalidRule.CompileError);
        }

        RaiseStatusChanged();
    }

    public void UpdateContextSettings(EventContextSettings contextSettings)
    {
        ArgumentNullException.ThrowIfNull(contextSettings);

        Volatile.Write(ref _contextSettings, NormalizeContextSettings(contextSettings));
        RaiseStatusChanged();
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

        lock (_stateGate)
        {
            _sessionFileName = normalizedSessionName;
            _sessionFileTimeText = sessionTimeText;
            _useSessionNameInFileName = useSessionFileName;
            if (requestNewFile && _isRunning)
            {
                _rotationRequested = true;
            }
        }

        RaiseStatusChanged();
    }

    public void SetEventLogWritingEnabled(bool enabled, string? logDirectory = null)
    {
        try
        {
            lock (_stateGate)
            {
                _eventLogWritingEnabled = enabled;
                if (!string.IsNullOrWhiteSpace(logDirectory))
                {
                    _logDirectory = logDirectory;
                }
            }

            if (enabled)
            {
                Directory.CreateDirectory(LogDirectorySnapshot());
                Interlocked.Exchange(ref _eventLogCloseRequested, 0);
            }
            else
            {
                Interlocked.Exchange(ref _eventLogCloseRequested, 1);
            }
        }
        catch (Exception ex)
        {
            ReportError($"Event log writing toggle failed: {ex.Message}");
        }

        RaiseStatusChanged();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed && _detectorTask is null && _eventLogWriterTask is null)
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
            await StopCurrentAsync(CancellationToken.None);
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }

    private async Task StopCurrentAsync(CancellationToken cancellationToken)
    {
        var detectorTask = _detectorTask;
        var eventLogWriterTask = _eventLogWriterTask;

        if (detectorTask is null && eventLogWriterTask is null)
        {
            SetRunning(false);
            return;
        }

        _input.Writer.TryComplete();

        if (detectorTask is not null)
        {
            try
            {
                await detectorTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        _eventLogQueue.Writer.TryComplete();

        if (eventLogWriterTask is not null)
        {
            try
            {
                await eventLogWriterTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        _events.Writer.TryComplete();
        _completedContexts.Writer.TryComplete();
        _cancellation?.Dispose();
        _cancellation = null;
        _detectorTask = null;
        _eventLogWriterTask = null;
        SetRunning(false);
    }

    private async Task ProcessEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in _input.Reader.ReadAllAsync(cancellationToken))
            {
                CaptureAfterContext(line);

                var beforeContext = _beforeContextBuffer.ToArray();
                foreach (var detectedEvent in Detect(line, beforeContext))
                {
                    RecordDetectedEvent(detectedEvent);

                    if (!_events.Writer.TryWrite(detectedEvent))
                    {
                        Interlocked.Increment(ref _droppedOutputEventCount);
                    }

                    StartContextCapture(detectedEvent);
                }

                AddBeforeContext(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ReportError($"Event detector failed: {ex.Message}");
        }
        finally
        {
            CompletePendingContextCaptures();
            _eventLogQueue.Writer.TryComplete();
            RaiseStatusChanged();
        }
    }

    private IEnumerable<DetectedEvent> Detect(LogLine line, IReadOnlyList<LogLine> beforeContext)
    {
        var rules = Volatile.Read(ref _rules);
        if (rules.Length == 0)
        {
            yield break;
        }

        foreach (var rule in rules)
        {
            bool isMatch;
            string? matchError;
            try
            {
                isMatch = LogRuleMatcher.IsMatch(line, rule, out matchError);
            }
            catch (Exception ex)
            {
                RecordRuleEvaluationError($"Rule evaluation failed: {FormatRuleName(rule.Rule)} ({ex.Message})");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(matchError))
            {
                RecordRuleEvaluationError(matchError);
                continue;
            }

            if (isMatch)
            {
                var sourceRule = rule.Rule;
                if (sourceRule.MatchMode == LogRuleMatchMode.Hex)
                {
                    RecordHexRuleMatch(sourceRule, line);
                }

                yield return new DetectedEvent(
                    DateTimeOffset.Now,
                    string.IsNullOrWhiteSpace(sourceRule.Name) ? sourceRule.Keyword : sourceRule.Name,
                    sourceRule.Keyword,
                    line.Direction,
                    line.Text,
                    line,
                    beforeContext);
            }
        }
    }

    private void RecordRuleEvaluationError(string message)
    {
        var safeMessage = string.IsNullOrWhiteSpace(message)
            ? "Rule evaluation failed."
            : message.Trim();
        Interlocked.Increment(ref _ruleEvaluationErrorCount);

        var changed = false;
        lock (_stateGate)
        {
            if (!string.Equals(_lastRuleEvaluationError, safeMessage, StringComparison.Ordinal))
            {
                _lastRuleEvaluationError = safeMessage;
                changed = true;
            }
        }

        if (changed)
        {
            RaiseStatusChanged();
        }
    }

    private void RecordHexRuleMatch(EventRule rule, LogLine line)
    {
        var ruleName = FormatRuleName(rule);
        var bytesPreview = LogRuleMatcher.FormatBytesPreview(line.RawBytes);
        var changed = false;
        lock (_stateGate)
        {
            if (!string.Equals(_lastHexRuleMatchName, ruleName, StringComparison.Ordinal) ||
                !string.Equals(_lastHexRuleMatchBytesPreview, bytesPreview, StringComparison.Ordinal))
            {
                _lastHexRuleMatchName = ruleName;
                _lastHexRuleMatchBytesPreview = bytesPreview;
                changed = true;
            }
        }

        if (changed)
        {
            RaiseStatusChanged();
        }
    }

    private void AddBeforeContext(LogLine line)
    {
        _beforeContextBuffer.Enqueue(line);
        var contextSettings = Volatile.Read(ref _contextSettings);
        var capacity = Math.Max(0, contextSettings.BeforeContextLines);
        while (_beforeContextBuffer.Count > capacity)
        {
            _beforeContextBuffer.Dequeue();
        }
    }

    private void CaptureAfterContext(LogLine line)
    {
        if (_pendingContextCaptures.Count == 0)
        {
            return;
        }

        var changed = false;
        for (var i = 0; i < _pendingContextCaptures.Count;)
        {
            var capture = _pendingContextCaptures[i];
            capture.AddAfterLine(line);
            if (capture.IsComplete)
            {
                _pendingContextCaptures.RemoveAt(i);
                changed = true;
                QueueCompletedContextCapture(capture);
                continue;
            }

            i++;
        }

        if (changed)
        {
            SetActivePendingContextCount();
        }
    }

    private void StartContextCapture(DetectedEvent detectedEvent)
    {
        Interlocked.Increment(ref _contextCapturesStartedCount);
        var contextSettings = Volatile.Read(ref _contextSettings);

        var capture = new EventContextCapture(
            detectedEvent,
            detectedEvent.BeforeContextLines,
            contextSettings.BeforeContextLines,
            contextSettings.AfterContextLines);

        if (capture.IsComplete)
        {
            QueueCompletedContextCapture(capture);
            return;
        }

        if (_pendingContextCaptures.Count >= MaxPendingContextCaptures)
        {
            Interlocked.Increment(ref _contextCaptureDroppedCount);
            ReportError("Too many pending event context captures. Dropped event context capture.");
            return;
        }

        _pendingContextCaptures.Add(capture);
        SetActivePendingContextCount();
        RaiseStatusChanged();
    }

    private void CompletePendingContextCaptures()
    {
        if (_pendingContextCaptures.Count == 0)
        {
            SetActivePendingContextCount();
            return;
        }

        foreach (var capture in _pendingContextCaptures)
        {
            QueueCompletedContextCapture(capture);
        }

        _pendingContextCaptures.Clear();
        SetActivePendingContextCount();
    }

    private void QueueCompletedContextCapture(EventContextCapture capture)
    {
        QueueCompletedContextUpdate(capture);

        if (!EventLogWritingEnabled)
        {
            Interlocked.Increment(ref _contextCapturesCompletedCount);
            RaiseStatusChanged();
            return;
        }

        if (_eventLogQueue.Writer.TryWrite(capture))
        {
            Interlocked.Increment(ref _contextCapturesCompletedCount);
            RaiseStatusChanged();
            return;
        }

        Interlocked.Increment(ref _contextCaptureFailedCount);
        ReportError("Event context log queue is full. Dropped event context log entry.");
    }

    private void QueueCompletedContextUpdate(EventContextCapture capture)
    {
        var context = capture.ToDetectedEventContext();
        if (_completedContexts.Writer.TryWrite(context))
        {
            return;
        }

        Interlocked.Increment(ref _contextCaptureFailedCount);
        ReportError("Event context UI queue is full. Dropped event context UI update.");
    }

    private async Task ProcessEventLogAsync(CancellationToken cancellationToken)
    {
        StreamWriter? writer = null;
        var currentDate = string.Empty;
        var currentLogIdentity = string.Empty;
        var writtenSinceFlush = 0;
        var lastFlush = DateTimeOffset.UtcNow;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                while (_eventLogQueue.Reader.TryRead(out var contextCapture))
                {
                    if (!EventLogWritingEnabled)
                    {
                        continue;
                    }

                    var dateText = contextCapture.Event.Timestamp.LocalDateTime.ToString("yyyy-MM-dd");
                    var naming = GetLogFileNamingSnapshot();
                    var lineLogIdentity = CreateEventLogFileIdentity(dateText, naming);
                    var rotationRequested = ConsumeRotationRequest();
                    if (writer is null ||
                        rotationRequested ||
                        !string.Equals(currentDate, dateText, StringComparison.Ordinal) ||
                        !string.Equals(currentLogIdentity, lineLogIdentity, StringComparison.Ordinal))
                    {
                        await FlushAndDisposeAsync(writer);
                        writer = null;

                        currentDate = dateText;
                        currentLogIdentity = lineLogIdentity;
                        Directory.CreateDirectory(LogDirectorySnapshot());
                        var path = CreateEventLogFilePath(dateText, naming);
                        writer = CreateWriter(path);
                        SetCurrentEventLogFilePath(path);
                        writtenSinceFlush = 0;
                        lastFlush = DateTimeOffset.UtcNow;
                    }

                    await WriteEventContextAsync(writer!, contextCapture);
                    Interlocked.Increment(ref _eventLogWrittenCount);
                    writtenSinceFlush++;

                    if (writtenSinceFlush >= FlushLineInterval || DateTimeOffset.UtcNow - lastFlush >= FlushTimeInterval)
                    {
                        await writer.FlushAsync();
                        writtenSinceFlush = 0;
                        lastFlush = DateTimeOffset.UtcNow;
                        RaiseStatusChanged();
                    }
                }

                if (ConsumeEventLogCloseRequest())
                {
                    await FlushAndDisposeAsync(writer);
                    writer = null;
                    currentDate = string.Empty;
                    currentLogIdentity = string.Empty;
                    writtenSinceFlush = 0;
                    lastFlush = DateTimeOffset.UtcNow;
                    SetCurrentEventLogFilePath(null);
                    RaiseStatusChanged();
                }

                using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                waitCancellation.CancelAfter(TimeSpan.FromMilliseconds(250));
                try
                {
                    if (!await _eventLogQueue.Reader.WaitToReadAsync(waitCancellation.Token))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ReportError($"Event log writer failed: {ex.Message}");
        }
        finally
        {
            await FlushAndDisposeAsync(writer);
            SetCurrentEventLogFilePath(null);
            RaiseStatusChanged();
        }
    }

    private static async Task WriteEventContextAsync(StreamWriter writer, EventContextCapture contextCapture)
    {
        var detectedEvent = contextCapture.Event;
        await writer.WriteLineAsync("===== EVENT START =====");
        await writer.WriteLineAsync($"Event Time: {FormatTimestamp(detectedEvent.Timestamp)}");
        await writer.WriteLineAsync($"Rule: {detectedEvent.RuleName}");
        await writer.WriteLineAsync($"Keyword: {detectedEvent.Keyword}");
        await writer.WriteLineAsync($"Direction: {FormatDirection(detectedEvent.Direction)}");
        await writer.WriteLineAsync($"Message: {detectedEvent.Message}");
        await writer.WriteLineAsync();

        await writer.WriteLineAsync($"--- BEFORE {contextCapture.BeforeContextLineLimit} LINES ---");
        foreach (var line in contextCapture.BeforeContextLines)
        {
            await writer.WriteLineAsync(line.Formatted);
        }

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("--- MATCHED LINE ---");
        await writer.WriteLineAsync(detectedEvent.SourceLogLine?.Formatted ?? detectedEvent.Formatted);

        await writer.WriteLineAsync();
        await writer.WriteLineAsync($"--- AFTER {contextCapture.AfterContextLineLimit} LINES ---");
        foreach (var line in contextCapture.AfterContextLines)
        {
            await writer.WriteLineAsync(line.Formatted);
        }

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("===== EVENT END =====");
        await writer.WriteLineAsync();
    }

    private void RecordDetectedEvent(DetectedEvent detectedEvent)
    {
        Interlocked.Increment(ref _detectedEventCount);
        lock (_stateGate)
        {
            _lastDetectedEventText = detectedEvent.Formatted;
        }
    }

    private void ReportError(string message)
    {
        Interlocked.Increment(ref _errorCount);
        lock (_stateGate)
        {
            _lastError = message;
        }

        SafeRaiseError(message);
        RaiseStatusChanged();
    }

    private void SetRunning(bool isRunning, bool clearLastError = false)
    {
        lock (_stateGate)
        {
            _isRunning = isRunning;
            if (clearLastError)
            {
                _lastError = null;
            }
        }

        RaiseStatusChanged();
    }

    private void SetCurrentEventLogFilePath(string? path)
    {
        lock (_stateGate)
        {
            _currentEventLogFilePath = path;
        }

        RaiseStatusChanged();
    }

    private string LogDirectorySnapshot()
    {
        lock (_stateGate)
        {
            return _logDirectory;
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
            lock (_stateGate)
            {
                _lastError = $"EventDetector StatusChanged subscriber failed: {ex.Message}";
            }
        }
    }

    private void SafeRaiseError(string message)
    {
        try
        {
            Error?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            lock (_stateGate)
            {
                _lastError = $"EventDetector Error subscriber failed: {ex.Message}";
            }
        }
    }

    private void SetActivePendingContextCount()
    {
        Volatile.Write(ref _activePendingContextCount, _pendingContextCaptures.Count);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static string FormatDirection(LogDirection direction)
    {
        return direction switch
        {
            LogDirection.Tx => "TX",
            LogDirection.Rx => "RX",
            _ => "SYS"
        };
    }

    private static string FormatTimestamp(DateTimeOffset timestamp)
    {
        return timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
    }

    private static EventContextSettings NormalizeContextSettings(EventContextSettings settings)
    {
        return new EventContextSettings
        {
            BeforeContextLines = Math.Clamp(settings.BeforeContextLines, 0, 1_000),
            AfterContextLines = Math.Clamp(settings.AfterContextLines, 0, 1_000)
        };
    }

    private static EventRule CloneEventRule(EventRule rule)
    {
        return new EventRule
        {
            Name = rule.Name,
            Keyword = rule.Keyword,
            Enabled = rule.Enabled,
            CaseSensitive = rule.CaseSensitive,
            MatchMode = rule.MatchMode,
            MatchDirection = rule.MatchDirection,
            HighlightColor = rule.HighlightColor
        };
    }

    private static string FormatRuleName(EventRule rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.Name))
        {
            return rule.Name.Trim();
        }

        return string.IsNullOrWhiteSpace(rule.Keyword)
            ? "(unnamed)"
            : rule.Keyword.Trim();
    }

    private static StreamWriter CreateWriter(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 32 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 32 * 1024);
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

    private static string CreateDefaultLogDirectory()
    {
        return Path.Combine(AppContext.BaseDirectory, "logs");
    }

    private string CreateEventLogFilePath(string dateText, LogFileNamingSnapshot naming)
    {
        var fileName = naming.UseSessionNameInFileName
            ? $"{dateText}_{naming.SessionFileTimeText}_{naming.SessionFileName}_events.log"
            : $"{dateText}_events.log";

        return Path.Combine(LogDirectorySnapshot(), fileName);
    }

    private static string CreateEventLogFileIdentity(string dateText, LogFileNamingSnapshot naming)
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

    private bool ConsumeEventLogCloseRequest()
    {
        return Interlocked.Exchange(ref _eventLogCloseRequested, 0) != 0;
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

    private static Channel<LogLine> CreateInputQueue()
    {
        return Channel.CreateBounded<LogLine>(new BoundedChannelOptions(InputQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    private static Channel<DetectedEvent> CreateEventQueue()
    {
        return Channel.CreateBounded<DetectedEvent>(new BoundedChannelOptions(OutputQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    }

    private static Channel<DetectedEventContext> CreateEventContextQueue()
    {
        return Channel.CreateBounded<DetectedEventContext>(new BoundedChannelOptions(OutputQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    }

    private static Channel<EventContextCapture> CreateEventLogQueue()
    {
        return Channel.CreateBounded<EventContextCapture>(new BoundedChannelOptions(EventLogQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    }

    private readonly record struct LogFileNamingSnapshot(
        bool UseSessionNameInFileName,
        string SessionFileName,
        string SessionFileTimeText);

    private sealed class EventContextCapture
    {
        private readonly List<LogLine> _afterContextLines;

        public EventContextCapture(
            DetectedEvent detectedEvent,
            IReadOnlyList<LogLine> beforeContextLines,
            int beforeContextLineLimit,
            int afterContextLineLimit)
        {
            Event = detectedEvent;
            BeforeContextLines = beforeContextLines;
            BeforeContextLineLimit = Math.Max(0, beforeContextLineLimit);
            AfterContextLineLimit = Math.Max(0, afterContextLineLimit);
            _afterContextLines = new List<LogLine>(AfterContextLineLimit);
        }

        public DetectedEvent Event { get; }

        public IReadOnlyList<LogLine> BeforeContextLines { get; }

        public IReadOnlyList<LogLine> AfterContextLines => _afterContextLines;

        public int BeforeContextLineLimit { get; }

        public int AfterContextLineLimit { get; }

        public bool IsComplete => _afterContextLines.Count >= AfterContextLineLimit;

        public void AddAfterLine(LogLine line)
        {
            if (IsComplete)
            {
                return;
            }

            _afterContextLines.Add(line);
        }

        public DetectedEventContext ToDetectedEventContext()
        {
            return new DetectedEventContext(
                Event,
                BeforeContextLines.ToArray(),
                _afterContextLines.ToArray(),
                BeforeContextLineLimit,
                AfterContextLineLimit,
                DateTimeOffset.Now);
        }
    }
}
