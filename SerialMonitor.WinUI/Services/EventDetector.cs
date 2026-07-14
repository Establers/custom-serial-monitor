using System.Threading.Channels;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed class EventDetector : IEventDetector
{
    private const int InputQueueCapacity = 100_000;
    private const int OutputQueueCapacity = 20_000;
    private const int MaxPendingContextCaptures = 1_000;
    private const int ContextCaptureOverloadHighWatermarkValue = 200;
    private const int ContextCaptureOverloadLowWatermarkValue = 100;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateGate = new();
    private Channel<LogLine> _input = CreateInputQueue();
    private Channel<DetectedEvent> _events = CreateEventQueue();
    private Channel<DetectedEventContext> _completedContexts = CreateEventContextQueue();
    private CancellationTokenSource? _cancellation;
    private Task? _detectorTask;
    private LogRuleMatcher.CompiledEventRule[] _rules = Array.Empty<LogRuleMatcher.CompiledEventRule>();
    private EventContextSettings _contextSettings = new();
    private readonly Queue<LogLine> _beforeContextBuffer = new();
    private readonly List<EventContextCapture> _pendingContextCaptures = new();
    private string? _lastDetectedEventText;
    private string? _lastError;
    private string? _lastRuleEvaluationError;
    private string? _lastHexRuleMatchName;
    private string? _lastHexRuleMatchBytesPreview;
    private long _detectedEventCount;
    private long _errorCount;
    private long _droppedInputLineCount;
    private int _pendingInputLineCount;
    private long _droppedOutputEventCount;
    private long _contextCapturesStartedCount;
    private long _contextCapturesCompletedCount;
    private long _contextCaptureDroppedCount;
    private long _contextCaptureFailedCount;
    private long _contextCaptureScanLineCount;
    private long _contextCaptureEntriesVisited;
    private int _maxContextCaptureScanCount;
    private long _contextCaptureOverloadSkippedCount;
    private long _contextCaptureOverloadTransitionCount;
    private long _lastContextCaptureOverloadTransitionUtcTicks;
    private long _ruleEvaluationErrorCount;
    private int _compiledTerminalRuleCount;
    private int _compiledHexRuleCount;
    private int _activeRuleMode = (int)LogRuleMatchMode.Terminal;
    private int _invalidCompiledRuleCount;
    private int _activePendingContextCount;
    private int _isContextCaptureOverloadActive;
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

    public int EventRuleCount => Volatile.Read(ref _rules).Length;

    public int CompiledTerminalRuleCount => Volatile.Read(ref _compiledTerminalRuleCount);

    public int CompiledHexRuleCount => Volatile.Read(ref _compiledHexRuleCount);

    public LogRuleMatchMode ActiveRuleMode =>
        Volatile.Read(ref _activeRuleMode) == (int)LogRuleMatchMode.Hex
            ? LogRuleMatchMode.Hex
            : LogRuleMatchMode.Terminal;

    public int InvalidCompiledRuleCount => Volatile.Read(ref _invalidCompiledRuleCount);

    public long DetectedEventCount => Interlocked.Read(ref _detectedEventCount);

    public long ErrorCount => Interlocked.Read(ref _errorCount);

    public long DroppedInputLineCount => Interlocked.Read(ref _droppedInputLineCount);

    public int PendingInputLineCount => Volatile.Read(ref _pendingInputLineCount);

    public long DroppedOutputEventCount => Interlocked.Read(ref _droppedOutputEventCount);

    public long ContextCapturesStartedCount => Interlocked.Read(ref _contextCapturesStartedCount);

    public long ContextCapturesCompletedCount => Interlocked.Read(ref _contextCapturesCompletedCount);

    public int ActivePendingContextCount => Volatile.Read(ref _activePendingContextCount);

    public long ContextCaptureDroppedCount => Interlocked.Read(ref _contextCaptureDroppedCount);

    public long ContextCaptureFailedCount => Interlocked.Read(ref _contextCaptureFailedCount);

    public long ContextCaptureScanLineCount => Interlocked.Read(ref _contextCaptureScanLineCount);

    public long ContextCaptureEntriesVisited => Interlocked.Read(ref _contextCaptureEntriesVisited);

    public int MaxContextCaptureScanCount => Volatile.Read(ref _maxContextCaptureScanCount);

    public bool IsContextCaptureOverloadActive => Volatile.Read(ref _isContextCaptureOverloadActive) != 0;

    public int ContextCaptureOverloadHighWatermark => ContextCaptureOverloadHighWatermarkValue;

    public int ContextCaptureOverloadLowWatermark => ContextCaptureOverloadLowWatermarkValue;

    public long ContextCaptureOverloadSkippedCount => Interlocked.Read(ref _contextCaptureOverloadSkippedCount);

    public long ContextCaptureOverloadTransitionCount => Interlocked.Read(ref _contextCaptureOverloadTransitionCount);

    public DateTimeOffset? LastContextCaptureOverloadTransitionTime
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastContextCaptureOverloadTransitionUtcTicks);
            return ticks <= 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

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

    public async Task StartAsync(
        IReadOnlyList<EventRule> rules,
        EventContextSettings contextSettings,
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
            _beforeContextBuffer.Clear();
            _pendingContextCaptures.Clear();
            Volatile.Write(ref _isContextCaptureOverloadActive, 0);
            SetActivePendingContextCount();
            _input = CreateInputQueue();
            Volatile.Write(ref _pendingInputLineCount, 0);
            _events = CreateEventQueue();
            _completedContexts = CreateEventContextQueue();
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            SetRunning(true, clearLastError: true);
            _detectorTask = Task.Run(() => ProcessEventsAsync(_cancellation.Token), CancellationToken.None);
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

        Interlocked.Increment(ref _pendingInputLineCount);
        if (_input.Writer.TryWrite(line))
        {
            return true;
        }

        Interlocked.Decrement(ref _pendingInputLineCount);
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
        Volatile.Write(ref _compiledTerminalRuleCount, snapshot.Count(rule => rule.IsTerminalRule));
        Volatile.Write(ref _compiledHexRuleCount, snapshot.Count(rule => rule.IsHexRule));
        Volatile.Write(ref _invalidCompiledRuleCount, snapshot.Count(rule => rule.IsInvalid));
        var invalidRule = snapshot.FirstOrDefault(rule => rule.IsInvalid);
        if (invalidRule?.CompileError is not null)
        {
            RecordRuleEvaluationError(invalidRule.CompileError);
        }

        RaiseStatusChanged();
    }

    public void UpdateRuleMode(LogRuleMatchMode mode)
    {
        var normalized = mode == LogRuleMatchMode.Hex
            ? LogRuleMatchMode.Hex
            : LogRuleMatchMode.Terminal;
        if (Interlocked.Exchange(ref _activeRuleMode, (int)normalized) != (int)normalized)
        {
            RaiseStatusChanged();
        }
    }

    public void UpdateContextSettings(EventContextSettings contextSettings)
    {
        ArgumentNullException.ThrowIfNull(contextSettings);

        Volatile.Write(ref _contextSettings, NormalizeContextSettings(contextSettings));
        RaiseStatusChanged();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed && _detectorTask is null)
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

        if (detectorTask is null)
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

        _events.Writer.TryComplete();
        _completedContexts.Writer.TryComplete();
        _cancellation?.Dispose();
        _cancellation = null;
        _detectorTask = null;
        SetRunning(false);
    }

    private async Task ProcessEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in _input.Reader.ReadAllAsync(cancellationToken))
            {
                Interlocked.Decrement(ref _pendingInputLineCount);
                CaptureAfterContext(line);

                var captureContextForNewEvents = !IsContextCaptureOverloadActive;
                foreach (var detectedEvent in Detect(line, captureContextForNewEvents))
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
            RaiseStatusChanged();
        }
    }

    private IEnumerable<DetectedEvent> Detect(LogLine line, bool captureContextForNewEvents)
    {
        var rules = Volatile.Read(ref _rules);
        if (rules.Length == 0)
        {
            yield break;
        }

        IReadOnlyList<LogLine>? beforeContext = null;
        var activeMode = ActiveRuleMode;
        foreach (var rule in rules)
        {
            bool isMatch;
            string? matchError;
            try
            {
                isMatch = LogRuleMatcher.IsMatch(line, rule, activeMode, out matchError);
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
                beforeContext ??= captureContextForNewEvents
                    ? _beforeContextBuffer.ToArray()
                    : Array.Empty<LogLine>();
                var sourceRule = rule.Rule;
                if (sourceRule.Mode == LogRuleMatchMode.Hex)
                {
                    RecordHexRuleMatch(sourceRule, line);
                }

                yield return new DetectedEvent(
                    DateTimeOffset.Now,
                    string.IsNullOrWhiteSpace(sourceRule.Name) ? sourceRule.Keyword : sourceRule.Name,
                    sourceRule.Keyword,
                    line.Direction,
                    line.DisplayText,
                    line,
                    beforeContext,
                    trayNotificationEnabled: sourceRule.TrayNotificationEnabled,
                    soundNotificationEnabled: sourceRule.SoundNotificationEnabled,
                    popupNotificationEnabled: sourceRule.PopupNotificationEnabled,
                    notificationCooldownSeconds: sourceRule.NotificationCooldownSeconds);
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
            UpdateContextCaptureOverloadState();
            return;
        }

        var changed = false;
        var pendingCount = _pendingContextCaptures.Count;
        Interlocked.Increment(ref _contextCaptureScanLineCount);
        Interlocked.Add(ref _contextCaptureEntriesVisited, pendingCount);
        UpdateMax(ref _maxContextCaptureScanCount, pendingCount);

        var writeIndex = 0;
        for (var readIndex = 0; readIndex < pendingCount; readIndex++)
        {
            var capture = _pendingContextCaptures[readIndex];
            capture.AddAfterLine(line);
            if (capture.IsComplete)
            {
                changed = true;
                QueueCompletedContextCapture(capture);
                continue;
            }

            if (writeIndex != readIndex)
            {
                _pendingContextCaptures[writeIndex] = capture;
            }

            writeIndex++;
        }

        if (writeIndex < pendingCount)
        {
            _pendingContextCaptures.RemoveRange(writeIndex, pendingCount - writeIndex);
        }

        if (changed)
        {
            SetActivePendingContextCount();
        }

        UpdateContextCaptureOverloadState();
    }

    private void StartContextCapture(DetectedEvent detectedEvent)
    {
        UpdateContextCaptureOverloadState();
        Interlocked.Increment(ref _contextCapturesStartedCount);
        if (IsContextCaptureOverloadActive)
        {
            Interlocked.Increment(ref _contextCaptureOverloadSkippedCount);
            QueueCompletedContextCapture(new EventContextCapture(
                detectedEvent,
                Array.Empty<LogLine>(),
                beforeContextLineLimit: 0,
                afterContextLineLimit: 0));
            return;
        }

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
        UpdateContextCaptureOverloadState();
        RaiseStatusChanged();
    }

    private void CompletePendingContextCaptures()
    {
        if (_pendingContextCaptures.Count == 0)
        {
            SetActivePendingContextCount();
            UpdateContextCaptureOverloadState();
            return;
        }

        foreach (var capture in _pendingContextCaptures)
        {
            QueueCompletedContextCapture(capture);
        }

        _pendingContextCaptures.Clear();
        SetActivePendingContextCount();
        UpdateContextCaptureOverloadState();
    }

    private void QueueCompletedContextCapture(EventContextCapture capture)
    {
        QueueCompletedContextUpdate(capture);
        Interlocked.Increment(ref _contextCapturesCompletedCount);
        RaiseStatusChanged();
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

    private void UpdateContextCaptureOverloadState()
    {
        var pendingCount = _pendingContextCaptures.Count;
        var currentlyActive = IsContextCaptureOverloadActive;
        var shouldBeActive = currentlyActive
            ? pendingCount > ContextCaptureOverloadLowWatermarkValue
            : pendingCount >= ContextCaptureOverloadHighWatermarkValue;
        if (shouldBeActive == currentlyActive)
        {
            return;
        }

        Volatile.Write(ref _isContextCaptureOverloadActive, shouldBeActive ? 1 : 0);
        Interlocked.Increment(ref _contextCaptureOverloadTransitionCount);
        Interlocked.Exchange(
            ref _lastContextCaptureOverloadTransitionUtcTicks,
            DateTimeOffset.UtcNow.UtcTicks);
        RaiseStatusChanged();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
            Mode = rule.Mode,
            MatchDirection = rule.MatchDirection,
            HighlightColor = rule.HighlightColor,
            TrayNotificationEnabled = rule.TrayNotificationEnabled,
            SoundNotificationEnabled = rule.SoundNotificationEnabled,
            PopupNotificationEnabled = rule.PopupNotificationEnabled,
            NotificationCooldownSeconds = rule.NotificationCooldownSeconds
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

    private static void UpdateMax(ref int target, int candidate)
    {
        var current = Volatile.Read(ref target);
        while (candidate > current)
        {
            var previous = Interlocked.CompareExchange(ref target, candidate, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }

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
