using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.UI.Dispatching;
using SerialMonitor.Core;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using Windows.ApplicationModel.DataTransfer;

namespace SerialMonitor.WinUI.ViewModels;

public sealed class XtermSearchRequest
{
    public XtermSearchRequest(long requestId, string searchText, bool isCaseSensitive, string direction, int? resultIndex = null)
    {
        RequestId = requestId;
        SearchText = searchText;
        IsCaseSensitive = isCaseSensitive;
        Direction = direction;
        ResultIndex = resultIndex;
    }

    public long RequestId { get; }

    public string SearchText { get; }

    public bool IsCaseSensitive { get; }

    public string Direction { get; }

    public int? ResultIndex { get; }
}

public sealed class ConnectFailureDialogRequest
{
    public ConnectFailureDialogRequest(string portName, int baudRate, string message)
    {
        PortName = portName;
        BaudRate = baudRate;
        Message = message;
    }

    public string PortName { get; }

    public int BaudRate { get; }

    public string Message { get; }
}

public sealed class EventNotificationRequest : EventArgs
{
    public EventNotificationRequest(
        string title,
        string message,
        int eventCount,
        bool showTray,
        bool playSound,
        bool showPopup)
    {
        Title = title;
        Message = message;
        EventCount = eventCount;
        ShowTray = showTray;
        PlaySound = playSound;
        ShowPopup = showPopup;
    }

    public string Title { get; }

    public string Message { get; }

    public int EventCount { get; }

    public bool ShowTray { get; }

    public bool PlaySound { get; }

    public bool ShowPopup { get; }
}

public sealed class VisibleSearchResult
{
    public VisibleSearchResult(
        int matchIndex,
        int visibleLineIndex,
        string timeText,
        string directionText,
        string messagePreview,
        string fullText)
    {
        MatchIndex = matchIndex;
        VisibleLineIndex = visibleLineIndex;
        TimeText = timeText;
        DirectionText = directionText;
        MessagePreview = messagePreview;
        FullText = fullText;
    }

    public int MatchIndex { get; }

    public int VisibleLineIndex { get; }

    public string TimeText { get; }

    public string DirectionText { get; }

    public string MessagePreview { get; }

    public string FullText { get; }
}

public sealed class VisibleLogFilterOption
{
    public const string AllKey = "__all";

    public VisibleLogFilterOption(string key, string displayName, HighlightRule? rule)
    {
        Key = key;
        DisplayName = displayName;
        Rule = rule;
    }

    public string Key { get; }

    public string DisplayName { get; }

    public HighlightRule? Rule { get; }

    public bool IsAll => Rule is null;

    public static VisibleLogFilterOption All() => new(AllKey, "ALL", null);

    public override string ToString() => DisplayName;
}

public sealed class MockGeneratorPatternOption
{
    public MockGeneratorPatternOption(MockGeneratorPattern pattern, string displayName)
    {
        Pattern = pattern;
        DisplayName = displayName;
    }

    public MockGeneratorPattern Pattern { get; }

    public string DisplayName { get; }

    public override string ToString() => DisplayName;
}

public sealed class TimestampDisplayFormatOption
{
    public TimestampDisplayFormatOption(TimestampDisplayFormat format, string displayName)
    {
        Format = format;
        DisplayName = displayName;
    }

    public TimestampDisplayFormat Format { get; }

    public string DisplayName { get; }

    public override string ToString() => DisplayName;
}

public sealed class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private const int MaxRetainedEventContexts = 5_000;
    private const int MaxVisibleSearchResults = 1_000;
    private const int MaxSearchHistoryCount = 50;
    private const int PendingVisualWarningThreshold = 5_000;
    private const long Boundary64KWarningThreshold = 65_000;
    private const int SmoothVisualRenderIntervalMs = 16;
    private const int EventRenderIntervalMs = 250;
    private const int EventUiMaxItemsPerTick = 250;
    private const int SmoothVisualAppendMaxLines = 40;
    private const int SmoothVisualAppendMaxChars = 32 * 1024;
    private const int BridgeVisualLogQueueCapacity = 4_096;
    private const int DefaultVisibleLogLines = 50_000;
    private const int MinVisibleLogLines = 1_000;
    private const int MaxVisibleLogLinesLimit = 500_000;
    private const int MinXtermScrollbackSize = 1_000;
    private const int MaxXtermScrollbackSizeLimit = 500_000;
    private const int MinHexGroupTimeoutMs = 1;
    private const int MaxHexGroupTimeoutMs = 5_000;
    private const int DefaultVisibleEventCount = UiSettings.FixedMaxVisibleEventCount;
    private const int MinMockStressLinesPerSecond = 1;
    private const int MaxMockStressLinesPerSecond = 50_000;
    private const int MinMockStressBurstSize = 1;
    private const int MaxMockStressBurstSize = 10_000;
    private const double MinCuteBackgroundOpacity = 0.02;
    private const double MaxCuteBackgroundOpacity = 0.50;
    private const long MinSizeRotationMegabytes = 1;
    private const long MaxSizeRotationMegabytes = 10 * 1024;
    private const long MinSizeRotationBytes = MinSizeRotationMegabytes * LogSettings.BytesPerMegabyte;
    private const long MaxSizeRotationBytes = MaxSizeRotationMegabytes * LogSettings.BytesPerMegabyte;
    private const long DiskWarningFreeBytes = 5L * 1024 * 1024 * 1024;
    private const long DiskErrorFreeBytes = 1L * 1024 * 1024 * 1024;
    private const string MockPortName = "MOCK";
    private const string MockPortDisplayName = "[TEST] MOCK";
    private static readonly TimeSpan ResourceSnapshotRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EventNotificationGroupingInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ViewPauseDrainTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AutomaticReceiveReconnectDebounce = TimeSpan.FromMilliseconds(150);
    private static readonly IReadOnlyList<string> CuteBackgroundOpacityOptionValues =
        new[] { "0.10", "0.20", "0.25", "0.30", "0.40", "0.50" };

    private readonly ISerialService _serialService;
    private readonly ILogPipeline _logPipeline;
    private readonly IFileLogWriter _fileLogWriter;
    private readonly IEventDetector _eventDetector;
    private readonly ISerialBridgeService _bridgeService;
    private readonly IBridgeLogProcessor _bridgeLogProcessor;
    private readonly IProfileService _profileService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly UiBatchDispatcher<LogLine> _logBatchDispatcher;
    private readonly UiBatchDispatcher<DetectedEvent> _eventBatchDispatcher;
    private readonly UiBatchDispatcher<DetectedEventContext> _eventContextBatchDispatcher;
    private readonly CoalescingAsyncOperation _portRefreshOperation;
    private readonly DispatcherQueueTimer _diagnosticsTimer;
    private readonly object _eventNotificationGate = new();
    private readonly Dictionary<string, PendingEventNotification> _pendingEventNotifications = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _eventNotificationCancellation = new();
    private readonly CancellationTokenSource _bridgeVisualLogCancellation = new();
    private readonly CancellationTokenSource _automaticReceiveReconnectCancellation = new();
    private readonly Channel<LogLine> _bridgeVisualLogQueue = CreateBridgeVisualLogQueue();
    private readonly SemaphoreSlim _connectionLifecycleGate = new(1, 1);
    private readonly object _viewPauseGate = new();
    private readonly ViewPauseStateMachine _viewPause = new();
    private readonly Dictionary<Guid, DetectedEventContext> _eventContextsById = new();
    private readonly Queue<Guid> _eventContextOrder = new();
    private CancellationTokenSource? _connectionCancellation;
    private Task? _observeLogsTask;
    private Task? _observeEventsTask;
    private Task? _observeBridgeVisualLogsTask;
    private Task? _observeBridgeProcessedLogsTask;
    private Task? _observeEventContextsTask;
    private Task? _automaticReceiveReconnectTask;
    private int _automaticReceiveReconnectWorkerRunning;
    private int _automaticReceiveReconnectRequestVersion;
    private int _bridgeStopForSerialDisconnectRunning;
    private long _pendingLogDropCount;
    private int _backgroundStatusSnapshotDirty;
    private string? _selectedPort;
    private long _portRefreshGeneration;
    private int _selectedBaudRate = 115200;
    private TxLineEndingMode _selectedTxLineEnding = TxLineEndingMode.Crlf;
    private bool _isConnected;
    private bool _isBusy;
    private bool _isXtermAppendBackpressureActive;
    private bool _isAutoScrollEnabled = true;
    private bool _isXtermReady;
    private SerialSettings _currentSerialSettings = new();
    private BridgeSettings _currentBridgeSettings = new();
    private string? _selectedBridgePort;
    private SerialSettings? _lastSuccessfulSerialSettings;
    private LogSettings _currentLogSettings = new();
    private UiSettings _currentUiSettings = new();
    private RxDisplayMode _appliedRxDisplayMode = RxDisplayMode.Terminal;
    private int _appliedHexGroupTimeoutMs = 10;
    private string _hexGroupTimeoutDraftText = "10";
    private EventContextSettings _currentEventContextSettings = new();
    private long _sentCommandCount;
    private long _txErrorCount;
    private long _markerCount;
    private long _markerInsertErrorCount;
    private long _xtermAppendedLineCount;
    private long _xtermAppendBatchCount;
    private long _xtermAppendErrorCount;
    private long _xtermPendingCharacterCount;
    private long _maxXtermPendingCharacterCount;
    private bool _isWindowMinimized;
    private bool _isVisualAppendSuspendedForMinimize;
    private bool _xtermNeedsFullRerenderAfterRestore;
    private string _lastWindowMinimizeTimeText = "(none)";
    private string _lastWindowRestoreTimeText = "(none)";
    private string _restoreRenderStartedTimeText = "(none)";
    private string _restoreRenderCompletedTimeText = "(none)";
    private string _restoreRenderDurationText = "(none)";
    private int _restoreRenderedLineCount;
    private string _lastRestoreRenderMode = "(none)";
    private long _restoreFullRerenderSuppressedCount;
    private long _windowActivationRerenderSuppressedCount;
    private long _lastRenderedSequenceId;
    private long _pendingVisualDeltaLineCount;
    private bool _isFullXtermRerenderInProgress;
    private string _lastFullXtermRerenderReason = "(none)";
    private int _lastFullXtermRerenderLineCount;
    private string _lastFullXtermRerenderDurationText = "(none)";
    private bool _lastFullXtermScrollRestoreAttempted;
    private string _lastFullXtermFinalScrollAction = "(none)";
    private long _suppressedIntermediateAutoScrollCount;
    private long _fullXtermRerenderRequestCount;
    private long _fullXtermRerenderCoalescedCount;
    private long _fullXtermRerenderCanceledCount;
    private long _lastFullXtermRerenderGeneration;
    private int _lastFullXtermClearCount;
    private int _lastFullXtermVisibilityToggleCount;
    private string _lastFullXtermRerenderError = string.Empty;
    private long _minimizedVisualCoalescedLineCount;
    private long _minimizedVisualCoalescedCharacterCount;
    private long _maxMinimizedVisualCoalescedLineCount;
    private long _maxMinimizedVisualCoalescedCharacterCount;
    private int _suspendedXtermPendingLineCount;
    private long _suspendedXtermPendingCharacterCount;
    private long _suspendedXtermQueueCollapseCount;
    private string _lastSuspendedXtermQueueCollapseReason = "(none)";
    private long _xtermCopyRequestCount;
    private long _xtermCopiedCharacterCount;
    private long _xtermCopyErrorCount;
    private long _xtermSearchRequestCount;
    private long _xtermSearchHitCount;
    private long _xtermSearchErrorCount;
    private long _xtermSearchRequestId;
    private int _lastVisualAppendLineCount;
    private int _maxVisualAppendLineCount;
    private long _visualAppendBatchCount;
    private long _maxVisualBacklogLineCount;
    private int _lastXtermAppendLineCount;
    private int _lastXtermAppendCharacterCount;
    private int _maxXtermAppendLineCount;
    private int _maxXtermAppendCharacterCount;
    private long _lastXtermAppendDurationMs;
    private long _maxXtermAppendDurationMs;
    private long _xtermBackpressureEventAutoScrollSuppressedCount;
    private long _xtermBackpressureAutoScrollSuppressedCount;
    private long _xtermBackpressureFullRerenderDeferredCount;
    private string _lastSentCommandText = string.Empty;
    private TxSendMode _lastTxMode = TxSendMode.Terminal;
    private string _lastTxRawInput = string.Empty;
    private int _lastTxByteCount;
    private string _lastTxHexParseError = string.Empty;
    private string _markerText = string.Empty;
    private string _lastMarkerText = string.Empty;
    private string _lastMarkerAction = "No marker inserted.";
    private string _lastMarkerError = string.Empty;
    private string _sessionName = string.Empty;
    private string _currentSessionName = string.Empty;
    private string _lastSessionAction = "No session set.";
    private string _lastSessionError = string.Empty;
    private DateTimeOffset? _sessionStartedTime;
    private long _sessionErrorCount;
    private string _lastTxError = string.Empty;
    private string _lastXtermAppendError = string.Empty;
    private string _lastXtermCopyError = string.Empty;
    private string _lastXtermSearchError = string.Empty;
    private string _searchText = string.Empty;
    private bool _isSearchCaseSensitive;
    private int _searchMatchCount;
    private int _currentSearchMatchIndex;
    private long _searchErrorCount;
    private long _searchResultBuildErrorCount;
    private long _searchResultJumpErrorCount;
    private long _searchResultsRebuildCount;
    private long _searchResultSelectionLostCount;
    private string _currentSearchMatchedLine = string.Empty;
    private string _lastSearchError = string.Empty;
    private string _searchResultStatusText = "Manual results. Enter search text.";
    private string _lastSearchResultBuildError = string.Empty;
    private string _lastSearchResultJumpError = string.Empty;
    private VisibleSearchResult? _selectedSearchResult;
    private bool _areSearchResultsStale;
    private string _lastSearchShortcutAction = "No search shortcut used.";
    private string _lastSearchShortcutSource = "(none)";
    private DateTimeOffset? _lastSearchShortcutTime;
    private long _searchShortcutErrorCount;
    private string _lastSearchShortcutError = string.Empty;
    private readonly List<string> _searchHistory = new();
    private int _searchHistoryCursor = -1;
    private string _searchHistoryDraft = string.Empty;
    private bool _isApplyingSearchHistory;
    private DetectedEvent? _selectedEvent;
    private EventRule? _selectedEventRule;
    private HighlightRule? _selectedHighlightRule;
    private LogRule? _selectedLogRule;
    private TxCommand? _selectedSavedCommand;
    private CommandSequence? _selectedCommandSequence;
    private CommandSequenceStep? _selectedCommandSequenceStep;
    private CancellationTokenSource? _sequenceCancellation;
    private bool _isSequenceRunning;
    private string _runningSequenceName = "(none)";
    private string _currentSequenceStepText = "(none)";
    private int _completedSequenceSteps;
    private string _lastSequenceActionStatus = "No sequence action yet.";
    private string _lastSequenceError = string.Empty;
    private long _sequenceRunCount;
    private long _sequenceStopCount;
    private long _sequenceErrorCount;
    private string _selectedEventContextText = "Select an event to view captured context.";
    private string _selectedEventContextStatusText = "No event selected";
    private bool _isEventAutoScrollEnabled = true;
    private int _selectedMockStressLinesPerSecond = 10;
    private int _selectedMockStressBurstSize = 1;
    private MockGeneratorPattern _selectedMockGeneratorPattern = MockGeneratorPattern.NormalLines;
    private bool _isMockStressEventInjectionEnabled = true;
    private bool _isMockStressInvalidByteInjectionEnabled;
    private long _mockExpectedSequence = 1;
    private long _mockLastParsedSequence;
    private long _mockMissingSequenceCount;
    private long _mockDuplicateSequenceCount;
    private long _mockOutOfOrderSequenceCount;
    private long _mockMalformedSequenceCount;
    private string _lastMockSequenceError = string.Empty;
    private long _copiedEventContextCount;
    private long _eventContextUiErrorCount;
    private long _eventSelectionErrorCount;
    private long _latestEventSelectCount;
    private long _eventListScrollErrorCount;
    private long _eventListIncrementalUpdateCount;
    private long _eventListResetCount;
    private long _eventSelectionPreservedCount;
    private long _eventSelectionLostCount;
    private long _listUpdateErrorCount;
    private long _inspectorTabLayoutErrorCount;
    private long _searchTabLayoutErrorCount;
    private long _contextRefreshCount;
    private long _contextRefreshErrorCount;
    private long _contextTabActivatedCount;
    private long _contextVisualRefreshCount;
    private long _contextRenderErrorCount;
    private long _xtermFitResizeCount;
    private long _xtermLayoutErrorCount;
    private int _lastAppliedXtermScrollbackSize;
    private long _ruleEditErrorCount;
    private long _commandEditErrorCount;
    private long _eventContextUiDroppedCount;
    private string _lastEventContextUiError = string.Empty;
    private string _lastEventSelectionError = string.Empty;
    private string _lastEventListScrollError = string.Empty;
    private string _lastListUpdateError = string.Empty;
    private string _activeInspectorTabText = "Events";
    private string _lastInspectorTabLayoutError = string.Empty;
    private string _lastSearchTabLayoutError = string.Empty;
    private string _lastContextRefreshError = string.Empty;
    private string _lastContextVisualRefreshTimeText = "(none)";
    private string _lastContextVisualRefreshEventId = "(none)";
    private string _lastContextVisualRefreshEventSummary = "(none)";
    private int _lastContextVisualRefreshTextLength;
    private string _lastContextRenderError = string.Empty;
    private bool _isContextWebViewReady;
    private long _contextWebViewUpdateCount;
    private long _contextWebViewUpdateErrorCount;
    private string _lastConnectRequestedPort = "(none)";
    private int _lastConnectRequestedBaud;
    private string _lastConnectResult = "No connect attempt yet.";
    private string _lastConnectFailureReason = string.Empty;
    private string _lastConnectExceptionType = string.Empty;
    private string _lastConnectFailureTimeText = "(none)";
    private string _selectedPortAfterConnectFailure = "(none)";
    private string _lastContextWebViewUpdateTimeText = "(none)";
    private string _lastContextWebViewUpdateEventSummary = "(none)";
    private int _lastContextWebViewTextLength;
    private string _lastContextWebViewUpdateError = string.Empty;
    private string _lastXtermLayoutError = string.Empty;
    private string _lastAutoScrollActionTimeText = "(none)";
    private string _lastAutoScrollError = string.Empty;
    private bool? _lastXtermAtBottom;
    private string _lastRuleEditStatus = "No rule edits yet.";
    private string _lastRuleEditError = string.Empty;
    private string _lastRuleColorChange = "No rule color changes yet.";
    private string _lastRuleColorChangeError = string.Empty;
    private long _ruleColorChangeErrorCount;
    private long _invalidRuleColorFallbackCount;
    private long _automaticRuleRerenderSuppressedCount;
    private long _ruleChangesSinceClearCount;
    private string _lastRuleChangeLiveOnlyTimeText = "(none)";
    private string _lastCommandEditStatus = "No command edits yet.";
    private string _lastCommandEditError = string.Empty;
    private DateTimeOffset? _lastSentCommandTime;
    private DateTimeOffset? _lastMarkerTime;
    private string _statusText = "Disconnected";
    private string _footerStatusText = "Ready";
    private string _diagnosticsSummaryText = "Diagnostics summary initializing...";
    private string _diagnosticsText = "Diagnostics initializing...";
    private string _lastBackgroundError = string.Empty;
    private bool _cuteBackgroundFileExists;
    private bool _cuteBackgroundLoaded;
    private string _cuteBackgroundSource = "none";
    private string _cuteBackgroundBundledPath = string.Empty;
    private string _cuteBackgroundLoadError = string.Empty;
    private string _cuteBackgroundLastAppliedTimeText = "(never)";
    private long _cuteBackgroundApplyCount;
    private long _cuteBackgroundImageReloadCount;
    private long _cuteBackgroundSkippedUnchangedCount;
    private string _lastLogFileActionStatus = "No log file actions yet.";
    private string _lastLogFileActionError = string.Empty;
    private long _logFileActionErrorCount;
    private string _lastSaveDirectoryAction = "No save directory browse yet.";
    private string _lastSaveDirectoryError = string.Empty;
    private long _saveDirectoryBrowseErrorCount;
    private string _lastLogToggleAction = "Log saving is OFF.";
    private string _lastLogToggleTimeText = "(none)";
    private string _lastLogToggleError = string.Empty;
    private int _fileLoggingTransitionCount;
    private long _logToggleErrorCount;
    private string _lastSessionFileAction = "Log file naming idle.";
    private string _lastSessionFileNamingError = string.Empty;
    private long _sessionFileNamingErrorCount;
    private long _settingsApplyErrorCount;
    private string _lastSettingsApplyError = string.Empty;
    private long _settingsValidationErrorCount;
    private string _lastSettingsValidationError = string.Empty;
    private string _lastNormalizedSetting = "(none)";
    private string _lastSettingsChange = "No settings changes yet.";
    private string _lastSettingsApplyStatus = "No settings changes yet.";
    private readonly HashSet<string> _pendingReconnectSettings = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingRestartSettings = new(StringComparer.Ordinal);
    private bool _suppressSettingsApplyRecording;
    private long _statusChangedThreadMarshalErrorCount;
    private string _lastStatusChangedThreadMarshalError = string.Empty;
    private int _duplicateMockPortEntryCount;
    private bool _lastPortRefreshIncludedMock;
    private readonly HashSet<string> _lastAvailableActualPorts = new(StringComparer.OrdinalIgnoreCase);
    private string _lastSuccessfulPort = "(none)";
    private int _lastSuccessfulBaudRate;
    private string _lastPortSelectionChangeReason = "No port selection change yet.";
    private bool _lastDisconnectPreservedPort;
    private string _lastPortRefreshResult = "No port refresh yet.";
    private bool _selectedPortAvailable;
    private long _fileWriterDroppedHealthBaseline;
    private long _fileWriterErrorHealthBaseline;
    private string _healthStateText = "HEALTH OK";
    private string _healthReasonSummary = "No health issues.";
    private string _healthReasonsText = "No health issues.";
    private string _lastHealthUpdateTimeText = "(none)";
    private int _healthWarningCount;
    private int _healthErrorCount;
    private long _lastHealthObservedDecodeErrorCount;
    private bool _hasHealthDecodeBaseline;
    private long _diskFreeBytes;
    private long _diskTotalBytes;
    private long _currentSessionLogSizeBytes;
    private long _processWorkingSetBytes;
    private long _lastResourceSnapshotUtcTicks;
    private int _resourceSnapshotRefreshInProgress;
    private bool _hasResourceSnapshot;
    private string _resourceSnapshotError = string.Empty;
    private long _eventNotificationBatchCount;
    private long _eventNotificationEventCount;
    private long _bridgeVisualLogDroppedCount;
    private int _bridgeVisualLogPendingCount;
    private int _shutdownStarted;
    private string _lastShutdownStartTimeText = "(none)";
    private string _lastShutdownCompletedTimeText = "(none)";
    private string _shutdownCleanupResult = "Shutdown has not run.";
    private string _shutdownDisconnectError = string.Empty;
    private string _shutdownFileFlushError = string.Empty;
    private string _shutdownProfileSaveError = string.Empty;
    private bool _wasSequenceRunningDuringShutdown;
    private bool _wasSerialConnectedDuringShutdown;
    private string _lastXtermContextMenuAction = "No xterm context menu action yet.";
    private string _lastXtermContextMenuError = string.Empty;
    private long _xtermContextMenuErrorCount;
    private int _lastCopyVisibleLineCount;
    private string _lastCopySinceTxActionTimeText = "(none)";
    private int _lastCopySinceTxLineCount;
    private int _lastCopySinceTxCharacterCount;
    private string _lastCopySinceTxResult = "No copy since TX action yet.";
    private string _lastCopySinceTxError = string.Empty;
    private long _copySinceTxErrorCount;
    private string _lastCopySinceMarkActionTimeText = "(none)";
    private int _lastCopySinceMarkLineCount;
    private int _lastCopySinceMarkCharacterCount;
    private string _lastCopySinceMarkResult = "No copy since MARK action yet.";
    private string _lastCopySinceMarkError = string.Empty;
    private long _copySinceMarkErrorCount;
    private int _lastSearchSelectedTextLength;
    private string _lastDisconnectConfirmationResult = "skipped";
    private string _lastDisconnectConfirmationError = string.Empty;
    private long _disconnectConfirmationErrorCount;
    private string _lastTimestampDisplayModeChangeTimeText = "(none)";
    private string _lastTimestampDisplayModeError = string.Empty;
    private long _timestampDisplayModeErrorCount;
    private VisibleLogFilterOption? _selectedViewFilterOption;
    private bool _isRefreshingVisibleLogFilterOptions;
    private string _lastVisibleCapChangeTimeText = "(none)";
    private string _lastVisibleFilterChangeTimeText = "(none)";
    private string _lastVisibleFilterError = string.Empty;
    private string _lastVisibleLogRebuildReason = "full re-render";
    private long _visibleFilterErrorCount;

    private enum SettingsApplyBehavior
    {
        Immediate,
        AutomaticReconnect,
        ReconnectRequired,
        NextSession,
        ProfileOnly
    }

    private enum ConnectFailureKind
    {
        AccessDenied,
        PortNotFound,
        OpenFailed,
        Unknown
    }

    private readonly record struct ResourceSnapshot(
        long DiskFreeBytes,
        long DiskTotalBytes,
        long CurrentSessionLogSizeBytes,
        long ProcessWorkingSetBytes,
        string Error);

    private sealed class PendingEventNotification
    {
        public DetectedEvent? LatestEvent { get; set; }

        public int EventCount { get; set; }

        public bool IsScheduled { get; set; }

        public DateTimeOffset LastNotificationTime { get; set; } = DateTimeOffset.MinValue;

        public bool ShowTray { get; set; }

        public bool PlaySound { get; set; }

        public bool ShowPopup { get; set; }

        public int CooldownSeconds { get; set; } = 30;
    }

    public MainViewModel(
        ISerialService serialService,
        ILogPipeline logPipeline,
        IFileLogWriter fileLogWriter,
        IEventDetector eventDetector,
        ISerialBridgeService bridgeService,
        IBridgeLogProcessor bridgeLogProcessor,
        IProfileService profileService,
        DispatcherQueue dispatcherQueue)
    {
        _serialService = serialService;
        _logPipeline = logPipeline;
        _fileLogWriter = fileLogWriter;
        _eventDetector = eventDetector;
        _bridgeService = bridgeService;
        _bridgeLogProcessor = bridgeLogProcessor;
        _profileService = profileService;
        _dispatcherQueue = dispatcherQueue;
        _portRefreshOperation = new CoalescingAsyncOperation(RefreshPortsOnceAsync);

        var profile = profileService.CreateDefaultProfile();

        _logBatchDispatcher = new UiBatchDispatcher<LogLine>(
            dispatcherQueue,
            TimeSpan.FromMilliseconds(SmoothVisualRenderIntervalMs),
            ApplyLogRenderBatch,
            maxPendingItems: 10_000,
            maxItemsPerTick: SmoothVisualAppendMaxLines,
            dropOldestWhenFull: true,
            catchUpMaxItemsPerTick: 400,
            catchUpPendingThreshold: 1_000);

        _eventBatchDispatcher = new UiBatchDispatcher<DetectedEvent>(
            dispatcherQueue,
            TimeSpan.FromMilliseconds(EventRenderIntervalMs),
            ApplyEventRenderBatch,
            maxPendingItems: 5_000,
            maxItemsPerTick: EventUiMaxItemsPerTick);

        _eventContextBatchDispatcher = new UiBatchDispatcher<DetectedEventContext>(
            dispatcherQueue,
            TimeSpan.FromMilliseconds(EventRenderIntervalMs),
            ApplyEventContextBatch,
            maxPendingItems: 5_000,
            maxItemsPerTick: EventUiMaxItemsPerTick);

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => CanConnect);
        ToggleConnectionCommand = new AsyncRelayCommand(ToggleConnectionAsync, () => !IsBusy && (IsConnected || CanConnect));
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnected && !IsBusy);
        SendCommand = new AsyncRelayCommand(SendCurrentCommandAsync, CanSendCurrentCommand);
        SendSavedCommandCommand = new AsyncRelayCommand(
            SendSavedCommandAsync,
            parameter => IsConnected && !IsBusy && !IsManualTxBusy && parameter is TxCommand);
        AddMarkerCommand = new AsyncRelayCommand(AddMarkerAsync, () => IsConnected && !IsBusy);
        AddDefaultMarkerCommand = new AsyncRelayCommand(AddDefaultMarkerAsync, () => IsConnected && !IsBusy);
        ToggleLogRenderingPauseCommand = new AsyncRelayCommand(ToggleLogRenderingPauseAsync);
        ClearScreenCommand = new AsyncRelayCommand(ClearScreenAsync);
        CopyDiagnosticsCommand = new AsyncRelayCommand(CopyDiagnosticsAsync);
        CopyHelpCommand = new AsyncRelayCommand(CopyHelpAsync);
        OpenLogFolderCommand = new AsyncRelayCommand(OpenLogFolderAsync);
        OpenCurrentSerialLogCommand = new AsyncRelayCommand(OpenCurrentSerialLogAsync, CanOpenCurrentSerialLog);
        CopySerialLogPathCommand = new AsyncRelayCommand(CopySerialLogPathAsync, CanUseCurrentSerialLogPath);
        ToggleFileLoggingCommand = new AsyncRelayCommand(ToggleFileLoggingAsync, () => !IsBusy);
        SaveProfileCommand = new AsyncRelayCommand(SaveProfileAsync, () => !IsBusy);
        LoadProfileCommand = new AsyncRelayCommand(LoadProfileAsync, () => !IsBusy && !IsConnected);
        ResetProfileCommand = new AsyncRelayCommand(ResetProfileAsync, () => !IsBusy && !IsConnected);
        ResetCuteBackgroundCommand = new AsyncRelayCommand(ResetCuteBackgroundAsync);
        FindNextCommand = new AsyncRelayCommand(FindNextSearchMatchAsync, CanSearch);
        FindPreviousCommand = new AsyncRelayCommand(FindPreviousSearchMatchAsync, CanSearch);
        RefreshSearchResultsCommand = new AsyncRelayCommand(RefreshSearchResultsAsync, CanSearch);
        CopyEventContextCommand = new AsyncRelayCommand(CopyEventContextAsync, CanCopyEventContext);
        SelectLatestEventCommand = new AsyncRelayCommand(SelectLatestEventAsync, CanSelectLatestEvent);
        StartMockStressCommand = new AsyncRelayCommand(StartMockStressAsync, () => CanStartMockStress);
        StopMockStressCommand = new AsyncRelayCommand(StopMockStressAsync, () => CanStopMockStress);
        ResetMockStressCountersCommand = new AsyncRelayCommand(ResetMockStressCountersAsync);
        SendMockCrlfCommand = new AsyncRelayCommand(SendMockCrlfAsync, () => IsConnected && CurrentPortIsMock);
        RunCommandSequenceCommand = new AsyncRelayCommand(RunSelectedCommandSequenceAsync, CanRunSelectedCommandSequence);
        StopCommandSequenceCommand = new AsyncRelayCommand(StopCommandSequenceAsync, () => IsSequenceRunning);
        SetSessionCommand = new AsyncRelayCommand(SetSessionAsync, () => !IsBusy && !FileLoggingEnabled);
        EndSessionCommand = new AsyncRelayCommand(EndSessionAsync, () => !IsBusy && !FileLoggingEnabled && !string.IsNullOrWhiteSpace(CurrentSessionName));
        RefreshBridgePortsCommand = new AsyncRelayCommand(RefreshBridgePortsAsync);
        StartBridgeCommand = new AsyncRelayCommand(StartBridgeAsync, () => CanStartBridge);
        StopBridgeCommand = new AsyncRelayCommand(StopBridgeAsync, () => CanStopBridge);

        ApplyProfile(profile);

        Commands.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CommandViewModel.CurrentCommandText))
            {
                SendCommand.NotifyCanExecuteChanged();
                return;
            }

            if (args.PropertyName == nameof(CommandViewModel.CommandHistoryCount) ||
                args.PropertyName == nameof(CommandViewModel.LastHistoryCommand) ||
                args.PropertyName == nameof(CommandViewModel.LastHistoryUpdateTimeText) ||
                args.PropertyName == nameof(CommandViewModel.HistoryErrorCount) ||
                args.PropertyName == nameof(CommandViewModel.LastHistoryError))
            {
                RefreshDiagnostics();
            }
        };

        _serialService.Error += OnBackgroundError;
        _serialService.StatusChanged += OnSerialStatusChanged;
        _logPipeline.Error += OnBackgroundError;
        _logPipeline.StatusChanged += OnPipelineStatusChanged;
        _fileLogWriter.Error += OnBackgroundError;
        _fileLogWriter.StatusChanged += OnFileLogStatusChanged;
        _eventDetector.Error += OnBackgroundError;
        _eventDetector.StatusChanged += OnEventDetectorStatusChanged;
        _serialService.RawBytesReceived += OnRawBytesReceived;
        _bridgeService.Error += OnBackgroundError;
        _bridgeService.StatusChanged += OnBridgeStatusChanged;
        _bridgeService.ManualTxStateChanged += OnManualTxStateChanged;
        _bridgeLogProcessor.Error += OnBackgroundError;
        _observeBridgeProcessedLogsTask = Task.Run(
            () => ObserveBridgeProcessedLogsAsync(_bridgeVisualLogCancellation.Token),
            CancellationToken.None);
        _observeBridgeVisualLogsTask = Task.Run(
            () => ObserveBridgeVisualLogsAsync(_bridgeVisualLogCancellation.Token),
            CancellationToken.None);

        _diagnosticsTimer = dispatcherQueue.CreateTimer();
        _diagnosticsTimer.Interval = TimeSpan.FromSeconds(1);
        _diagnosticsTimer.Tick += OnDiagnosticsTimerTick;
        _diagnosticsTimer.Start();
        RefreshDiagnostics();

        _ = LoadStartupProfileAsync();
        _ = RefreshPortsAsync();
    }

    public event EventHandler<XtermSearchRequest>? XtermSearchRequested;

    public event EventHandler<ConnectFailureDialogRequest>? ConnectFailureDialogRequested;

    public event EventHandler<EventNotificationRequest>? EventNotificationRequested;

    public event Func<CancellationToken, Task<bool>>? ViewPauseDrainRequested;

    public ObservableCollection<string> PortNames { get; } = new();

    public ObservableCollection<string> BridgePortNames { get; } = new();

    public ObservableCollection<int> BaudRates { get; } = new()
    {
        1200,
        4800,
        9600,
        19200,
        38400,
        57600,
        115200,
        230400,
        460800,
        921600
    };

    public ObservableCollection<int> DataBitOptions { get; } = new()
    {
        5,
        6,
        7,
        8
    };

    public ObservableCollection<SerialParityMode> ParityModes { get; } = new()
    {
        SerialParityMode.None,
        SerialParityMode.Odd,
        SerialParityMode.Even,
        SerialParityMode.Mark,
        SerialParityMode.Space
    };

    public ObservableCollection<SerialStopBitsMode> StopBitsModes { get; } = new()
    {
        SerialStopBitsMode.One,
        SerialStopBitsMode.OnePointFive,
        SerialStopBitsMode.Two
    };

    public ObservableCollection<SerialHandshakeMode> HandshakeModes { get; } = new()
    {
        SerialHandshakeMode.None,
        SerialHandshakeMode.XOn,
        SerialHandshakeMode.Rts,
        SerialHandshakeMode.Dtr,
        SerialHandshakeMode.RtsXOn,
        SerialHandshakeMode.DtrXOn,
        SerialHandshakeMode.DtrRts,
        SerialHandshakeMode.DtrRtsXOn
    };

    public ObservableCollection<RxLineEndingMode> RxLineEndingModes { get; } = new()
    {
        RxLineEndingMode.Auto,
        RxLineEndingMode.Cr,
        RxLineEndingMode.Lf,
        RxLineEndingMode.Crlf
    };

    public ObservableCollection<TxLineEndingMode> TxLineEndingModes { get; } = new()
    {
        TxLineEndingMode.None,
        TxLineEndingMode.Cr,
        TxLineEndingMode.Lf,
        TxLineEndingMode.Crlf
    };

    public ObservableCollection<RxEncodingMode> RxEncodingModes { get; } = new()
    {
        RxEncodingMode.Ascii,
        RxEncodingMode.Utf8,
        RxEncodingMode.Cp949,
        RxEncodingMode.Hex
    };

    public ObservableCollection<RxDisplayMode> RxDisplayModes { get; } = new()
    {
        RxDisplayMode.Terminal,
        RxDisplayMode.Hex
    };

    public ObservableCollection<TxSendMode> TxSendModes { get; } = new()
    {
        TxSendMode.Terminal,
        TxSendMode.Hex
    };

    public ObservableCollection<int> VisibleLogLineCapOptions { get; } = new()
    {
        10_000,
        50_000,
        100_000,
        200_000,
        500_000
    };

    public ObservableCollection<int> MockStressLineRatePresets { get; } = new()
    {
        10,
        50,
        100,
        250,
        500,
        1000,
        2000,
        5000
    };

    public ObservableCollection<int> MockStressBurstSizePresets { get; } = new()
    {
        1,
        5,
        10,
        25,
        50,
        100
    };

    public ObservableCollection<MockGeneratorPatternOption> MockGeneratorPatternOptions { get; } = new()
    {
        new(MockGeneratorPattern.NormalLines, "Normal Lines"),
        new(MockGeneratorPattern.NoNewlineZzz, "No-Newline zzz"),
        new(MockGeneratorPattern.NoNewlineZzzBurst, "No-Newline zzz burst"),
        new(MockGeneratorPattern.VisualHexPackets, "Visual HEX 3-5 ms")
    };

    public LogViewModel Log { get; } = new(DefaultVisibleLogLines);

    public EventViewModel Events { get; } = new(DefaultVisibleEventCount);

    public ObservableCollection<VisibleSearchResult> SearchResults { get; } = new();

    public ObservableCollection<EventRule> EventRules { get; } = new();

    public ObservableCollection<HighlightRule> HighlightRules { get; } = new();

    public ObservableCollection<LogRule> LogRules { get; } = new();

    public ObservableCollection<VisibleLogFilterOption> VisibleLogFilterOptions { get; } = new();

    public ObservableCollection<CommandSequence> CommandSequences { get; } = new();

    public ObservableCollection<string> HighlightColorPresets { get; } = new()
    {
        "Default",
        "Red",
        "Orange",
        "Yellow",
        "Green",
        "Cyan",
        "Blue",
        "Magenta",
        "White",
        "Gray"
    };

    public CommandViewModel Commands { get; } = new();

    public EventRule? SelectedEventRule
    {
        get => _selectedEventRule;
        set
        {
            if (SetProperty(ref _selectedEventRule, value))
            {
                OnPropertyChanged(nameof(HasSelectedEventRule));
            }
        }
    }

    public bool HasSelectedEventRule => SelectedEventRule is not null;

    public HighlightRule? SelectedHighlightRule
    {
        get => _selectedHighlightRule;
        set
        {
            if (SetProperty(ref _selectedHighlightRule, value))
            {
                OnPropertyChanged(nameof(HasSelectedHighlightRule));
            }
        }
    }

    public bool HasSelectedHighlightRule => SelectedHighlightRule is not null;

    public LogRule? SelectedLogRule
    {
        get => _selectedLogRule;
        set
        {
            if (SetProperty(ref _selectedLogRule, value))
            {
                OnPropertyChanged(nameof(HasSelectedLogRule));
            }
        }
    }

    public bool HasSelectedLogRule => SelectedLogRule is not null;

    public VisibleLogFilterOption? SelectedViewFilterOption
    {
        get => _selectedViewFilterOption;
        set
        {
            if (SetProperty(ref _selectedViewFilterOption, value))
            {
                if (!_isRefreshingVisibleLogFilterOptions)
                {
                    ApplySelectedVisibleLogFilter(recordStatus: true);
                }
            }
        }
    }

    public string CurrentVisibleFilterText => SelectedViewFilterOption?.DisplayName ?? "ALL";

    public int AvailableViewFilterCount => Math.Max(0, VisibleLogFilterOptions.Count - 1);

    public string LastVisibleFilterChangeTimeText => _lastVisibleFilterChangeTimeText;

    public string LastVisibleCapChangeTimeText => _lastVisibleCapChangeTimeText;

    public string LastVisibleLogRebuildReason => _lastVisibleLogRebuildReason;

    public long VisibleFilterErrorCount => Interlocked.Read(ref _visibleFilterErrorCount);

    public string LastVisibleFilterError => _lastVisibleFilterError;

    public TxCommand? SelectedSavedCommand
    {
        get => _selectedSavedCommand;
        set
        {
            if (SetProperty(ref _selectedSavedCommand, value))
            {
                OnPropertyChanged(nameof(HasSelectedSavedCommand));
            }
        }
    }

    public bool HasSelectedSavedCommand => SelectedSavedCommand is not null;

    public CommandSequence? SelectedCommandSequence
    {
        get => _selectedCommandSequence;
        set
        {
            if (SetProperty(ref _selectedCommandSequence, value))
            {
                SelectedCommandSequenceStep = value?.Steps.FirstOrDefault();
                OnPropertyChanged(nameof(HasSelectedCommandSequence));
                OnPropertyChanged(nameof(CanEditSelectedCommandSequence));
                OnPropertyChanged(nameof(SelectedCommandSequenceStepCount));
                OnPropertyChanged(nameof(SelectedCommandSequenceStepCountText));
                OnPropertyChanged(nameof(SelectedCommandSequenceName));
                OnPropertyChanged(nameof(SequenceStatusText));
                OnPropertyChanged(nameof(SequenceRuntimeStateText));
                OnPropertyChanged(nameof(SequenceCompletedStepsText));
                RefreshSelectedCommandSequenceStepNumbers();
                RunCommandSequenceCommand.NotifyCanExecuteChanged();
                RefreshDiagnostics();
            }
        }
    }

    public bool HasSelectedCommandSequence => SelectedCommandSequence is not null;

    public bool CanEditCommandSequences => !IsSequenceRunning && !IsBusy;

    public bool CanEditSelectedCommandSequence => CanEditCommandSequences && HasSelectedCommandSequence;

    public CommandSequenceStep? SelectedCommandSequenceStep
    {
        get => _selectedCommandSequenceStep;
        set
        {
            if (SetProperty(ref _selectedCommandSequenceStep, value))
            {
                OnPropertyChanged(nameof(HasSelectedCommandSequenceStep));
                OnPropertyChanged(nameof(CanEditSelectedCommandSequenceStep));
            }
        }
    }

    public bool HasSelectedCommandSequenceStep => SelectedCommandSequenceStep is not null;

    public bool CanEditSelectedCommandSequenceStep => CanEditCommandSequences && HasSelectedCommandSequenceStep;

    public int SelectedCommandSequenceStepCount => SelectedCommandSequence?.Steps.Count ?? 0;

    public string SelectedCommandSequenceStepCountText => SelectedCommandSequenceStepCount == 0
        ? "No steps."
        : $"{SelectedCommandSequenceStepCount:N0} steps";

    public string SelectedCommandSequenceName => SelectedCommandSequence?.Name ?? "(none)";

    public bool IsSequenceRunning
    {
        get => _isSequenceRunning;
        private set
        {
            if (SetProperty(ref _isSequenceRunning, value))
            {
                OnPropertyChanged(nameof(SequenceStatusText));
                OnPropertyChanged(nameof(SequenceRuntimeStateText));
                OnPropertyChanged(nameof(SequenceCurrentStepDisplayText));
                OnPropertyChanged(nameof(CanStartBridge));
                OnPropertyChanged(nameof(CanEditCommandSequences));
                OnPropertyChanged(nameof(CanEditSelectedCommandSequence));
                OnPropertyChanged(nameof(CanEditSelectedCommandSequenceStep));
                RunCommandSequenceCommand.NotifyCanExecuteChanged();
                StopCommandSequenceCommand.NotifyCanExecuteChanged();
                StartBridgeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string RunningSequenceName
    {
        get => _runningSequenceName;
        private set
        {
            if (SetProperty(ref _runningSequenceName, value))
            {
                OnPropertyChanged(nameof(SequenceStatusText));
                OnPropertyChanged(nameof(SequenceCurrentStepDisplayText));
            }
        }
    }

    public string CurrentSequenceStepText
    {
        get => _currentSequenceStepText;
        private set
        {
            if (SetProperty(ref _currentSequenceStepText, value))
            {
                OnPropertyChanged(nameof(SequenceStatusText));
            }
        }
    }

    public int CompletedSequenceSteps
    {
        get => _completedSequenceSteps;
        private set
        {
            if (SetProperty(ref _completedSequenceSteps, value))
            {
                OnPropertyChanged(nameof(SequenceStatusText));
                OnPropertyChanged(nameof(SequenceCompletedStepsText));
            }
        }
    }

    public string LastSequenceError => _lastSequenceError;

    public string LastSequenceActionStatus => _lastSequenceActionStatus;

    public long SequenceRunCount => Interlocked.Read(ref _sequenceRunCount);

    public long SequenceStopCount => Interlocked.Read(ref _sequenceStopCount);

    public long SequenceErrorCount => Interlocked.Read(ref _sequenceErrorCount);

    public int CommandSequenceCount => CommandSequences.Count;

    public string SequenceRuntimeStateText => IsSequenceRunning ? "Running" : "Stopped";

    public string SequenceCurrentStepDisplayText => IsSequenceRunning ? CurrentSequenceStepText : "(none)";

    public string SequenceCompletedStepsText => $"{CompletedSequenceSteps:N0}/{SelectedCommandSequenceStepCount:N0}";

    public string SequenceStatusText => IsSequenceRunning
        ? $"Running {RunningSequenceName}: step {CompletedSequenceSteps + 1:N0}, {CurrentSequenceStepText}"
        : string.IsNullOrWhiteSpace(LastSequenceError)
            ? $"Stopped | selected {SelectedCommandSequenceName}"
            : $"Stopped | last error: {LastSequenceError}";

    public AsyncRelayCommand ConnectCommand { get; }

    public AsyncRelayCommand ToggleConnectionCommand { get; }

    public AsyncRelayCommand DisconnectCommand { get; }

    public AsyncRelayCommand SendCommand { get; }

    public AsyncRelayCommand SendSavedCommandCommand { get; }

    public AsyncRelayCommand AddMarkerCommand { get; }

    public AsyncRelayCommand AddDefaultMarkerCommand { get; }

    public AsyncRelayCommand ToggleLogRenderingPauseCommand { get; }

    public AsyncRelayCommand ClearScreenCommand { get; }

    public AsyncRelayCommand CopyDiagnosticsCommand { get; }

    public AsyncRelayCommand CopyHelpCommand { get; }

    public AsyncRelayCommand OpenLogFolderCommand { get; }

    public AsyncRelayCommand OpenCurrentSerialLogCommand { get; }

    public AsyncRelayCommand CopySerialLogPathCommand { get; }

    public AsyncRelayCommand ToggleFileLoggingCommand { get; }

    public AsyncRelayCommand SaveProfileCommand { get; }

    public AsyncRelayCommand LoadProfileCommand { get; }

    public AsyncRelayCommand ResetProfileCommand { get; }

    public AsyncRelayCommand ResetCuteBackgroundCommand { get; }

    public AsyncRelayCommand FindNextCommand { get; }

    public AsyncRelayCommand FindPreviousCommand { get; }

    public AsyncRelayCommand RefreshSearchResultsCommand { get; }

    public AsyncRelayCommand CopyEventContextCommand { get; }

    public AsyncRelayCommand SelectLatestEventCommand { get; }

    public AsyncRelayCommand StartMockStressCommand { get; }

    public AsyncRelayCommand StopMockStressCommand { get; }

    public AsyncRelayCommand ResetMockStressCountersCommand { get; }

    public AsyncRelayCommand SendMockCrlfCommand { get; }

    public AsyncRelayCommand RunCommandSequenceCommand { get; }

    public AsyncRelayCommand StopCommandSequenceCommand { get; }

    public AsyncRelayCommand SetSessionCommand { get; }

    public AsyncRelayCommand EndSessionCommand { get; }

    public AsyncRelayCommand RefreshBridgePortsCommand { get; }

    public AsyncRelayCommand StartBridgeCommand { get; }

    public AsyncRelayCommand StopBridgeCommand { get; }

    public string? SelectedBridgePort
    {
        get => _selectedBridgePort;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (SetProperty(ref _selectedBridgePort, normalized))
            {
                _currentBridgeSettings.VirtualPortName = normalized ?? string.Empty;
                NotifyBridgePropertiesChanged();
            }
        }
    }

    public bool BridgeRequestedEnabled => _currentBridgeSettings.Enabled;

    public bool IsBridgeActive => _bridgeService.IsRunning;

    public bool CanEditBridgePort => !IsBridgeActive && !IsBusy;

    public bool CanStartBridge =>
        IsConnected &&
        !IsBusy &&
        !IsSequenceRunning &&
        !IsBridgeActive &&
        !string.IsNullOrWhiteSpace(SelectedBridgePort) &&
        !string.Equals(
            GetActualPortName(SelectedPort),
            SelectedBridgePort,
            StringComparison.OrdinalIgnoreCase);

    public bool CanStopBridge => IsBridgeActive;

    public string BridgeStateText => IsBridgeActive
        ? "BRIDGE ON"
        : "BRIDGE OFF";

    public string BridgeIndicatorText => IsBridgeActive
        ? $"BRIDGE ON {GetActualPortName(SelectedPort) ?? "?"} ↔ {_bridgeService.VirtualPortName}"
        : string.Empty;

    public string BridgeRouteText =>
        $"{GetActualPortName(SelectedPort) ?? "(device not selected)"} ↔ {SelectedBridgePort ?? "(virtual port not selected)"}";

    public string BridgeStatusText => IsBridgeActive
        ? $"Bidirectional raw-byte bridge active: {BridgeRouteText}"
        : "Bridge is off. It starts only when you press Start bridge.";

    public long BridgeDeviceToVirtualByteCount => _bridgeService.DeviceToVirtualByteCount;

    public long BridgeDeviceToVirtualChunkCount => _bridgeService.DeviceToVirtualChunkCount;

    public long BridgeVirtualToDeviceByteCount => _bridgeService.VirtualToDeviceByteCount;

    public long BridgeVirtualToDeviceChunkCount => _bridgeService.VirtualToDeviceChunkCount;

    public long BridgeDroppedDeviceToVirtualByteCount => _bridgeService.DroppedDeviceToVirtualByteCount;

    public long BridgeDroppedDeviceToVirtualChunkCount => _bridgeService.DroppedDeviceToVirtualChunkCount;

    public long BridgeDroppedVirtualToDeviceByteCount => _bridgeService.DroppedVirtualToDeviceByteCount;

    public long BridgeDroppedVirtualToDeviceChunkCount => _bridgeService.DroppedVirtualToDeviceChunkCount;

    public long BridgeDroppedByteCount =>
        BridgeDroppedDeviceToVirtualByteCount + BridgeDroppedVirtualToDeviceByteCount;

    public long BridgeDroppedChunkCount =>
        BridgeDroppedDeviceToVirtualChunkCount + BridgeDroppedVirtualToDeviceChunkCount;

    public long BridgeErrorCount => _bridgeService.ErrorCount + _bridgeLogProcessor.ErrorCount;

    public int BridgePendingDeviceToVirtualChunkCount => _bridgeService.PendingDeviceToVirtualChunkCount;

    public int BridgePendingVirtualToDeviceChunkCount => _bridgeService.PendingVirtualToDeviceChunkCount;

    public int BridgePendingDeviceToVirtualByteCount => _bridgeService.PendingDeviceToVirtualByteCount;

    public int BridgePendingVirtualToDeviceByteCount => _bridgeService.PendingVirtualToDeviceByteCount;

    public double BridgeOldestPendingChunkAgeMs => _bridgeService.OldestPendingChunkAgeMs;

    public ManualTxState ManualTxState => _bridgeService.ManualTxState;

    public bool IsManualTxBusy => IsBridgeActive && ManualTxState != ManualTxState.Idle;

    public bool CanSendManualTx => IsConnected && !IsBusy && !IsManualTxBusy;

    public string ManualTxStateText => ManualTxState switch
    {
        ManualTxState.WaitingForBridgeIdle =>
            $"TX waiting for bridge idle ({_bridgeService.ManualTxWaitMs:0} ms, guard {_bridgeService.ManualTxIdleGuardRemainingMs:0} ms)",
        ManualTxState.Sending => "TX sending",
        _ => "TX idle"
    };

    public ObservableCollection<TimestampDisplayFormatOption> TimestampDisplayFormatOptions { get; } = new()
    {
        new(TimestampDisplayFormat.DateTimeMilliseconds, "yyyy-MM-dd HH:mm:ss.fff"),
        new(TimestampDisplayFormat.DateTimeSeconds, "yyyy-MM-dd HH:mm:ss"),
        new(TimestampDisplayFormat.TimeMilliseconds, "HH:mm:ss.fff"),
        new(TimestampDisplayFormat.TimeSeconds, "HH:mm:ss")
    };

    public int BridgePendingChunkCount =>
        BridgePendingDeviceToVirtualChunkCount + BridgePendingVirtualToDeviceChunkCount;

    public string BridgePendingText => $"Pending {BridgePendingChunkCount:N0}";

    public string BridgeDroppedChunksText => $"Drop chunks {BridgeDroppedChunkCount:N0}";

    public string BridgeDroppedBytesText => $"Drop bytes {BridgeDroppedByteCount:N0}";

    public string BridgeErrorsText => $"Errors {BridgeErrorCount:N0}";

    public int BridgeVisualLogPendingCount => Volatile.Read(ref _bridgeVisualLogPendingCount);

    public long BridgeVisualLogDroppedCount => Interlocked.Read(ref _bridgeVisualLogDroppedCount);

    public string BridgeVisualLogStatusText =>
        $"Log input pending {_bridgeLogProcessor.PendingInputChunkCount:N0} / " +
        $"input drops {_bridgeLogProcessor.DroppedInputChunkCount:N0} / " +
        $"output drops {_bridgeLogProcessor.DroppedOutputLineCount:N0} / " +
        $"decode errors {_bridgeLogProcessor.DecodeErrorCount:N0} / " +
        $"UI pending {BridgeVisualLogPendingCount:N0} / UI drops {BridgeVisualLogDroppedCount:N0}";

    public bool IsRawBridgePriorityEnabled => _serialService.IsRawBridgePriorityEnabled;

    public long BridgePriorityDroppedPipelineByteCount => _serialService.BridgePriorityDroppedPipelineByteCount;

    public long BridgePriorityDroppedPipelineChunkCount => _serialService.BridgePriorityDroppedPipelineChunkCount;

    public string BridgePriorityPipelineStatusText => IsRawBridgePriorityEnabled
        ? $"Raw bridge priority ON · parser/log drops {BridgePriorityDroppedPipelineChunkCount:N0} chunks / {BridgePriorityDroppedPipelineByteCount:N0} bytes"
        : "Raw bridge priority OFF · normal lossless RX pipeline";

    public string BridgeLastError => _bridgeLogProcessor.LastError ?? _bridgeService.LastError ?? string.Empty;

    public string? SelectedPort
    {
        get => _selectedPort;
        set
        {
            var displayValue = NormalizePortSelectorValue(value, ShowMockTestPort);
            if (SetProperty(ref _selectedPort, displayValue))
            {
                _currentSerialSettings.PortName = GetActualPortName(displayValue) ?? string.Empty;
                RecordPortSelectionChange(_suppressSettingsApplyRecording
                    ? $"Restored port selection: {displayValue ?? "(none)"}"
                    : $"Selected port: {displayValue ?? "(none)"}");
                RecordSettingsChange("Port", SettingsApplyBehavior.ReconnectRequired, _currentSerialSettings.PortName);
                OnPropertyChanged(nameof(ConnectionStateText));
                OnPropertyChanged(nameof(CompactConnectionStatusText));
                OnPropertyChanged(nameof(CurrentPortIsMock));
                RefreshBridgePortOptionsFromCurrentPorts();
                NotifyBridgePropertiesChanged();
                NotifyConnectionSelectionCommandState();
            }
        }
    }

    public int SelectedBaudRate
    {
        get => _selectedBaudRate;
        set
        {
            if (value is <= 0 or > 10_000_000)
            {
                RecordSettingsValidationError("Baudrate must be a positive integer up to 10,000,000.");
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _selectedBaudRate, value))
            {
                _currentSerialSettings.BaudRate = value;
                RecordSettingsChange("Baudrate", SettingsApplyBehavior.ReconnectRequired, value.ToString(CultureInfo.InvariantCulture));
                OnPropertyChanged(nameof(HexGroupTimeoutRecommendationText));
                OnPropertyChanged(nameof(ConnectionStateText));
                OnPropertyChanged(nameof(CompactConnectionStatusText));
                NotifyConnectionSelectionCommandState();
            }
        }
    }

    public TxLineEndingMode SelectedTxLineEnding
    {
        get => _selectedTxLineEnding;
        set
        {
            if (!Enum.IsDefined(value))
            {
                RecordSettingsValidationError("TX line ending selection is invalid.");
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _selectedTxLineEnding, value))
            {
                _currentSerialSettings.TxLineEnding = value;
                RecordSettingsChange("TX line ending", SettingsApplyBehavior.Immediate, value.ToString());
            }
        }
    }

    public RxDisplayMode SelectedRxDisplayMode
    {
        get => _currentUiSettings.RxDisplayMode;
        set
        {
            var normalized = NormalizeRxDisplayMode(value);
            if (!Enum.IsDefined(value))
            {
                RecordSettingsValidationError("Terminal / HEX mode selection is invalid.");
                OnPropertyChanged();
                return;
            }

            var txMode = normalized == RxDisplayMode.Hex
                ? TxSendMode.Hex
                : TxSendMode.Terminal;
            var rxChanged = _currentUiSettings.RxDisplayMode != normalized;
            var txChanged = _currentUiSettings.TxSendMode != txMode;
            if (!rxChanged && !txChanged)
            {
                return;
            }

            _currentUiSettings.RxDisplayMode = normalized;
            _currentUiSettings.TxSendMode = txMode;
            if (rxChanged)
            {
                _bridgeLogProcessor.ResetStream();
            }
            var activeRuleMode = ToLogRuleMode(normalized);
            _eventDetector.UpdateRuleMode(activeRuleMode);
            ClearPendingEventNotifications();
            var reconnectRequired = RequiresAutomaticReceiveReconnect(normalized, HexGroupTimeoutMs);
            if (rxChanged)
            {
                if (!reconnectRequired)
                {
                    ApplyRxDisplayRuntime(normalized, HexGroupTimeoutMs, "Terminal / HEX mode change");
                }
            }

            RecordSettingsChange(
                "Terminal / HEX mode",
                reconnectRequired
                    ? SettingsApplyBehavior.AutomaticReconnect
                    : SettingsApplyBehavior.Immediate,
                FormatRxDisplayModeName(normalized));
            if (reconnectRequired || Volatile.Read(ref _automaticReceiveReconnectWorkerRunning) != 0)
            {
                QueueAutomaticReceiveReconnect();
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedTxSendMode));
            OnPropertyChanged(nameof(IsHexRxViewSelected));
            OnPropertyChanged(nameof(HexGroupTimeoutHeaderText));
            OnPropertyChanged(nameof(HexGroupTimeoutHeaderMinWidth));
            OnPropertyChanged(nameof(HexGroupTimeoutAppliedText));
            OnPropertyChanged(nameof(IsTxLineEndingEffective));
            OnPropertyChanged(nameof(TxLineEndingToolTip));
            RefreshVisibleLogFilterOptions(preserveSelection: true, applyFilter: true);
            NotifyRuleEditorStateChanged();
            RefreshDiagnostics();
        }
    }

    public bool IsHexRxViewSelected => SelectedRxDisplayMode == RxDisplayMode.Hex;

    public int HexGroupTimeoutMs
    {
        get => _currentUiSettings.HexGroupTimeoutMs;
        set
        {
            if (!ValidateIntRange("HEX group timeout (ms)", value, MinHexGroupTimeoutMs, MaxHexGroupTimeoutMs))
            {
                OnPropertyChanged();
                OnPropertyChanged(nameof(HexGroupTimeoutMsText));
                return;
            }

            if (_currentUiSettings.HexGroupTimeoutMs == value)
            {
                return;
            }

            _currentUiSettings.HexGroupTimeoutMs = value;
            var reconnectRequired = RequiresAutomaticReceiveReconnect(SelectedRxDisplayMode, value);
            if (!reconnectRequired)
            {
                _logPipeline.ConfigureRxDisplay(_appliedRxDisplayMode, value);
                _appliedHexGroupTimeoutMs = value;
            }

            RecordSettingsChange(
                "HEX group timeout",
                reconnectRequired ? SettingsApplyBehavior.AutomaticReconnect : SettingsApplyBehavior.Immediate,
                $"{value} ms");
            if (reconnectRequired || Volatile.Read(ref _automaticReceiveReconnectWorkerRunning) != 0)
            {
                QueueAutomaticReceiveReconnect();
            }

            OnPropertyChanged();
            _hexGroupTimeoutDraftText = value.ToString(CultureInfo.InvariantCulture);
            OnPropertyChanged(nameof(HexGroupTimeoutDraftText));
            OnPropertyChanged(nameof(HasPendingHexGroupTimeout));
            OnPropertyChanged(nameof(HexGroupTimeoutAppliedText));
            OnPropertyChanged(nameof(HexGroupTimeoutHeaderText));
            OnPropertyChanged(nameof(HexGroupTimeoutMsText));
            RefreshDiagnostics();
        }
    }

    public string HexGroupTimeoutDraftText
    {
        get => _hexGroupTimeoutDraftText;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_hexGroupTimeoutDraftText, value, StringComparison.Ordinal))
            {
                return;
            }

            _hexGroupTimeoutDraftText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPendingHexGroupTimeout));
        }
    }

    public bool HasPendingHexGroupTimeout =>
        !int.TryParse(HexGroupTimeoutDraftText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
        parsed != HexGroupTimeoutMs;

    public string HexGroupTimeoutAppliedText
    {
        get
        {
            if (!IsConnected)
            {
                return $"Next: {HexGroupTimeoutMs:N0} ms";
            }

            if (!IsHexRxViewSelected)
            {
                return $"Saved: {HexGroupTimeoutMs:N0} ms";
            }

            var nativeTimeout = _serialService.AppliedReceiveIdleTimeoutMs;
            var fullyApplied = _logPipeline.HexGroupTimeoutMs == HexGroupTimeoutMs &&
                (CurrentPortIsMock ||
                 (_serialService.UsesNativeReceiveIdleTimeout && nativeTimeout == HexGroupTimeoutMs));
            return fullyApplied
                ? $"Applied: {HexGroupTimeoutMs:N0} ms"
                : $"Active: {_logPipeline.HexGroupTimeoutMs:N0} ms · reconnect";
        }
    }

    public string HexGroupTimeoutRecommendationText
    {
        get
        {
            var recommendation = SerialTimingAdvisor.Calculate(
                Math.Max(1, SelectedBaudRate),
                Math.Max(1, SelectedDataBits),
                SelectedParity != SerialParityMode.None,
                SelectedStopBits switch
                {
                    SerialStopBitsMode.OnePointFive => 1.5,
                    SerialStopBitsMode.Two => 2.0,
                    _ => 1.0
                });
            return $"Start ≥ {recommendation.SuggestedStartingTimeoutMilliseconds:N0} ms " +
                   $"({recommendation.CharacterTimeMilliseconds:0.###} ms/char); tune to actual gaps";
        }
    }

    public string HexGroupTimeoutHeaderText
    {
        get
        {
            if (!IsHexRxViewSelected)
            {
                return string.Empty;
            }

            var activeTimeout = IsConnected && _serialService.UsesNativeReceiveIdleTimeout
                ? _serialService.AppliedReceiveIdleTimeoutMs
                : HexGroupTimeoutMs;
            return $"HEX {activeTimeout:N0} ms";
        }
    }

    public double HexGroupTimeoutHeaderMinWidth => IsHexRxViewSelected ? 68d : 0d;

    public bool ApplyHexGroupTimeoutDraft()
    {
        if (!TryParseIntSetting(
                "HEX group timeout (ms)",
                HexGroupTimeoutDraftText,
                MinHexGroupTimeoutMs,
                MaxHexGroupTimeoutMs,
                out var normalized))
        {
            return false;
        }

        HexGroupTimeoutMs = normalized;
        _hexGroupTimeoutDraftText = normalized.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(HexGroupTimeoutDraftText));
        OnPropertyChanged(nameof(HasPendingHexGroupTimeout));
        OnPropertyChanged(nameof(HexGroupTimeoutAppliedText));
        OnPropertyChanged(nameof(HexGroupTimeoutHeaderText));
        OnPropertyChanged(nameof(HexGroupTimeoutHeaderMinWidth));
        return true;
    }

    public string HexGroupTimeoutMsText
    {
        get => HexGroupTimeoutMs.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (TryParseIntSetting(
                "HEX group timeout (ms)",
                value,
                MinHexGroupTimeoutMs,
                MaxHexGroupTimeoutMs,
                out var parsed))
            {
                HexGroupTimeoutMs = parsed;
            }
            else
            {
                OnPropertyChanged();
            }
        }
    }

    public TxSendMode SelectedTxSendMode
    {
        get => _currentUiSettings.TxSendMode;
        set
        {
            var normalized = NormalizeTxSendMode(value);
            if (!Enum.IsDefined(value))
            {
                RecordSettingsValidationError("Terminal / HEX mode selection is invalid.");
                OnPropertyChanged();
                return;
            }

            SelectedRxDisplayMode = normalized == TxSendMode.Hex
                ? RxDisplayMode.Hex
                : RxDisplayMode.Terminal;
        }
    }

    public bool IsTxLineEndingEffective => SelectedTxSendMode != TxSendMode.Hex;

    public string TxLineEndingToolTip => SelectedTxSendMode == TxSendMode.Hex
        ? "Ignored in HEX TX mode. Enter all bytes explicitly, including 0D 0A if needed."
        : "Line ending appended after Terminal TX input.";

    private static RxDisplayMode NormalizeRxDisplayMode(RxDisplayMode mode)
    {
        return mode == RxDisplayMode.Hex
            ? RxDisplayMode.Hex
            : RxDisplayMode.Terminal;
    }

    private static TxSendMode NormalizeTxSendMode(TxSendMode mode)
    {
        return mode == TxSendMode.Hex
            ? TxSendMode.Hex
            : TxSendMode.Terminal;
    }

    private static string FormatRxDisplayModeName(RxDisplayMode mode)
    {
        return NormalizeRxDisplayMode(mode) == RxDisplayMode.Hex
            ? "HEX"
            : "Terminal";
    }

    private static string FormatTxSendModeName(TxSendMode mode)
    {
        return NormalizeTxSendMode(mode) == TxSendMode.Hex
            ? "HEX"
            : "Terminal";
    }

    private static LogRuleMatchMode ToLogRuleMode(RxDisplayMode mode)
    {
        return NormalizeRxDisplayMode(mode) == RxDisplayMode.Hex
            ? LogRuleMatchMode.Hex
            : LogRuleMatchMode.Terminal;
    }

    private bool RequiresAutomaticReceiveReconnect(RxDisplayMode mode, int hexGroupTimeoutMs)
    {
        var serialLifecycleActive = _serialService.ConnectionState is
            SerialConnectionState.Connecting or
            SerialConnectionState.Connected or
            SerialConnectionState.Disconnecting;
        var hasActiveRealConnection = !CurrentPortIsMock &&
            (serialLifecycleActive || (IsBusy && _connectionCancellation is not null));
        if (!hasActiveRealConnection)
        {
            return false;
        }

        var normalizedMode = NormalizeRxDisplayMode(mode);
        return normalizedMode != _appliedRxDisplayMode ||
            (normalizedMode == RxDisplayMode.Hex && hexGroupTimeoutMs != _appliedHexGroupTimeoutMs);
    }

    private void ApplyRxDisplayRuntime(RxDisplayMode mode, int hexGroupTimeoutMs, string rebuildReason)
    {
        var normalizedMode = NormalizeRxDisplayMode(mode);
        _logPipeline.ConfigureRxDisplay(normalizedMode, hexGroupTimeoutMs);
        SetVisibleLogRebuildReason(rebuildReason);
        Log.SetRxDisplayMode(normalizedMode);
        MarkSearchResultsStale();
        _appliedRxDisplayMode = normalizedMode;
        _appliedHexGroupTimeoutMs = hexGroupTimeoutMs;
    }

    private static string FormatMockGeneratorPatternName(MockGeneratorPattern pattern)
    {
        return pattern switch
        {
            MockGeneratorPattern.NoNewlineZzz => "No-Newline zzz",
            MockGeneratorPattern.NoNewlineZzzBurst => "No-Newline zzz burst",
            MockGeneratorPattern.VisualHexPackets => "Visual HEX 3-5 ms",
            _ => "Normal Lines"
        };
    }

    public int SelectedDataBits
    {
        get => _currentSerialSettings.DataBits;
        set
        {
            if (value is < 5 or > 8)
            {
                RecordSettingsValidationError("Data bits must be between 5 and 8.");
                OnPropertyChanged();
                return;
            }

            if (_currentSerialSettings.DataBits == value)
            {
                return;
            }

            _currentSerialSettings.DataBits = value;
            RecordSettingsChange("Data bits", SettingsApplyBehavior.ReconnectRequired, value.ToString(CultureInfo.InvariantCulture));
            OnPropertyChanged();
            OnPropertyChanged(nameof(HexGroupTimeoutRecommendationText));
        }
    }

    public SerialParityMode SelectedParity
    {
        get => _currentSerialSettings.Parity;
        set
        {
            if (!Enum.IsDefined(value))
            {
                RecordSettingsValidationError("Parity selection is invalid.");
                OnPropertyChanged();
                return;
            }

            if (_currentSerialSettings.Parity == value)
            {
                return;
            }

            _currentSerialSettings.Parity = value;
            RecordSettingsChange("Parity", SettingsApplyBehavior.ReconnectRequired, value.ToString());
            OnPropertyChanged();
            OnPropertyChanged(nameof(HexGroupTimeoutRecommendationText));
        }
    }

    public SerialStopBitsMode SelectedStopBits
    {
        get => _currentSerialSettings.StopBits;
        set
        {
            if (!Enum.IsDefined(value))
            {
                RecordSettingsValidationError("Stop bits selection is invalid.");
                OnPropertyChanged();
                return;
            }

            if (_currentSerialSettings.StopBits == value)
            {
                return;
            }

            _currentSerialSettings.StopBits = value;
            RecordSettingsChange("Stop bits", SettingsApplyBehavior.ReconnectRequired, value.ToString());
            OnPropertyChanged();
            OnPropertyChanged(nameof(HexGroupTimeoutRecommendationText));
        }
    }

    public SerialHandshakeMode SelectedHandshake
    {
        get => _currentSerialSettings.Handshake;
        set
        {
            if (!Enum.IsDefined(value))
            {
                RecordSettingsValidationError("Handshake selection is invalid.");
                OnPropertyChanged();
                return;
            }

            if (_currentSerialSettings.Handshake == value)
            {
                return;
            }

            _currentSerialSettings.Handshake = value;
            RecordSettingsChange("Handshake", SettingsApplyBehavior.ReconnectRequired, value.ToString());
            OnPropertyChanged();
        }
    }

    public bool DtrEnable
    {
        get => _currentSerialSettings.DtrEnable;
        set
        {
            if (_currentSerialSettings.DtrEnable == value)
            {
                return;
            }

            _currentSerialSettings.DtrEnable = value;
            RecordSettingsChange("DTR", SettingsApplyBehavior.ReconnectRequired, value ? "enabled" : "disabled");
            OnPropertyChanged();
        }
    }

    public bool RtsEnable
    {
        get => _currentSerialSettings.RtsEnable;
        set
        {
            if (_currentSerialSettings.RtsEnable == value)
            {
                return;
            }

            _currentSerialSettings.RtsEnable = value;
            RecordSettingsChange("RTS", SettingsApplyBehavior.ReconnectRequired, value ? "enabled" : "disabled");
            OnPropertyChanged();
        }
    }

    public RxLineEndingMode SelectedRxLineEnding
    {
        get => _currentSerialSettings.RxLineEnding;
        set
        {
            if (!Enum.IsDefined(value))
            {
                RecordSettingsValidationError("RX line ending selection is invalid.");
                OnPropertyChanged();
                return;
            }

            if (_currentSerialSettings.RxLineEnding == value)
            {
                return;
            }

            _currentSerialSettings.RxLineEnding = value;
            RecordSettingsChange("RX line ending", SettingsApplyBehavior.ReconnectRequired, value.ToString());
            OnPropertyChanged();
        }
    }

    public RxEncodingMode SelectedRxEncoding
    {
        get => _currentSerialSettings.Encoding;
        set
        {
            if (!Enum.IsDefined(value))
            {
                RecordSettingsValidationError("RX encoding selection is invalid.");
                OnPropertyChanged();
                return;
            }

            if (_currentSerialSettings.Encoding == value)
            {
                return;
            }

            _currentSerialSettings.Encoding = value;
            _bridgeLogProcessor.ResetStream();
            RecordSettingsChange("RX encoding", SettingsApplyBehavior.ReconnectRequired, value.ToString());
            OnPropertyChanged();
        }
    }

    public bool FileLoggingEnabled
    {
        get => _currentLogSettings.FileLoggingEnabled;
        set
        {
            if (_currentLogSettings.FileLoggingEnabled == value)
            {
                return;
            }

            _ = SetFileLoggingEnabledAsync(value, recordSettingChange: true);
        }
    }

    public bool FileLoggingActive => FileLoggingEnabled && _fileLogWriter.IsRunning;

    public string FileLoggingToggleText => FileLoggingEnabled ? "LOG ON" : "LOG OFF";

    public string FileLoggingMainStatusText => FileLoggingEnabled ? "Log Save: ON" : "Log Save: OFF";

    public string FileLoggingToolTip => FileLoggingEnabled
        ? "Log Save ON writes the serial stream to a text log. Click to stop saving; existing files are not deleted."
        : "Log Save OFF keeps the terminal and event detection live without writing serial log files. Click to start saving.";

    public bool FileLoggingWhileViewPaused
    {
        get => _currentUiSettings.FileLoggingWhileViewPaused;
        set
        {
            if (_currentUiSettings.FileLoggingWhileViewPaused == value)
            {
                return;
            }

            _currentUiSettings.FileLoggingWhileViewPaused = value;
            RecordSettingsChange(
                "File logging while view paused",
                SettingsApplyBehavior.Immediate,
                value ? "enabled" : "disabled");
            OnPropertyChanged();
            OnPropertyChanged(nameof(PauseRenderingToolTip));
            SetFooter(CreateFooterStatus());
        }
    }

    public bool CanEditLogFileName =>
        !FileLoggingEnabled &&
        !IsBusy &&
        Volatile.Read(ref _fileLoggingTransitionCount) == 0;

    public string LogSaveDirectory
    {
        get => _currentLogSettings.SaveDirectory;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (!TryValidateSaveDirectory(normalized, out var validatedDirectory))
            {
                OnPropertyChanged();
                return;
            }

            if (string.Equals(_currentLogSettings.SaveDirectory, validatedDirectory, StringComparison.Ordinal))
            {
                return;
            }

            _currentLogSettings.SaveDirectory = validatedDirectory;
            _currentSerialSettings.SaveDirectory = validatedDirectory;
            RecordSettingsChange("Log save directory", SettingsApplyBehavior.ReconnectRequired, validatedDirectory);
            OnPropertyChanged();
        }
    }

    public bool SizeRotationEnabled
    {
        get => _currentLogSettings.SizeRotationEnabled;
        set
        {
            if (_currentLogSettings.SizeRotationEnabled == value)
            {
                return;
            }

            _currentLogSettings.SizeRotationEnabled = value;
            EnsureDefaultSizeRotationBytes();
            ApplySizeRotationSettings();
            RecordSettingsChange("Size rotation", SettingsApplyBehavior.Immediate, value ? "enabled" : "disabled");
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanEditSizeRotationMegabytes));
        }
    }

    public string SizeRotationMegabytesText
    {
        get => (_currentLogSettings.SizeRotationBytes.GetValueOrDefault() / LogSettings.BytesPerMegabyte)
            .ToString(CultureInfo.InvariantCulture);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _currentLogSettings.SizeRotationBytes = LogSettings.DefaultSizeRotationBytes;
                ApplySizeRotationSettings();
                RecordSettingsChange("Size rotation", SettingsApplyBehavior.Immediate, "10 MB default");
                OnPropertyChanged();
                return;
            }

            if (long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                parsed >= MinSizeRotationMegabytes &&
                parsed <= MaxSizeRotationMegabytes)
            {
                _currentLogSettings.SizeRotationBytes = parsed * LogSettings.BytesPerMegabyte;
                ApplySizeRotationSettings();
                RecordSettingsChange("Size rotation", SettingsApplyBehavior.Immediate, $"{parsed:N0} MB");
                OnPropertyChanged();
                return;
            }

            RecordSettingsValidationError($"Size rotation must be a whole number between {MinSizeRotationMegabytes:N0} MB and {MaxSizeRotationMegabytes:N0} MB.");
            OnPropertyChanged();
        }
    }

    public int MaxVisibleLogLines
    {
        get => _currentUiSettings.MaxVisibleLogLines;
        set
        {
            if (!ValidateIntRange("Max visible log lines", value, MinVisibleLogLines, MaxVisibleLogLinesLimit))
            {
                OnPropertyChanged();
                OnPropertyChanged(nameof(MaxVisibleLogLinesText));
                return;
            }

            if (_currentUiSettings.MaxVisibleLogLines == value)
            {
                return;
            }

            _currentUiSettings.MaxVisibleLogLines = value;
            if (_currentUiSettings.XtermScrollbackSize < value)
            {
                _currentUiSettings.XtermScrollbackSize = value;
                OnPropertyChanged(nameof(XtermScrollbackSize));
                OnPropertyChanged(nameof(XtermScrollbackSizeText));
            }
            SetVisibleLogRebuildReason("visible log capacity change");
            Log.SetCapacity(value);
            _lastVisibleCapChangeTimeText = FormatDiagnosticTime(DateTimeOffset.Now);
            MarkSearchResultsStale();
            RecordSettingsChange("Max visible log lines", SettingsApplyBehavior.Immediate, value.ToString(CultureInfo.InvariantCulture));
            OnPropertyChanged();
            OnPropertyChanged(nameof(MaxVisibleLogLinesText));
            OnPropertyChanged(nameof(EffectiveXtermScrollbackSize));
            OnPropertyChanged(nameof(LastVisibleCapChangeTimeText));
            RefreshDiagnostics();
        }
    }

    public string MaxVisibleLogLinesText
    {
        get => MaxVisibleLogLines.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (TryParseIntSetting("Max visible log lines", value, MinVisibleLogLines, MaxVisibleLogLinesLimit, out var parsed))
            {
                MaxVisibleLogLines = parsed;
            }
            else
            {
                OnPropertyChanged();
            }
        }
    }

    public int MaxVisibleEventCount => _currentUiSettings.MaxVisibleEventCount;

    public int XtermScrollbackSize
    {
        get => _currentUiSettings.XtermScrollbackSize;
        set
        {
            if (!ValidateIntRange("xterm scrollback size", value, MinXtermScrollbackSize, MaxXtermScrollbackSizeLimit))
            {
                OnPropertyChanged();
                OnPropertyChanged(nameof(XtermScrollbackSizeText));
                return;
            }

            var normalizedValue = Math.Max(value, MaxVisibleLogLines);
            if (_currentUiSettings.XtermScrollbackSize == normalizedValue)
            {
                if (normalizedValue != value)
                {
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(XtermScrollbackSizeText));
                }
                return;
            }

            _currentUiSettings.XtermScrollbackSize = normalizedValue;
            RecordSettingsChange("xterm scrollback", SettingsApplyBehavior.Immediate, normalizedValue.ToString(CultureInfo.InvariantCulture));
            OnPropertyChanged();
            OnPropertyChanged(nameof(XtermScrollbackSizeText));
            OnPropertyChanged(nameof(EffectiveXtermScrollbackSize));
        }
    }

    public string XtermScrollbackSizeText
    {
        get => XtermScrollbackSize.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (TryParseIntSetting("xterm scrollback size", value, MinXtermScrollbackSize, MaxXtermScrollbackSizeLimit, out var parsed))
            {
                XtermScrollbackSize = parsed;
            }
            else
            {
                OnPropertyChanged();
            }
        }
    }

    public int EffectiveXtermScrollbackSize => Math.Max(XtermScrollbackSize, MaxVisibleLogLines);

    public int BeforeContextLines => _currentEventContextSettings.BeforeContextLines;

    public int AfterContextLines => _currentEventContextSettings.AfterContextLines;

    public string MarkerText
    {
        get => _markerText;
        set
        {
            if (SetProperty(ref _markerText, value))
            {
                _currentUiSettings.MarkerText = value?.Trim() ?? string.Empty;
                RecordSettingsChange("Marker text", SettingsApplyBehavior.Immediate, string.IsNullOrWhiteSpace(value) ? "default marker" : value.Trim());
            }
        }
    }

    public int SelectedMockStressLinesPerSecond
    {
        get => _selectedMockStressLinesPerSecond;
        set
        {
            if (!ValidateIntRange("Mock stress lines/sec", value, MinMockStressLinesPerSecond, MaxMockStressLinesPerSecond))
            {
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _selectedMockStressLinesPerSecond, value))
            {
                _currentUiSettings.MockStressLinesPerSecond = value;
                ConfigureMockStressFromUi();
                RecordSettingsChange(
                    "Mock stress lines/sec",
                    SettingsApplyBehavior.Immediate,
                    SelectedMockStressLinesPerSecond.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    public int SelectedMockStressBurstSize
    {
        get => _selectedMockStressBurstSize;
        set
        {
            if (!ValidateIntRange("Mock stress burst size", value, MinMockStressBurstSize, MaxMockStressBurstSize))
            {
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _selectedMockStressBurstSize, value))
            {
                _currentUiSettings.MockStressBurstSize = value;
                ConfigureMockStressFromUi();
                RecordSettingsChange(
                    "Mock stress burst",
                    SettingsApplyBehavior.Immediate,
                    SelectedMockStressBurstSize.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    public MockGeneratorPatternOption? SelectedMockGeneratorPatternOption
    {
        get => MockGeneratorPatternOptions.FirstOrDefault(option => option.Pattern == _selectedMockGeneratorPattern) ??
            MockGeneratorPatternOptions.FirstOrDefault();
        set
        {
            var pattern = value?.Pattern ?? MockGeneratorPattern.NormalLines;
            if (_selectedMockGeneratorPattern == pattern)
            {
                return;
            }

            _selectedMockGeneratorPattern = pattern;
            _currentUiSettings.MockGeneratorPattern = pattern;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedMockGeneratorPatternText));
            ConfigureMockStressFromUi();
            RecordSettingsChange("Mock pattern", SettingsApplyBehavior.Immediate, FormatMockGeneratorPatternName(pattern));
        }
    }

    public string SelectedMockGeneratorPatternText => FormatMockGeneratorPatternName(_selectedMockGeneratorPattern);

    public bool IsMockStressEventInjectionEnabled
    {
        get => _isMockStressEventInjectionEnabled;
        set
        {
            if (SetProperty(ref _isMockStressEventInjectionEnabled, value))
            {
                _currentUiSettings.MockStressEventInjectionEnabled = value;
                ConfigureMockStressFromUi();
                RecordSettingsChange("Mock event injection", SettingsApplyBehavior.Immediate, value ? "enabled" : "disabled");
            }
        }
    }

    public bool IsMockStressInvalidByteInjectionEnabled
    {
        get => _isMockStressInvalidByteInjectionEnabled;
        set
        {
            if (SetProperty(ref _isMockStressInvalidByteInjectionEnabled, value))
            {
                _currentUiSettings.MockStressInvalidByteInjectionEnabled = value;
                ConfigureMockStressFromUi();
                RecordSettingsChange("Mock invalid byte injection", SettingsApplyBehavior.Immediate, value ? "enabled" : "disabled");
            }
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(ConnectionButtonText));
                OnPropertyChanged(nameof(ConnectionStateText));
                OnPropertyChanged(nameof(CompactConnectionStatusText));
                OnPropertyChanged(nameof(CanEditConnectionSettings));
                OnPropertyChanged(nameof(CanEditSizeRotationMegabytes));
                OnPropertyChanged(nameof(CanManualDisconnect));
                OnPropertyChanged(nameof(CanConnect));
                NotifyCommandStates();
                NotifyBridgePropertiesChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanEditConnectionSettings));
                OnPropertyChanged(nameof(CanEditSizeRotationMegabytes));
                OnPropertyChanged(nameof(CanEditLogFileName));
                OnPropertyChanged(nameof(CanManualDisconnect));
                OnPropertyChanged(nameof(CanConnect));
                OnPropertyChanged(nameof(CanEditCommandSequences));
                OnPropertyChanged(nameof(CanEditSelectedCommandSequence));
                OnPropertyChanged(nameof(CanEditSelectedCommandSequenceStep));
                NotifyCommandStates();
                NotifyBridgePropertiesChanged();
            }
        }
    }

    public bool CanEditConnectionSettings => !IsConnected && !IsBusy;

    public bool CanEditSizeRotationMegabytes => !IsBusy && SizeRotationEnabled;

    public bool CanManualDisconnect => IsConnected && !IsBusy;

    public bool CanToggleConnection => IsConnected ? CanManualDisconnect : CanConnect;

    public bool CanConnect =>
        !IsConnected &&
        !IsBusy &&
        SelectedPortAvailable &&
        BaudRates.Contains(SelectedBaudRate);

    public bool IsLogRenderingPaused
    {
        get
        {
            lock (_viewPauseGate)
            {
                return _viewPause.State != ViewPauseState.Live;
            }
        }
    }

    public bool IsManualLogRenderingPaused => IsLogRenderingPaused;

    public bool IsViewPauseTransitioning
    {
        get
        {
            lock (_viewPauseGate)
            {
                return _viewPause.State == ViewPauseState.Pausing;
            }
        }
    }

    public bool IsViewFullyPaused
    {
        get
        {
            lock (_viewPauseGate)
            {
                return _viewPause.State == ViewPauseState.Paused;
            }
        }
    }

    public bool IsXtermAppendBackpressureActive => _isXtermAppendBackpressureActive;

    public bool IsEffectiveXtermAutoScrollEnabled =>
        IsAutoScrollEnabled && !IsXtermAppendBackpressureActive;

    public bool IsEventAutoScrollSuppressedByXtermBackpressure =>
        IsXtermAppendBackpressureActive && IsEventAutoScrollEnabled;

    public bool IsAutoScrollEnabled
    {
        get => _isAutoScrollEnabled;
        set
        {
            if (SetProperty(ref _isAutoScrollEnabled, value))
            {
                _currentUiSettings.AutoScrollEnabled = value;
                OnPropertyChanged(nameof(IsEffectiveXtermAutoScrollEnabled));
                RecordSettingsChange("xterm auto-scroll", SettingsApplyBehavior.Immediate, value ? "enabled" : "disabled");
                RecordAutoScrollAction(value ? "Auto Scroll enabled" : "Auto Scroll disabled", null);
            }
        }
    }

    public bool ConfirmBeforeDisconnect
    {
        get => _currentUiSettings.ConfirmBeforeDisconnect;
        set
        {
            if (_currentUiSettings.ConfirmBeforeDisconnect == value)
            {
                return;
            }

            _currentUiSettings.ConfirmBeforeDisconnect = value;
            RecordSettingsChange("Confirm before disconnect", SettingsApplyBehavior.Immediate, value ? "enabled" : "disabled");
            OnPropertyChanged();
        }
    }

    public bool ShowTimestampInLogView
    {
        get => _currentUiSettings.ShowTimestampInLogView;
        set
        {
            if (_currentUiSettings.ShowTimestampInLogView == value)
            {
                return;
            }

            try
            {
                _currentUiSettings.ShowTimestampInLogView = value;
                SetVisibleLogRebuildReason("timestamp display change");
                Log.SetShowTimestampInLogView(value);
                _lastTimestampDisplayModeChangeTimeText = FormatDiagnosticTime(DateTimeOffset.Now);
                _lastTimestampDisplayModeError = string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastTimestampDisplayModeChangeTimeText));
                OnPropertyChanged(nameof(LastTimestampDisplayModeError));
                RefreshSelectedEventContextText();
                MarkSearchResultsStale();
                RecordSettingsChange("Show timestamp in log view", SettingsApplyBehavior.Immediate, value ? "shown" : "hidden");
            }
            catch (Exception ex)
            {
                RecordTimestampDisplayModeError($"Timestamp display mode update failed: {ex.Message}");
                OnPropertyChanged();
            }
        }
    }

    public bool CuteBackgroundMode
    {
        get => _currentUiSettings.CuteBackgroundMode;
        set
        {
            if (_currentUiSettings.CuteBackgroundMode == value)
            {
                return;
            }

            _currentUiSettings.CuteBackgroundMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CuteBackgroundLayerOpacity));
            OnPropertyChanged(nameof(CuteBackgroundOpacity));
            OnPropertyChanged(nameof(CuteBackgroundOpacityText));
            OnPropertyChanged(nameof(CuteBackgroundOverlayOpacity));
            RecordSettingsChange("Cute background mode", SettingsApplyBehavior.Immediate, value ? "enabled" : "disabled");
            RefreshDiagnostics();
        }
    }

    public string CuteBackgroundImagePath
    {
        get => _currentUiSettings.CuteBackgroundImagePath;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(_currentUiSettings.CuteBackgroundImagePath, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _currentUiSettings.CuteBackgroundImagePath = normalized;
            OnPropertyChanged();
            RecordSettingsChange(
                "Custom cute background image",
                SettingsApplyBehavior.Immediate,
                string.IsNullOrWhiteSpace(normalized) ? "(none)" : normalized);
            RefreshDiagnostics();
        }
    }

    public IReadOnlyList<string> CuteBackgroundOpacityOptions => CuteBackgroundOpacityOptionValues;

    public double CuteBackgroundLayerOpacity => CuteBackgroundMode ? 1.0 : 0.0;

    public double CuteBackgroundOpacity
    {
        get => _currentUiSettings.CuteBackgroundOpacity;
        set
        {
            var clamped = Math.Clamp(value, MinCuteBackgroundOpacity, MaxCuteBackgroundOpacity);
            if (Math.Abs(_currentUiSettings.CuteBackgroundOpacity - clamped) < 0.0001)
            {
                return;
            }

            _currentUiSettings.CuteBackgroundOpacity = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CuteBackgroundOpacityText));
            OnPropertyChanged(nameof(CuteBackgroundOverlayOpacity));
            RecordSettingsChange(
                "Cute background opacity",
                SettingsApplyBehavior.Immediate,
                clamped.ToString("0.00", CultureInfo.InvariantCulture));
            RefreshDiagnostics();
        }
    }

    public string CuteBackgroundOpacityText
    {
        get => CuteBackgroundOpacity.ToString("0.00", CultureInfo.InvariantCulture);
        set
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                CuteBackgroundOpacity = parsed;
                return;
            }

            SetStatus("Cute background opacity was invalid.");
            _lastBackgroundError = "Cute background opacity was invalid.";
            RefreshDiagnostics();
            OnPropertyChanged();
        }
    }

    public double CuteBackgroundOverlayOpacity => CuteBackgroundMode
        ? Math.Clamp(0.70 - (CuteBackgroundOpacity * 0.5), 0.45, 0.65)
        : 0.0;

    public bool CuteBackgroundFileExists => _cuteBackgroundFileExists;

    public bool CuteBackgroundLoaded => _cuteBackgroundLoaded;

    public string CuteBackgroundSource => _cuteBackgroundSource;

    public string CuteBackgroundBundledPath => _cuteBackgroundBundledPath;

    public string CuteBackgroundLoadError => _cuteBackgroundLoadError;

    public string CuteBackgroundLastAppliedTimeText => _cuteBackgroundLastAppliedTimeText;

    public long CuteBackgroundApplyCount => Interlocked.Read(ref _cuteBackgroundApplyCount);

    public long CuteBackgroundImageReloadCount => Interlocked.Read(ref _cuteBackgroundImageReloadCount);

    public long CuteBackgroundSkippedUnchangedCount => Interlocked.Read(ref _cuteBackgroundSkippedUnchangedCount);

    public void SetCuteBackgroundImagePathFromPicker(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            RecordSettingsChange("Custom cute background image", SettingsApplyBehavior.Immediate, "browse canceled");
            return;
        }

        CuteBackgroundImagePath = path;
    }

    private Task ResetCuteBackgroundAsync()
    {
        CuteBackgroundImagePath = string.Empty;
        RecordSettingsChange(
            "Custom cute background image",
            SettingsApplyBehavior.Immediate,
            "reset to bundled default");
        SetStatus("Cute background reset to bundled default.");
        RefreshDiagnostics();
        return Task.CompletedTask;
    }

    public void RecordCuteBackgroundApplySkipped()
    {
        Interlocked.Increment(ref _cuteBackgroundSkippedUnchangedCount);
        OnPropertyChanged(nameof(CuteBackgroundSkippedUnchangedCount));
    }

    public void RecordCuteBackgroundImageReloaded()
    {
        Interlocked.Increment(ref _cuteBackgroundImageReloadCount);
        OnPropertyChanged(nameof(CuteBackgroundImageReloadCount));
    }

    public void RecordCuteBackgroundApplyResult(
        bool fileExists,
        bool loaded,
        string? error,
        string? source = null,
        string? bundledPath = null)
    {
        Interlocked.Increment(ref _cuteBackgroundApplyCount);
        _cuteBackgroundLastAppliedTimeText = FormatDiagnosticTime(DateTimeOffset.Now);
        _cuteBackgroundFileExists = fileExists;
        _cuteBackgroundLoaded = loaded;
        if (source is not null)
        {
            _cuteBackgroundSource = string.IsNullOrWhiteSpace(source) ? "none" : source.Trim();
        }

        if (bundledPath is not null)
        {
            _cuteBackgroundBundledPath = bundledPath.Trim();
        }

        _cuteBackgroundLoadError = error?.Trim() ?? string.Empty;

        if (CuteBackgroundMode && !string.IsNullOrWhiteSpace(_cuteBackgroundLoadError))
        {
            SetStatus(_cuteBackgroundLoadError);
            if (!loaded)
            {
                _lastBackgroundError = _cuteBackgroundLoadError;
            }
            else
            {
                _lastBackgroundError = string.Empty;
            }
        }
        else if (loaded)
        {
            _lastBackgroundError = string.Empty;
        }

        OnPropertyChanged(nameof(CuteBackgroundLastAppliedTimeText));
        OnPropertyChanged(nameof(CuteBackgroundApplyCount));
        OnPropertyChanged(nameof(CuteBackgroundFileExists));
        OnPropertyChanged(nameof(CuteBackgroundLoaded));
        OnPropertyChanged(nameof(CuteBackgroundSource));
        OnPropertyChanged(nameof(CuteBackgroundBundledPath));
        OnPropertyChanged(nameof(CuteBackgroundLoadError));
        RefreshDiagnostics();
    }

    public void RecordCuteBackgroundLoadResult(bool fileExists, bool loaded, string? error)
    {
        RecordCuteBackgroundApplyResult(fileExists, loaded, error);
    }

    public bool ShowMockTestPort
    {
        get => _currentUiSettings.ShowMockTestPort;
        set
        {
            if (_currentUiSettings.ShowMockTestPort == value)
            {
                return;
            }

            _currentUiSettings.ShowMockTestPort = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentPortIsMock));
            RecordSettingsChange("Show MOCK test port", SettingsApplyBehavior.Immediate, value ? "shown" : "hidden");

            if (!value && CurrentPortIsMock)
            {
                SetStatus(IsConnected
                    ? "MOCK test port hidden. Current MOCK connection stays active until disconnect."
                    : "MOCK test port hidden.");
            }

            _ = RefreshPortsAsync();
            RefreshDiagnostics();
        }
    }

    public string ConnectionButtonText => IsConnected ? "Disconnect" : "Connect";

    public string PauseRenderingButtonText => IsViewPauseTransitioning
        ? "Pausing View"
        : IsViewFullyPaused
            ? "Resume Live"
            : "Pause View";

    public string CompactPauseRenderingButtonText => IsLogRenderingPaused ? ">" : "||";

    public string CompactPauseRenderingButtonGlyph => IsLogRenderingPaused ? "\uE768" : "\uE769";

    public string PauseRenderingToolTip => IsViewPauseTransitioning
        ? "Finishing display work accepted before the pause boundary. RX, file logging, and events continue."
        : IsViewFullyPaused
        ? "Resume live display. Data received while paused is not replayed in the view."
        : FileLoggingWhileViewPaused
            ? "Freeze the current view. RX, parsing, events, and file logging continue; paused data is not replayed."
            : "Freeze the current view. RX, parsing, and events continue; paused data is omitted from both the view and file log.";

    public string RenderingPauseReason => IsViewPauseTransitioning
        ? "view pause drain"
        : IsViewFullyPaused
            ? "view paused"
        : _isXtermAppendBackpressureActive
            ? "xterm append backlog"
            : "none";

    public string ConnectionStateText =>
        $"{_serialService.ConnectionState}: {SelectedPort ?? "(no port selected)"} @ {SelectedBaudRate:N0} bps";

    public string CompactConnectionStatusText => string.IsNullOrWhiteSpace(StatusText)
        ? ConnectionStateText
        : $"{ConnectionStateText} | {StatusText}";

    public string RenderingStateText => IsVisualAppendSuspendedForMinimize
        ? "Rendering suspended: minimized"
        : IsViewPauseTransitioning
            ? "Rendering state: pausing view"
            : IsViewFullyPaused
                ? "Rendering paused: view paused"
            : _isXtermAppendBackpressureActive
                ? "Rendering catching up: xterm backlog"
            : "Rendering live";

    public int PendingVisualLineCount => _logBatchDispatcher.PendingItemCount;

    public long CurrentViewPauseOmittedLineCount
    {
        get { lock (_viewPauseGate) return _viewPause.CurrentOmittedFromView; }
    }

    public long TotalViewPauseOmittedLineCount
    {
        get { lock (_viewPauseGate) return _viewPause.TotalOmittedFromView; }
    }

    public long ViewPauseCount
    {
        get { lock (_viewPauseGate) return _viewPause.PauseCount; }
    }

    public string LastViewPauseSummary
    {
        get { lock (_viewPauseGate) return _viewPause.LastSummary; }
    }

    public long VisualDispatcherFlushCount => _logBatchDispatcher.FlushCount;

    public int MaxVisualDispatcherBatchSize => _logBatchDispatcher.MaxBatchSize;

    public bool SmoothLogRenderingEnabled => true;

    public int VisualRenderIntervalMs => SmoothVisualRenderIntervalMs;

    public int VisualDrainBatchSize => SmoothVisualAppendMaxLines;

    public int VisualDrainMaxChars => SmoothVisualAppendMaxChars;

    public int LastVisualAppendLineCount => Volatile.Read(ref _lastVisualAppendLineCount);

    public int MaxVisualAppendLineCount => Volatile.Read(ref _maxVisualAppendLineCount);

    public long VisualAppendBatchCount => Interlocked.Read(ref _visualAppendBatchCount);

    public long MaxVisualBacklogLineCount => Interlocked.Read(ref _maxVisualBacklogLineCount);

    public string ActiveLogViewModeText => "xterm.js";

    public int ActiveHighlightRuleCount =>
        LogRules.Count(rule =>
            rule.Enabled &&
            rule.Mode == ToLogRuleMode(SelectedRxDisplayMode) &&
            rule.UseForHighlight &&
            !string.IsNullOrWhiteSpace(rule.Keyword));

    public int ActiveEventLogRuleCount =>
        LogRules.Count(rule =>
            rule.Enabled &&
            rule.Mode == ToLogRuleMode(SelectedRxDisplayMode) &&
            rule.UseForEvent &&
            !string.IsNullOrWhiteSpace(rule.Keyword));

    public int ActiveViewFilterRuleCount =>
        LogRules.Count(rule =>
            rule.Enabled &&
            rule.Mode == ToLogRuleMode(SelectedRxDisplayMode) &&
            rule.UseAsViewFilter &&
            !string.IsNullOrWhiteSpace(rule.Keyword));

    public int TerminalLogRuleCount =>
        LogRules.Count(rule => rule.Mode == LogRuleMatchMode.Terminal);

    public int HexLogRuleCount =>
        LogRules.Count(rule => rule.Mode == LogRuleMatchMode.Hex);

    public int InvalidHexLogRuleCount =>
        LogRules.Count(IsInvalidHexLogRule);

    public string LastInvalidHexLogRuleName =>
        LogRules.LastOrDefault(IsInvalidHexLogRule) is { } rule
            ? FormatRuleName(rule)
            : string.Empty;

    public string LastInvalidHexLogRuleError =>
        LogRules.LastOrDefault(IsInvalidHexLogRule) is { } rule
            ? GetHexRuleParseError(rule)
            : string.Empty;

    public bool IsMockStressRunning => _serialService.IsMockStressRunning;

    public string MockStressStatusText => _serialService.MockGeneratorPattern switch
    {
        MockGeneratorPattern.NormalLines =>
            $"{_serialService.MockStressStatus}; Missing sequences: {MockMissingSequenceCount:N0}",
        MockGeneratorPattern.VisualHexPackets =>
            $"{_serialService.MockStressStatus}; Packets: {MockGeneratedLineCount:N0}",
        _ => $"{_serialService.MockStressStatus}; z bytes: {MockNoNewlineEmittedBytes:N0}"
    };

    public long MockGeneratedLineCount => _serialService.MockGeneratedLineCount;

    public bool IsMockNoNewlineActive => _serialService.IsMockNoNewlineActive;

    public long MockNoNewlineEmittedBytes => _serialService.MockNoNewlineEmittedBytes;

    public long MockExpectedSequence => Interlocked.Read(ref _mockExpectedSequence);

    public long MockLastGeneratedSequence => _serialService.MockLastGeneratedSequence;

    public long MockLastParsedSequence => Interlocked.Read(ref _mockLastParsedSequence);

    public long MockMissingSequenceCount => Interlocked.Read(ref _mockMissingSequenceCount);

    public long MockDuplicateSequenceCount => Interlocked.Read(ref _mockDuplicateSequenceCount);

    public long MockOutOfOrderSequenceCount => Interlocked.Read(ref _mockOutOfOrderSequenceCount);

    public long MockMalformedSequenceCount => Interlocked.Read(ref _mockMalformedSequenceCount);

    public string LastMockSequenceError => _lastMockSequenceError;

    public bool IsXtermReady
    {
        get => _isXtermReady;
        private set => SetProperty(ref _isXtermReady, value);
    }

    public long SentCommandCount => Interlocked.Read(ref _sentCommandCount);

    public long TxErrorCount => Interlocked.Read(ref _txErrorCount);

    public long MarkerCount => Interlocked.Read(ref _markerCount);

    public long MarkerInsertErrorCount => Interlocked.Read(ref _markerInsertErrorCount);

    public long XtermAppendedLineCount => Interlocked.Read(ref _xtermAppendedLineCount);

    public long XtermAppendBatchCount => Interlocked.Read(ref _xtermAppendBatchCount);

    public long XtermAppendErrorCount => Interlocked.Read(ref _xtermAppendErrorCount);

    public long XtermPendingCharacterCount => Interlocked.Read(ref _xtermPendingCharacterCount);

    public long MaxXtermPendingCharacterCount => Interlocked.Read(ref _maxXtermPendingCharacterCount);

    public bool IsWindowMinimized => _isWindowMinimized;

    public bool IsVisualAppendSuspendedForMinimize => _isVisualAppendSuspendedForMinimize;

    public bool XtermNeedsFullRerenderAfterRestore => _xtermNeedsFullRerenderAfterRestore;

    public string LastWindowMinimizeTimeText => _lastWindowMinimizeTimeText;

    public string LastWindowRestoreTimeText => _lastWindowRestoreTimeText;

    public string RestoreRenderStartedTimeText => _restoreRenderStartedTimeText;

    public string RestoreRenderCompletedTimeText => _restoreRenderCompletedTimeText;

    public string RestoreRenderDurationText => _restoreRenderDurationText;

    public int RestoreRenderedLineCount => Volatile.Read(ref _restoreRenderedLineCount);

    public string LastRestoreRenderMode => _lastRestoreRenderMode;

    public long RestoreFullRerenderSuppressedCount => Interlocked.Read(ref _restoreFullRerenderSuppressedCount);

    public long WindowActivationRerenderSuppressedCount => Interlocked.Read(ref _windowActivationRerenderSuppressedCount);

    public long LastRenderedSequenceId => Interlocked.Read(ref _lastRenderedSequenceId);

    public long PendingVisualDeltaLineCount => Interlocked.Read(ref _pendingVisualDeltaLineCount);

    public bool IsFullXtermRerenderInProgress => _isFullXtermRerenderInProgress;

    public string LastFullXtermRerenderReason => _lastFullXtermRerenderReason;

    public int LastFullXtermRerenderLineCount => Volatile.Read(ref _lastFullXtermRerenderLineCount);

    public string LastFullXtermRerenderDurationText => _lastFullXtermRerenderDurationText;

    public bool LastFullXtermScrollRestoreAttempted => _lastFullXtermScrollRestoreAttempted;

    public string LastFullXtermFinalScrollAction => _lastFullXtermFinalScrollAction;

    public long SuppressedIntermediateAutoScrollCount => Interlocked.Read(ref _suppressedIntermediateAutoScrollCount);

    public long FullXtermRerenderRequestCount => Interlocked.Read(ref _fullXtermRerenderRequestCount);

    public long FullXtermRerenderCoalescedCount => Interlocked.Read(ref _fullXtermRerenderCoalescedCount);

    public long FullXtermRerenderCanceledCount => Interlocked.Read(ref _fullXtermRerenderCanceledCount);

    public long LastFullXtermRerenderGeneration => Interlocked.Read(ref _lastFullXtermRerenderGeneration);

    public int LastFullXtermClearCount => Volatile.Read(ref _lastFullXtermClearCount);

    public int LastFullXtermVisibilityToggleCount => Volatile.Read(ref _lastFullXtermVisibilityToggleCount);

    public string LastFullXtermRerenderError => _lastFullXtermRerenderError;

    public long MinimizedVisualCoalescedLineCount => Interlocked.Read(ref _minimizedVisualCoalescedLineCount);

    public long MinimizedVisualCoalescedCharacterCount => Interlocked.Read(ref _minimizedVisualCoalescedCharacterCount);

    public long MaxMinimizedVisualCoalescedLineCount => Interlocked.Read(ref _maxMinimizedVisualCoalescedLineCount);

    public long MaxMinimizedVisualCoalescedCharacterCount => Interlocked.Read(ref _maxMinimizedVisualCoalescedCharacterCount);

    public int SuspendedXtermPendingLineCount => Volatile.Read(ref _suspendedXtermPendingLineCount);

    public long SuspendedXtermPendingCharacterCount => Interlocked.Read(ref _suspendedXtermPendingCharacterCount);

    public long SuspendedXtermQueueCollapseCount => Interlocked.Read(ref _suspendedXtermQueueCollapseCount);

    public string LastSuspendedXtermQueueCollapseReason => _lastSuspendedXtermQueueCollapseReason;

    public int LastXtermAppendLineCount => Volatile.Read(ref _lastXtermAppendLineCount);

    public int LastXtermAppendCharacterCount => Volatile.Read(ref _lastXtermAppendCharacterCount);

    public int MaxXtermAppendLineCount => Volatile.Read(ref _maxXtermAppendLineCount);

    public int MaxXtermAppendCharacterCount => Volatile.Read(ref _maxXtermAppendCharacterCount);

    public string LastXtermAppendDurationText => $"{Interlocked.Read(ref _lastXtermAppendDurationMs):N0} ms";

    public string MaxXtermAppendDurationText => $"{Interlocked.Read(ref _maxXtermAppendDurationMs):N0} ms";

    public long XtermBackpressureEventAutoScrollSuppressedCount =>
        Interlocked.Read(ref _xtermBackpressureEventAutoScrollSuppressedCount);

    public long XtermBackpressureAutoScrollSuppressedCount =>
        Interlocked.Read(ref _xtermBackpressureAutoScrollSuppressedCount);

    public long XtermBackpressureFullRerenderDeferredCount =>
        Interlocked.Read(ref _xtermBackpressureFullRerenderDeferredCount);

    public long XtermCopyRequestCount => Interlocked.Read(ref _xtermCopyRequestCount);

    public long XtermCopiedCharacterCount => Interlocked.Read(ref _xtermCopiedCharacterCount);

    public long XtermCopyErrorCount => Interlocked.Read(ref _xtermCopyErrorCount);

    public long XtermSearchRequestCount => Interlocked.Read(ref _xtermSearchRequestCount);

    public long XtermSearchHitCount => Interlocked.Read(ref _xtermSearchHitCount);

    public long XtermSearchErrorCount => Interlocked.Read(ref _xtermSearchErrorCount);

    public string LastSentCommandText => _lastSentCommandText;

    public TxSendMode LastTxMode => _lastTxMode;

    public string LastTxRawInput => _lastTxRawInput;

    public int LastTxByteCount => _lastTxByteCount;

    public string LastTxHexParseError => _lastTxHexParseError;

    public string LastMarkerText => _lastMarkerText;

    public string LastMarkerAction => _lastMarkerAction;

    public string LastMarkerError => _lastMarkerError;

    public string SessionName
    {
        get => _sessionName;
        set
        {
            if (SetProperty(ref _sessionName, value))
            {
                RecordSettingsChange("Log file name", SettingsApplyBehavior.Immediate, string.IsNullOrWhiteSpace(value) ? "(automatic)" : value);
                OnPropertyChanged(nameof(LogFileName));
                OnPropertyChanged(nameof(ConfiguredLogFileNameDisplayText));
            }
        }
    }

    public string LogFileName
    {
        get => SessionName;
        set => SessionName = value;
    }

    public string ConfiguredLogFileNameDisplayText => string.IsNullOrWhiteSpace(LogFileName)
        ? "(automatic timestamp name)"
        : LogFileName;

    public string CurrentSessionName
    {
        get => _currentSessionName;
        private set
        {
            if (SetProperty(ref _currentSessionName, value))
            {
                OnPropertyChanged(nameof(CurrentSessionDisplayText));
                OnPropertyChanged(nameof(ActiveLogFileName));
                EndSessionCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string CurrentSessionDisplayText => string.IsNullOrWhiteSpace(CurrentSessionName)
        ? "(none)"
        : CurrentSessionName;

    public string SessionStartedTimeText => _sessionStartedTime?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "(none)";

    public string LastSessionAction => _lastSessionAction;

    public string LastSessionError => _lastSessionError;

    public long SessionErrorCount => Interlocked.Read(ref _sessionErrorCount);

    public string ActiveLogFileName => CurrentSessionName;

    public string LastTxError => _lastTxError;

    public string LastXtermAppendError => _lastXtermAppendError;

    public string LastXtermCopyError => _lastXtermCopyError;

    public string LastXtermSearchError => _lastXtermSearchError;

    public string LastSentCommandTimeText => _lastSentCommandTime?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "(none)";

    public string LastMarkerTimeText => _lastMarkerTime?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "(none)";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                if (!_isApplyingSearchHistory)
                {
                    ResetSearchHistoryNavigation();
                }

                _currentUiSettings.LastSearchText = value?.Trim() ?? string.Empty;
                InvalidateSearchResultsForCriteriaChange();
                RecordSettingsChange("Search text", SettingsApplyBehavior.Immediate, string.IsNullOrWhiteSpace(value) ? "(empty)" : value.Trim());
                NotifySearchCommandStates();
            }
        }
    }

    public bool IsSearchCaseSensitive
    {
        get => _isSearchCaseSensitive;
        set
        {
            if (SetProperty(ref _isSearchCaseSensitive, value))
            {
                _currentUiSettings.SearchCaseSensitive = value;
                InvalidateSearchResultsForCriteriaChange();
                RecordSettingsChange("Search case", SettingsApplyBehavior.Immediate, value ? "case-sensitive" : "case-insensitive");
            }
        }
    }

    public bool AreSearchResultsStale
    {
        get => _areSearchResultsStale;
        private set => SetProperty(ref _areSearchResultsStale, value);
    }

    public int SearchMatchCount
    {
        get => _searchMatchCount;
        private set
        {
            if (SetProperty(ref _searchMatchCount, value))
            {
                OnPropertyChanged(nameof(SearchSummaryText));
            }
        }
    }

    public int CurrentSearchMatchIndex
    {
        get => _currentSearchMatchIndex;
        private set
        {
            if (SetProperty(ref _currentSearchMatchIndex, value))
            {
                OnPropertyChanged(nameof(SearchSummaryText));
            }
        }
    }

    public string CurrentSearchMatchedLine
    {
        get => _currentSearchMatchedLine;
        private set => SetProperty(ref _currentSearchMatchedLine, value);
    }

    public long SearchErrorCount => Interlocked.Read(ref _searchErrorCount);

    public string LastSearchError => _lastSearchError;

    public string SearchSummaryText => SearchMatchCount == 0
        ? "0 matches"
        : $"{CurrentSearchMatchIndex:N0} / {SearchMatchCount:N0}";

    public string SearchResultStatusText
    {
        get => _searchResultStatusText;
        private set => SetProperty(ref _searchResultStatusText, value);
    }

    public int SearchResultVisibleCount => SearchResults.Count;

    public VisibleSearchResult? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set
        {
            if (SetProperty(ref _selectedSearchResult, value))
            {
                OnPropertyChanged(nameof(SelectedSearchResultIndex));
            }
        }
    }

    public int SelectedSearchResultIndex => SelectedSearchResult?.MatchIndex ?? 0;

    public long SearchResultBuildErrorCount => Interlocked.Read(ref _searchResultBuildErrorCount);

    public long SearchResultJumpErrorCount => Interlocked.Read(ref _searchResultJumpErrorCount);

    public long SearchResultsRebuildCount => Interlocked.Read(ref _searchResultsRebuildCount);

    public long SearchResultSelectionLostCount => Interlocked.Read(ref _searchResultSelectionLostCount);

    public string LastSearchResultBuildError => _lastSearchResultBuildError;

    public string LastSearchResultJumpError => _lastSearchResultJumpError;

    public string LastSearchShortcutAction => _lastSearchShortcutAction;

    public string LastSearchShortcutSource => _lastSearchShortcutSource;

    public string LastSearchShortcutTimeText => _lastSearchShortcutTime?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "(none)";

    public long SearchShortcutErrorCount => Interlocked.Read(ref _searchShortcutErrorCount);

    public string LastSearchShortcutError => _lastSearchShortcutError;

    public DetectedEvent? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (SetProperty(ref _selectedEvent, value))
            {
                _lastEventSelectionError = string.Empty;
                RefreshSelectedEventContextText();
                OnPropertyChanged(nameof(SelectedEventContextAvailable));
                OnPropertyChanged(nameof(SelectedEventRuleName));
                OnPropertyChanged(nameof(SelectedEventContextLineCount));
                OnPropertyChanged(nameof(SelectedEventContextHeaderText));
                OnPropertyChanged(nameof(LastEventSelectionError));
                CopyEventContextCommand.NotifyCanExecuteChanged();
                RefreshDiagnostics();
            }
        }
    }

    public string SelectedEventContextText
    {
        get => _selectedEventContextText;
        private set => SetProperty(ref _selectedEventContextText, value);
    }

    public string SelectedEventContextStatusText
    {
        get => _selectedEventContextStatusText;
        private set => SetProperty(ref _selectedEventContextStatusText, value);
    }

    public string SelectedEventContextHeaderText
    {
        get
        {
            if (SelectedEvent is null)
            {
                return "Select an event.";
            }

            return $"{SelectedEvent.RuleName} | {SelectedEvent.CompactDirectionText} | {SelectedEvent.TimeText} | {SelectedEventContextStatusText}";
        }
    }

    public bool SelectedEventContextAvailable =>
        SelectedEvent is not null && _eventContextsById.ContainsKey(SelectedEvent.Id);

    public bool IsEventAutoScrollEnabled
    {
        get => _isEventAutoScrollEnabled;
        set
        {
            if (SetProperty(ref _isEventAutoScrollEnabled, value))
            {
                _currentUiSettings.EventAutoScrollEnabled = value;
                OnPropertyChanged(nameof(IsEventAutoScrollSuppressedByXtermBackpressure));
                RecordSettingsChange("Event auto-scroll", SettingsApplyBehavior.Immediate, value ? "enabled" : "disabled");
            }
        }
    }

    public int DetectedEventUiItemCount => Events.CurrentVisibleEventCount;

    private int RetainedEventContextLimit => Math.Min(
        MaxRetainedEventContexts,
        Math.Max(DefaultVisibleEventCount, MaxVisibleEventCount));

    public int PendingEventUiCount => _eventBatchDispatcher.PendingItemCount;

    public long EventUiFlushCount => _eventBatchDispatcher.FlushCount;

    public int MaxEventUiBatchSize => _eventBatchDispatcher.MaxBatchSize;

    public string DetectedEventUiCountText => $"{DetectedEventUiItemCount:N0} visible";

    public string SelectedEventRuleName => SelectedEvent?.RuleName ?? "(none)";

    public int SelectedEventContextLineCount
    {
        get
        {
            if (SelectedEvent is null)
            {
                return 0;
            }

            return _eventContextsById.TryGetValue(SelectedEvent.Id, out var context)
                ? context.BeforeContextLines.Count + 1 + context.AfterContextLines.Count
                : SelectedEvent.BeforeContextLines.Count + 1 + SelectedEvent.AfterContextLines.Count;
        }
    }

    public long CopiedEventContextCount => Interlocked.Read(ref _copiedEventContextCount);

    public long EventContextUiErrorCount => Interlocked.Read(ref _eventContextUiErrorCount);

    public long EventContextUiDroppedCount => Interlocked.Read(ref _eventContextUiDroppedCount);

    public int PendingEventContextUiCount => _eventContextBatchDispatcher.PendingItemCount;

    public string LastEventContextUiError => _lastEventContextUiError;

    public long EventSelectionErrorCount => Interlocked.Read(ref _eventSelectionErrorCount);

    public string LastEventSelectionError => _lastEventSelectionError;

    public long LatestEventSelectCount => Interlocked.Read(ref _latestEventSelectCount);

    public long EventListScrollErrorCount => Interlocked.Read(ref _eventListScrollErrorCount);

    public string LastEventListScrollError => _lastEventListScrollError;

    public long EventListIncrementalUpdateCount => Interlocked.Read(ref _eventListIncrementalUpdateCount);

    public long EventListResetCount => Interlocked.Read(ref _eventListResetCount);

    public long EventSelectionPreservedCount => Interlocked.Read(ref _eventSelectionPreservedCount);

    public long EventSelectionLostCount => Interlocked.Read(ref _eventSelectionLostCount);

    public long ListUpdateErrorCount => Interlocked.Read(ref _listUpdateErrorCount);

    public string LastListUpdateError => _lastListUpdateError;

    public string ActiveInspectorTabText
    {
        get => _activeInspectorTabText;
        private set => SetProperty(ref _activeInspectorTabText, value);
    }

    public long InspectorTabLayoutErrorCount => Interlocked.Read(ref _inspectorTabLayoutErrorCount);

    public string LastInspectorTabLayoutError => _lastInspectorTabLayoutError;

    public long SearchTabLayoutErrorCount => Interlocked.Read(ref _searchTabLayoutErrorCount);

    public string LastSearchTabLayoutError => _lastSearchTabLayoutError;

    public long ContextRefreshCount => Interlocked.Read(ref _contextRefreshCount);

    public long ContextRefreshErrorCount => Interlocked.Read(ref _contextRefreshErrorCount);

    public string LastContextRefreshError => _lastContextRefreshError;

    public long ContextTabActivatedCount => Interlocked.Read(ref _contextTabActivatedCount);

    public long ContextVisualRefreshCount => Interlocked.Read(ref _contextVisualRefreshCount);

    public string LastContextVisualRefreshTimeText => _lastContextVisualRefreshTimeText;

    public string LastContextVisualRefreshEventId => _lastContextVisualRefreshEventId;

    public string LastContextVisualRefreshEventSummary => _lastContextVisualRefreshEventSummary;

    public int LastContextVisualRefreshTextLength => _lastContextVisualRefreshTextLength;

    public long ContextRenderErrorCount => Interlocked.Read(ref _contextRenderErrorCount);

    public string LastContextRenderError => _lastContextRenderError;

    public bool IsContextWebViewReady
    {
        get => _isContextWebViewReady;
        private set => SetProperty(ref _isContextWebViewReady, value);
    }

    public long ContextWebViewUpdateCount => Interlocked.Read(ref _contextWebViewUpdateCount);

    public long ContextWebViewUpdateErrorCount => Interlocked.Read(ref _contextWebViewUpdateErrorCount);

    public string LastContextWebViewUpdateTimeText => _lastContextWebViewUpdateTimeText;

    public string LastContextWebViewUpdateEventSummary => _lastContextWebViewUpdateEventSummary;

    public int LastContextWebViewTextLength => _lastContextWebViewTextLength;

    public string LastContextWebViewUpdateError => _lastContextWebViewUpdateError;

    public long XtermFitResizeCount => Interlocked.Read(ref _xtermFitResizeCount);

    public long XtermLayoutErrorCount => Interlocked.Read(ref _xtermLayoutErrorCount);

    public string LastXtermLayoutError => _lastXtermLayoutError;

    public int LastAppliedXtermScrollbackSize => Volatile.Read(ref _lastAppliedXtermScrollbackSize);

    public string LastAutoScrollActionTimeText => _lastAutoScrollActionTimeText;

    public string LastAutoScrollError => _lastAutoScrollError;

    public string XtermAtBottomText => _lastXtermAtBottom.HasValue
        ? _lastXtermAtBottom.Value.ToString(CultureInfo.InvariantCulture)
        : "(unknown)";

    public int LogRuleEditorCount => LogRules.Count;

    public int EventRuleEditorCount => LogRules.Count(rule => rule.UseForEvent);

    public int HighlightRuleEditorCount => LogRules.Count(rule => rule.UseForHighlight);

    public string RuleMigrationResult => _profileService.LastRuleMigrationResult;

    public int SavedCommandEditorCount => Commands.SavedCommands.Count;

    public long RuleEditErrorCount => Interlocked.Read(ref _ruleEditErrorCount);

    public long CommandEditErrorCount => Interlocked.Read(ref _commandEditErrorCount);

    public string LastRuleEditStatus => _lastRuleEditStatus;

    public string LastRuleEditError => _lastRuleEditError;

    public string LastRuleColorChange => _lastRuleColorChange;

    public string LastRuleColorChangeError => _lastRuleColorChangeError;

    public long RuleColorChangeErrorCount => Interlocked.Read(ref _ruleColorChangeErrorCount);

    public long AutomaticRuleRerenderSuppressedCount => Interlocked.Read(ref _automaticRuleRerenderSuppressedCount);

    public long RuleChangesSinceClearCount => Interlocked.Read(ref _ruleChangesSinceClearCount);

    public string LastRuleChangeLiveOnlyTimeText => _lastRuleChangeLiveOnlyTimeText;

    public long InvalidRuleColorFallbackCount =>
        Interlocked.Read(ref _invalidRuleColorFallbackCount) + _profileService.InvalidRuleColorFallbackCount;

    public int UnifiedLogRuleColorCount => LogRules
        .Select(rule => NormalizeRuleColorForCount(rule.ForegroundColor))
        .Where(color => !string.IsNullOrWhiteSpace(color))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    public string LastCommandEditStatus => _lastCommandEditStatus;

    public string LastCommandEditError => _lastCommandEditError;

    public string CurrentProfilePath => _profileService.DefaultProfilePath;

    public string ProfileStatusText => _profileService.LastStatus;

    public string ProfileLastError => _profileService.LastError ?? string.Empty;

    public long ProfileLoadErrorCount => _profileService.LoadErrorCount;

    public long ProfileSaveErrorCount => _profileService.SaveErrorCount;

    public string CurrentSerialLogPath =>
        _fileLogWriter.CurrentLogFilePath ?? _fileLogWriter.LastLogFilePath ?? string.Empty;

    public string LastLogToggleAction => _lastLogToggleAction;

    public string LastLogToggleTimeText => _lastLogToggleTimeText;

    public string LastLogToggleError => _lastLogToggleError;

    public long LogToggleErrorCount => Interlocked.Read(ref _logToggleErrorCount);

    public string LastLogFileActionStatus => _lastLogFileActionStatus;

    public long LogFileActionErrorCount => Interlocked.Read(ref _logFileActionErrorCount);

    public string LastLogFileActionError => _lastLogFileActionError;

    public string LastSaveDirectoryAction => _lastSaveDirectoryAction;

    public long SaveDirectoryBrowseErrorCount => Interlocked.Read(ref _saveDirectoryBrowseErrorCount);

    public string LastSaveDirectoryError => _lastSaveDirectoryError;

    public string LastSessionFileAction => _lastSessionFileAction;

    public long SessionFileNamingErrorCount => Interlocked.Read(ref _sessionFileNamingErrorCount);

    public string LastSessionFileNamingError => _lastSessionFileNamingError;

    public long SettingsApplyErrorCount => Interlocked.Read(ref _settingsApplyErrorCount);

    public string LastSettingsApplyError => _lastSettingsApplyError;

    public long SettingsValidationErrorCount => Interlocked.Read(ref _settingsValidationErrorCount);

    public string LastSettingsValidationError => _lastSettingsValidationError;

    public string LastNormalizedSetting => _lastNormalizedSetting;

    public long ProfileNormalizationCount => _profileService.ProfileNormalizationCount;

    public bool ProfileLoaded => _profileService.HasLoadedProfile;

    public string ProfileLoadTimeText => _profileService.LastLoadTime.HasValue
        ? FormatDiagnosticTime(_profileService.LastLoadTime.Value)
        : "(none)";

    public string ProfileSaveTimeText => _profileService.LastSaveTime.HasValue
        ? FormatDiagnosticTime(_profileService.LastSaveTime.Value)
        : "(none)";

    public long ProfileLoadCount => _profileService.LoadCount;

    public long ProfileSaveCount => _profileService.SaveCount;

    public int ProfileSchemaVersion => _profileService.LastSchemaVersion;

    public bool ProfileCuteBackgroundCustomPathCleared => _profileService.LastCuteBackgroundCustomPathCleared;

    public string ProfileCuteBackgroundCustomPathClearReason => _profileService.LastCuteBackgroundCustomPathClearReason;

    public string LastSettingsChange => _lastSettingsChange;

    public string LastSettingsApplyStatus => _lastSettingsApplyStatus;

    public int PendingReconnectRequiredSettingsCount => _pendingReconnectSettings.Count;

    public int PendingRestartRequiredSettingsCount => _pendingRestartSettings.Count;

    public string SettingsPendingSummaryText =>
        $"Pending: reconnect {PendingReconnectRequiredSettingsCount:N0}, next start {PendingRestartRequiredSettingsCount:N0}";

    public long StatusChangedThreadMarshalErrorCount => Interlocked.Read(ref _statusChangedThreadMarshalErrorCount);

    public string LastStatusChangedThreadMarshalError => _lastStatusChangedThreadMarshalError;

    public int DuplicateMockPortEntryCount
    {
        get => _duplicateMockPortEntryCount;
        private set => SetProperty(ref _duplicateMockPortEntryCount, value);
    }

    public bool CurrentPortIsMock => IsMockPortName(GetActualPortName(SelectedPort) ?? _currentSerialSettings.PortName);

    public bool CanStartMockStress => IsConnected && CurrentPortIsMock && !IsBusy && !IsMockStressRunning;

    public bool CanStopMockStress => IsMockStressRunning;

    public bool LastPortRefreshIncludedMock => _lastPortRefreshIncludedMock;

    public string LastSuccessfulPort => _lastSuccessfulPort;

    public int LastSuccessfulBaudRate => _lastSuccessfulBaudRate;

    public string LastPortSelectionChangeReason => _lastPortSelectionChangeReason;

    public bool LastDisconnectPreservedPort => _lastDisconnectPreservedPort;

    public string LastPortRefreshResult => _lastPortRefreshResult;

    public bool SelectedPortAvailable => _selectedPortAvailable;

    public int ActiveLogObserverTaskCount => _observeLogsTask is { IsCompleted: false } ? 1 : 0;

    public int ActiveEventObserverTaskCount => _observeEventsTask is { IsCompleted: false } ? 1 : 0;

    public int ActiveEventContextObserverTaskCount => _observeEventContextsTask is { IsCompleted: false } ? 1 : 0;

    public string LastConnectRequestedPort => _lastConnectRequestedPort;

    public int LastConnectRequestedBaud => _lastConnectRequestedBaud;

    public string LastConnectResult => _lastConnectResult;

    public string LastConnectFailureReason => _lastConnectFailureReason;

    public string LastConnectExceptionType => _lastConnectExceptionType;

    public string LastConnectFailureTimeText => _lastConnectFailureTimeText;

    public string SelectedPortAfterConnectFailure => _selectedPortAfterConnectFailure;

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(CompactConnectionStatusText));
            }
        }
    }

    public string FooterStatusText
    {
        get => _footerStatusText;
        private set => SetProperty(ref _footerStatusText, value);
    }

    public string DiagnosticsSummaryText
    {
        get => _diagnosticsSummaryText;
        private set => SetProperty(ref _diagnosticsSummaryText, value);
    }

    public string DiagnosticsText
    {
        get => _diagnosticsText;
        private set => SetProperty(ref _diagnosticsText, value);
    }

    public string HelpGuideText => """
        빠른 시작

        1. Port와 Baud를 선택하고 Connect를 누릅니다.
        2. 일반 명령은 TX Mode를 Terminal로 두고 전송합니다.
        3. 바이너리 패킷은 TX Mode를 HEX로 바꾸고 AA 55 01처럼 입력합니다.
        4. 장시간 기록이 필요하면 Log ON을 확인합니다.
        5. 설정을 유지하려면 Save Profile을 누릅니다.

        이벤트 만들기

        1. Rules 탭에서 +를 누릅니다.
        2. Name과 Keyword를 입력합니다.
        3. Enabled와 Event를 켭니다.
        4. 현재 Terminal 모드에서 쓸 룰은 Mode = Terminal, HEX 모드에서 쓸 룰은 Mode = HEX를 선택합니다.
        5. 일반적인 장비 이벤트는 Direction = RxOnly를 권장합니다.
        6. Save 후 새로 들어오는 로그에서 동작을 확인합니다.

        이벤트 확인

        Events에서 발생 시간과 메시지를 확인합니다.
        이벤트를 더블클릭하면 Context에서 전후 로그를 볼 수 있습니다.
        Tray, Sound, Popup 알림은 규칙 편집창에서 필요한 것만 켭니다.
        알림 기본값은 모두 OFF이며 기본 쿨다운은 30초입니다.

        명령 시퀀스

        1. Sequences 탭의 위쪽 +로 시퀀스를 만듭니다.
        2. 아래쪽 +로 명령 Step을 추가합니다.
        3. Command, Line ending, Delay after ms를 설정합니다.
        4. Up과 Dn으로 순서를 조정합니다.
        5. 장비 연결 후 Run으로 실행하고 Stop으로 중단합니다.

        현재 시퀀스는 명령과 지연 시간만 순서대로 실행합니다.
        장비 응답 판정, 조건 분기, HEX Step은 아직 지원하지 않습니다.

        COM Bridge

        실제 장비에 먼저 Connect합니다.
        Bridge 탭에서 com0com 쌍 중 앱이 사용할 포트를 선택하고 Start bridge를 누릅니다.
        외부 프로그램은 반드시 가상 포트 쌍의 반대편을 엽니다.
        BRIDGE ON 표시가 보이면 원본 바이트가 양방향으로 전달됩니다.
        외부 프로그램에서 장비로 보내는 로그는 현재 앱 모드를 따릅니다.
        Terminal 모드는 선택한 인코딩으로 디코딩하고 Terminal RxOnly/Both 규칙을 사용합니다.
        HEX 모드는 원본 바이트를 표시하고 HEX RxOnly/Both 규칙을 사용합니다.

        화면과 로그

        Pause View는 현재 화면을 고정하고 Pause 중 수신 로그를 화면에서 생략합니다. Resume Live 이후 새 로그부터 표시됩니다.
        Pause 중에도 RX, 파싱, 이벤트 검출은 계속되며 파일 저장 여부는 Log 탭의 View pause 옵션을 따릅니다.
        Clear는 화면만 지우며 저장 파일은 삭제하지 않습니다.
        RX View = HEX는 수신 원본 바이트 확인용입니다.
        HEX timeout은 마지막 바이트 이후 한 줄로 묶을 대기 시간이며 프로필 값이 그대로 사용됩니다.
        한 패킷 내부에서 관측되는 가장 긴 공백 < HEX timeout < 서로 다른 패킷 사이의 가장 짧은 공백으로 설정합니다.
        예: 내부 공백 최대 1ms, 패킷 사이 최소 3ms이면 2ms를 사용합니다. 두 범위가 겹치면 시간만으로 안정적인 구분이 불가능합니다.
        표시되는 권장값은 baud와 프레임 형식만 반영한 시작점이며 자동 적용되지 않습니다.
        연결 중 HEX timeout 또는 Terminal/HEX 모드를 바꾸면 네이티브 RX 적용을 위해 COM 포트를 재연결합니다.
        Diag의 Last RX chunk gap과 Last HEX group bytes를 기대 패킷 크기와 비교해 확인합니다.
        Health의 Drop 또는 Error가 증가하면 Diag 탭에서 원인을 확인합니다.

        단축키

        Ctrl+F  검색
        F3 / Shift+F3  다음 / 이전 결과
        Ctrl+C  선택 로그 복사
        Ctrl+M  MARK 삽입
        TX 입력창 ↑ / ↓  전송 기록 이동
        """;

    private string LegacyHelpGuideText => """
        빠른 시작

        * Port와 Baud를 선택한 뒤 Connect를 누릅니다.
        * TX 입력창에 명령어를 입력하고 Send를 누르면 전송됩니다.
        * 일반 RTOS 쉘 명령은 TX Mode = Terminal, RX View = Terminal을 사용합니다.
        * raw 패킷을 보고 보내려면 RX View = HEX, TX Mode = HEX를 사용합니다.
        * 설정을 바꾼 뒤 필요하면 Profile을 저장합니다.

        주요 단축키

        * Ctrl+F: 검색창으로 이동
        * Enter: 검색 다음 결과
        * Shift+Enter: 검색 이전 결과
        * F3: 검색 다음 결과
        * Shift+F3: 검색 이전 결과
        * Esc: 검색창 포커스 해제 또는 로그창으로 복귀
        * Ctrl+C: 로그에서 선택한 텍스트 복사
        * Ctrl+M: 기본 MARK 삽입
        * TX 입력창에서 ↑ / ↓: 이전/다음 전송 기록 불러오기

        로그 화면

        * RX는 수신 데이터, TX는 전송 데이터, MARK는 사용자가 찍은 구분선입니다.
        * Auto Scroll ON: 새 로그가 오면 자동으로 맨 아래로 따라갑니다.
        * Auto Scroll OFF: 과거 로그를 보는 중에 화면이 아래로 끌려가지 않습니다.
        * Pause View: 현재 화면을 고정하고 Pause 중 수신 로그는 화면에 보관하지 않습니다. Resume Live 이후 새 로그부터 표시합니다.
        * 수신, 파싱, 이벤트 검출은 Pause 중에도 계속되며 파일 저장은 View pause의 Keep saving file log 옵션을 따릅니다.
        * 최소화 중에도 수신, 저장, 이벤트 감지는 계속 동작합니다.
        * 최소화 중에는 화면 렌더링만 일시 중지될 수 있고, 복원하면 최신 화면 버퍼를 다시 그립니다.
        * Clear는 화면만 지웁니다. 저장된 로그 파일이나 카운터는 삭제하지 않습니다.
        * Copy since last TX: 마지막 TX 이후의 화면 로그를 복사합니다.
        * Copy since last MARK: 마지막 MARK 이후의 화면 로그를 복사합니다.

        Log Save

        * Log ON: RX/TX/MARK와 이벤트 로그를 파일로 저장합니다.
        * Log OFF: 화면 표시, 검색, 이벤트, 필터, TX는 그대로 동작하지만 새 로그를 파일로 저장하지 않습니다.
        * Log OFF로 바꿔도 기존 로그 파일은 삭제되지 않습니다.
        * 현재 저장 여부는 상단 Log ON/OFF와 하단 File ON/OFF에서 확인합니다.

        RX View

        * Terminal: 일반 쉘처럼 보여줍니다. 평소에는 이 모드를 사용합니다.
        * HEX: 받은 raw byte를 16진수로 보여줍니다.
        * 예: 실제 바이트 49 4E은 Terminal에서는 IN, HEX에서는 49 4E로 보입니다.
        * 개행이 없는 연속 출력도 일정 간격으로 화면에 표시됩니다.
        * 성능을 위해 바이트마다 갱신하지 않고 작은 덩어리로 묶어 표시합니다.
        * TAB 바이트 09는 Terminal에서 탭 간격처럼 표시됩니다.
        * 화면에 \t가 보이면 장치가 실제로 백슬래시와 t를 보냈을 수 있습니다.

        TX Mode

        * Terminal: 일반 텍스트 명령을 보냅니다. TX Ending이 적용됩니다.
        * HEX: 입력한 16진수 바이트를 그대로 보냅니다.
        * HEX 예시: 49 4E 0D 0A
        * HEX 모드에서는 TX Ending이 무시됩니다. 엔터가 필요하면 0D 0A를 직접 입력합니다.
        * Terminal 모드에서는 \t 같은 escape 문자를 해석하지 않습니다. TAB을 보내려면 HEX 모드에서 09를 입력합니다.

        Search

        * 검색은 현재 화면에 유지된 로그 버퍼를 대상으로 합니다.
        * 전체 로그 파일을 검색하는 기능은 아닙니다.
        * 검색 결과 이동은 Enter, Shift+Enter, F3, Shift+F3을 사용합니다.

        Visible Log Max Lines

        * 앱 안에서 최근 몇 줄을 검색/복사/필터용으로 유지할지 정합니다.
        * 값이 클수록 더 오래된 로그를 앱에서 볼 수 있지만 메모리를 더 사용합니다.
        * 장기 기록은 Log Save ON으로 파일에 저장하는 것이 좋습니다.

        이벤트 규칙 만드는 방법

        1. Rules 탭을 열고 상단의 + 버튼을 누릅니다.
        2. Name에는 사람이 알아보기 쉬운 이름을 입력합니다. 예: Boot Error
        3. Keyword에는 실제로 찾을 값을 입력합니다. 예: ERROR 또는 AA 55
        4. Enabled와 Event를 켭니다. Event를 끄면 Events 탭에 기록되지 않습니다.
        5. 필요하면 Highlight를 켜서 로그에 색상을 적용합니다.
        6. 필요하면 Filter를 켜서 로그 상단 Filter 목록에서 이 규칙만 볼 수 있게 합니다.
        7. Save를 누른 뒤 새로 수신되는 로그부터 규칙이 적용되는지 확인합니다.
        8. 규칙을 계속 사용할 경우 Save Profile을 눌러 저장합니다.

        이벤트 규칙 옵션

        * Mode = Terminal: 앱이 Terminal 모드일 때만 동작하며 디코딩된 문자열에서 찾습니다. 예: ERROR, WARN, boot complete
        * Mode = HEX: 앱이 HEX 모드일 때만 동작하며 원본 바이트에서 찾습니다. 예: AA 55 01, 49 4E
        * Direction = RxOnly: 장비에서 받은 로그만 검사합니다. 일반적인 이벤트 규칙은 이 값을 권장합니다.
        * Direction = TxOnly: 앱에서 보낸 TX만 검사합니다.
        * Direction = Both: RX와 TX를 모두 검사합니다.
        * Case sensitive: Terminal 규칙의 대소문자를 구분합니다. HEX 규칙에서는 무시됩니다.
        * Priority: 여러 Highlight 규칙이 동시에 일치할 때 높은 값의 색상을 우선 적용합니다.
        * Background는 필요할 때만 사용합니다. 너무 많은 배경색은 로그 가독성을 떨어뜨릴 수 있습니다.
        * 현재 앱 모드와 Rule Mode가 다르면 Enabled 상태여도 이벤트, 하이라이트, 필터가 동작하지 않습니다.

        이벤트 확인과 Context 사용

        * 규칙이 일치하면 Events 탭에 시간, 규칙 이름, 방향, 원본 메시지가 추가됩니다.
        * 이벤트를 한 번 선택하면 관련 정보가 갱신되고, 더블클릭하면 Context 탭으로 이동합니다.
        * Context는 BEFORE / MATCHED / AFTER 순서로 이벤트 전후 로그를 보여줍니다.
        * 이벤트 Context는 앞 5줄과 뒤 5줄로 고정됩니다.
        * 뒤쪽 로그가 아직 도착하지 않았다면 Context pending으로 표시될 수 있습니다.
        * Copy Event Context로 전후 로그 전체를 복사할 수 있습니다.

        이벤트 알림 설정

        * 규칙 편집창에서 Tray, Sound, Popup을 필요한 항목만 켭니다. 기본값은 모두 OFF입니다.
        * Tray는 Windows 알림 영역, Sound는 알림음, Popup은 앱 내부 알림입니다.
        * Notify cooldown 기본값은 30초입니다. 같은 규칙이 반복되면 여러 건을 묶어서 한 번만 알립니다.
        * 알림 옵션을 모두 꺼도 이벤트 기록과 파일 저장은 계속 동작합니다.
        * MOCK에서도 ERROR/WARN 규칙과 알림을 시험할 수 있습니다.

        Saved Commands / History

        * Saved Commands는 자주 쓰는 TX 명령을 버튼으로 저장하는 기능입니다.
        * History는 최근 보낸 명령을 다시 불러오는 기능입니다.
        * History와 Saved Command는 쉘 프롬프트 예: lupa:/> 를 자동으로 붙이지 않습니다.
        * 프롬프트는 장치가 보여주는 문자열이고, 사용자가 보낼 명령이 아닙니다.

        명령 시퀀스 만드는 방법

        1. Sequences 탭을 열고 위쪽 + 버튼으로 새 시퀀스를 만듭니다. 예: Boot Check
        2. 시퀀스를 선택한 뒤 아래쪽 + 버튼으로 첫 번째 step을 추가합니다.
        3. Command에 실제 장비로 보낼 명령을 입력합니다. 쉘 프롬프트 문자는 넣지 않습니다.
        4. Line ending은 Global, None, CR, LF, CRLF 중 장비에 맞는 값을 선택합니다.
        5. Delay after ms에는 이 명령을 보낸 뒤 다음 step까지 기다릴 시간을 입력합니다.
        6. 필요한 명령 수만큼 step을 추가하고 Up/Dn으로 순서를 조정합니다.
        7. 실제 장비 또는 MOCK에 Connect한 뒤 Run을 누릅니다.
        8. 실행 중 중단하려면 Stop을 누릅니다.
        9. 재사용할 시퀀스는 Save Profile로 저장합니다.

        명령 시퀀스 사용 예

        * Step 1: version / Global / 300 ms
        * Step 2: status / Global / 500 ms
        * Step 3: help / CRLF / 300 ms
        * Delay는 명령 전송 후 대기 시간입니다. 너무 짧으면 장비가 다음 명령을 처리하지 못할 수 있습니다.
        * 현재 시퀀스는 지정된 시간만 기다리며 장비 응답 성공 여부를 판정하지 않습니다.
        * 실행 중 연결이 끊기거나 TX가 실패하면 시퀀스 오류로 중단됩니다.
        * MOCK에서는 RX 로그가 계속 생성되므로 각 TX 사이에 다른 RX 로그가 섞여 보이는 것이 정상입니다.
        * HEX 패킷 시퀀스와 응답 대기/조건 분기는 현재 지원하지 않습니다.

        Markers

        * MARK는 테스트 구간을 나누기 위한 표시입니다.
        * MARK는 화면과 로그에만 기록되고 장치로 전송되지 않습니다.
        * Ctrl+M으로 빠르게 MARK를 찍을 수 있습니다.
        * Copy since last MARK로 특정 실험 구간만 복사할 수 있습니다.

        Health

        * HEALTH OK: 알려진 오류나 손실 신호가 없습니다.
        * WARNING: 처리 지연이나 pending 증가 같은 주의 상태입니다.
        * ERROR: 파일 드롭, 이벤트 드롭, xterm 오류, 시리얼 오류 같은 문제가 감지된 상태입니다.
        * 화면 로그가 최대 줄 수를 넘어 오래된 줄이 잘리는 것은 정상 동작이며 오류가 아닙니다.

        COM Bridge

        * Bridge는 실제 장비 COM과 선택한 가상 COM 사이에서 원본 바이트를 양방향 전달합니다.
        * 외부 프로그램은 com0com 쌍의 반대편 포트를 열어야 합니다.
        * 가상 포트에서 들어온 Bridge 로그는 현재 Terminal/HEX 모드를 따르며 방향은 RX입니다.
        * Terminal에서는 선택한 인코딩으로 디코딩하고 Terminal RX 규칙만, HEX에서는 원본 바이트와 HEX RX 규칙만 사용합니다.
        * 표시 형식, 인코딩, 필터, 줄바꿈 설정은 실제 브리지 전달 바이트를 변경하지 않습니다.
        * Bridge ON은 상단과 하단 상태줄에 항상 표시됩니다.

        Settings

        * Now: 즉시 적용됩니다.
        * Reconn: 재연결 후 적용됩니다.
        * Restart: 앱 재시작 후 적용됩니다.
        * Profile: 프로필에 저장되는 설정입니다.
        * 연결 중에는 위험한 시리얼 설정 변경이 제한될 수 있습니다.

        MOCK

        * MOCK은 실제 장비 없이 RX/TX/UI/File/Event 동작을 테스트하는 모드입니다.
        * 스트레스 테스트로 장시간 로그 처리 안정성을 확인할 수 있습니다.
        * No-Newline zzz 패턴은 CR/LF 없이 계속 출력되는 펌웨어 로그를 테스트합니다.
        * CRLF 버튼으로 partial 출력이 중복 없이 줄 종료되는지 확인할 수 있습니다.
        * 실제 장비 동작은 펌웨어와 쉘 구현에 따라 다를 수 있습니다.

        Cute Background

        * Cute background mode는 화면 배경만 바꿉니다.
        * 시리얼 데이터, 로그 저장, 이벤트 감지에는 영향을 주지 않습니다.
        * 커스텀 이미지 경로를 비워두면 앱에 내장된 기본 배경을 사용합니다.
        * Reset bg to default는 커스텀 경로를 지우고 기본 배경으로 되돌립니다.
        * 오래되었거나 없는 커스텀 경로는 기본 배경으로 대체됩니다.
        * 배경이 너무 진하면 로그 가독성이 떨어질 수 있습니다.
        """;

    public string AboutVersionText
    {
        get
        {
            var version = typeof(MainViewModel).Assembly.GetName().Version;
            return version is null
                ? "Version (unknown)"
                : $"Version {version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public string AboutLicenseText => """
        애플리케이션

        Copyright © 2026 박재환.
        이 애플리케이션은 MIT License로 배포됩니다.
        누구나 라이선스 고지를 유지하는 조건으로 사용, 수정, 재배포 및 상업적으로 이용할 수 있습니다.
        자세한 조건은 배포 패키지와 저장소의 LICENSE 파일을 확인하세요.

        주요 서드파티 구성요소

        * Microsoft Windows App SDK 1.8.260508005 — MIT License
        * RJCP.SerialPortStream 3.0.5 — Microsoft Public License (MS-PL)
        * xterm.js 및 xterm.js addons — MIT License
        * .NET / System.Text.Encoding.CodePages — MIT License
        * Microsoft Edge WebView2 Runtime — Microsoft의 해당 배포 및 사용 조건 적용

        각 서드파티 구성요소의 저작권과 라이선스는 원 저작권자에게 있습니다.
        정확한 전체 조건은 배포 패키지, NuGet 패키지 및 각 프로젝트 저장소의 라이선스 원문을 따릅니다.
        """;

    public string HealthStateText
    {
        get => _healthStateText;
        private set => SetProperty(ref _healthStateText, value);
    }

    public string HealthReasonSummary
    {
        get => _healthReasonSummary;
        private set => SetProperty(ref _healthReasonSummary, value);
    }

    public string HealthReasonsText
    {
        get => _healthReasonsText;
        private set => SetProperty(ref _healthReasonsText, value);
    }

    public int HealthWarningCount
    {
        get => _healthWarningCount;
        private set => SetProperty(ref _healthWarningCount, value);
    }

    public int HealthErrorCount
    {
        get => _healthErrorCount;
        private set => SetProperty(ref _healthErrorCount, value);
    }

    public string LastHealthUpdateTimeText
    {
        get => _lastHealthUpdateTimeText;
        private set => SetProperty(ref _lastHealthUpdateTimeText, value);
    }

    public long DiskFreeBytes => Interlocked.Read(ref _diskFreeBytes);

    public long DiskTotalBytes => Interlocked.Read(ref _diskTotalBytes);

    public double DiskFreePercent => DiskTotalBytes <= 0
        ? 0
        : DiskFreeBytes * 100.0 / DiskTotalBytes;

    public long CurrentSessionLogSizeBytes => Interlocked.Read(ref _currentSessionLogSizeBytes);

    public long ProcessWorkingSetBytes => Interlocked.Read(ref _processWorkingSetBytes);

    public int TotalPendingUiCount =>
        PendingVisualLineCount + PendingEventUiCount + PendingEventContextUiCount;

    public long RecordedRxDropCount => CurrentPortIsMock ? MockMissingSequenceCount : 0;

    public long RecordedUiDropCount =>
        Log.DroppedPendingLineCount +
        Interlocked.Read(ref _pendingLogDropCount) +
        BridgeVisualLogDroppedCount;

    public string LastShutdownStartTimeText => _lastShutdownStartTimeText;

    public string LastShutdownCompletedTimeText => _lastShutdownCompletedTimeText;

    public string ShutdownCleanupResult => _shutdownCleanupResult;

    public string ShutdownDisconnectError => _shutdownDisconnectError;

    public string ShutdownFileFlushError => _shutdownFileFlushError;

    public string ShutdownProfileSaveError => _shutdownProfileSaveError;

    public bool WasSequenceRunningDuringShutdown => _wasSequenceRunningDuringShutdown;

    public bool WasSerialConnectedDuringShutdown => _wasSerialConnectedDuringShutdown;

    public string LastXtermContextMenuAction => _lastXtermContextMenuAction;

    public string LastXtermContextMenuError => _lastXtermContextMenuError;

    public long XtermContextMenuErrorCount => Interlocked.Read(ref _xtermContextMenuErrorCount);

    public int LastCopyVisibleLineCount => _lastCopyVisibleLineCount;

    public string LastCopySinceTxActionTimeText => _lastCopySinceTxActionTimeText;

    public int LastCopySinceTxLineCount => _lastCopySinceTxLineCount;

    public int LastCopySinceTxCharacterCount => _lastCopySinceTxCharacterCount;

    public string LastCopySinceTxResult => _lastCopySinceTxResult;

    public string LastCopySinceTxError => _lastCopySinceTxError;

    public long CopySinceTxErrorCount => Interlocked.Read(ref _copySinceTxErrorCount);

    public string LastCopySinceMarkActionTimeText => _lastCopySinceMarkActionTimeText;

    public int LastCopySinceMarkLineCount => _lastCopySinceMarkLineCount;

    public int LastCopySinceMarkCharacterCount => _lastCopySinceMarkCharacterCount;

    public string LastCopySinceMarkResult => _lastCopySinceMarkResult;

    public string LastCopySinceMarkError => _lastCopySinceMarkError;

    public long CopySinceMarkErrorCount => Interlocked.Read(ref _copySinceMarkErrorCount);

    public int LastSearchSelectedTextLength => _lastSearchSelectedTextLength;

    public string LastDisconnectConfirmationResult => _lastDisconnectConfirmationResult;

    public string LastDisconnectConfirmationError => _lastDisconnectConfirmationError;

    public long DisconnectConfirmationErrorCount => Interlocked.Read(ref _disconnectConfirmationErrorCount);

    public string LastTimestampDisplayModeChangeTimeText => _lastTimestampDisplayModeChangeTimeText;

    public string LastTimestampDisplayModeError => _lastTimestampDisplayModeError;

    public long TimestampDisplayModeErrorCount => Interlocked.Read(ref _timestampDisplayModeErrorCount);

    public async Task ShutdownAsync(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        _automaticReceiveReconnectCancellation.Cancel();

        var startedAt = DateTimeOffset.Now;
        _lastShutdownStartTimeText = FormatDiagnosticTime(startedAt);
        _lastShutdownCompletedTimeText = "(pending)";
        _shutdownCleanupResult = "Shutdown cleanup running.";
        _shutdownDisconnectError = string.Empty;
        _shutdownFileFlushError = string.Empty;
        _shutdownProfileSaveError = string.Empty;
        _wasSequenceRunningDuringShutdown = IsSequenceRunning;
        _wasSerialConnectedDuringShutdown = IsConnected || _serialService.IsConnected;
        NotifyShutdownPropertiesChanged();
        SetStatus("Shutting down...");
        RefreshDiagnostics();

        var cleanupErrors = new List<string>();
        using var shutdownCancellation = new CancellationTokenSource(timeout);

        try
        {
            if (IsSequenceRunning)
            {
                try
                {
                    await StopCommandSequenceAsync();
                    await WaitForSequenceStopAsync(shutdownCancellation.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var message = $"Shutdown sequence stop failed: {ex.Message}";
                    cleanupErrors.Add(message);
                    _lastSequenceError = message;
                    OnPropertyChanged(nameof(LastSequenceError));
                }
            }

            try
            {
                if (IsConnected ||
                    _connectionCancellation is not null ||
                    _serialService.ConnectionState != SerialConnectionState.Disconnected)
                {
                    await DisconnectAsync(shutdownCancellation.Token);
                }
            }
            catch (Exception ex)
            {
                _shutdownDisconnectError = $"Shutdown disconnect failed: {ex.Message}";
                cleanupErrors.Add(_shutdownDisconnectError);
            }

            try
            {
                await _fileLogWriter.StopAsync(shutdownCancellation.Token);
            }
            catch (Exception ex)
            {
                _shutdownFileFlushError = $"Shutdown file flush failed: {ex.Message}";
                cleanupErrors.Add(_shutdownFileFlushError);
            }

            try
            {
                var profile = CreateProfileFromCurrentState();
                await _profileService.SaveAsync(CurrentProfilePath, profile, shutdownCancellation.Token);
                RefreshProfileProperties();
            }
            catch (Exception ex)
            {
                _shutdownProfileSaveError = $"Shutdown profile save failed: {ex.Message}";
                cleanupErrors.Add(_shutdownProfileSaveError);
            }
        }
        catch (OperationCanceledException ex)
        {
            cleanupErrors.Add($"Shutdown cleanup timed out after {timeout.TotalSeconds:N0}s: {ex.Message}");
        }
        catch (Exception ex)
        {
            cleanupErrors.Add($"Shutdown cleanup failed: {ex.Message}");
        }
        finally
        {
            _lastShutdownCompletedTimeText = FormatDiagnosticTime(DateTimeOffset.Now);
            _shutdownCleanupResult = cleanupErrors.Count == 0
                ? "Shutdown cleanup completed."
                : $"Shutdown cleanup completed with {cleanupErrors.Count:N0} issue(s): {string.Join("; ", cleanupErrors)}";

            if (cleanupErrors.Count > 0)
            {
                RuntimeDiagnostics.RecordError(
                    "MainViewModel.ShutdownAsync",
                    new InvalidOperationException(_shutdownCleanupResult));
            }

            RuntimeDiagnostics.RecordShutdown(BuildShutdownRecord(cleanupErrors));
            NotifyShutdownPropertiesChanged();
            SetStatus(_shutdownCleanupResult);
            RefreshDiagnostics();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _eventNotificationCancellation.Cancel();
        _bridgeVisualLogCancellation.Cancel();
        _automaticReceiveReconnectCancellation.Cancel();
        _bridgeVisualLogQueue.Writer.TryComplete();
        _diagnosticsTimer.Stop();
        _diagnosticsTimer.Tick -= OnDiagnosticsTimerTick;
        if (_automaticReceiveReconnectTask is not null)
        {
            await _automaticReceiveReconnectTask;
        }

        await DisconnectAsync();
        await _fileLogWriter.StopAsync(CancellationToken.None);
        _serialService.Error -= OnBackgroundError;
        _serialService.StatusChanged -= OnSerialStatusChanged;
        _logPipeline.Error -= OnBackgroundError;
        _logPipeline.StatusChanged -= OnPipelineStatusChanged;
        _fileLogWriter.Error -= OnBackgroundError;
        _fileLogWriter.StatusChanged -= OnFileLogStatusChanged;
        _eventDetector.Error -= OnBackgroundError;
        _eventDetector.StatusChanged -= OnEventDetectorStatusChanged;
        _serialService.RawBytesReceived -= OnRawBytesReceived;
        _bridgeService.Error -= OnBackgroundError;
        _bridgeService.StatusChanged -= OnBridgeStatusChanged;
        _bridgeService.ManualTxStateChanged -= OnManualTxStateChanged;
        _bridgeLogProcessor.Error -= OnBackgroundError;
        await _bridgeService.DisposeAsync();
        await _bridgeLogProcessor.DisposeAsync();
        if (_observeBridgeProcessedLogsTask is not null)
        {
            try
            {
                await _observeBridgeProcessedLogsTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        if (_observeBridgeVisualLogsTask is not null)
        {
            try
            {
                await _observeBridgeVisualLogsTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        await _serialService.DisposeAsync();
        await _fileLogWriter.DisposeAsync();
        await _eventDetector.DisposeAsync();
        _logBatchDispatcher.Dispose();
        _eventBatchDispatcher.Dispose();
        _eventContextBatchDispatcher.Dispose();
        _eventNotificationCancellation.Dispose();
        _bridgeVisualLogCancellation.Dispose();
        _automaticReceiveReconnectCancellation.Dispose();
        _connectionLifecycleGate.Dispose();
    }

    private async Task WaitForSequenceStopAsync(CancellationToken cancellationToken)
    {
        while (IsSequenceRunning)
        {
            await Task.Delay(50, cancellationToken);
        }
    }

    private void NotifyShutdownPropertiesChanged()
    {
        OnPropertyChanged(nameof(LastShutdownStartTimeText));
        OnPropertyChanged(nameof(LastShutdownCompletedTimeText));
        OnPropertyChanged(nameof(ShutdownCleanupResult));
        OnPropertyChanged(nameof(ShutdownDisconnectError));
        OnPropertyChanged(nameof(ShutdownFileFlushError));
        OnPropertyChanged(nameof(ShutdownProfileSaveError));
        OnPropertyChanged(nameof(WasSequenceRunningDuringShutdown));
        OnPropertyChanged(nameof(WasSerialConnectedDuringShutdown));
    }

    private string BuildShutdownRecord(IReadOnlyCollection<string> cleanupErrors)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Shutdown start: {LastShutdownStartTimeText}");
        builder.AppendLine($"Shutdown completed: {LastShutdownCompletedTimeText}");
        builder.AppendLine($"Result: {ShutdownCleanupResult}");
        builder.AppendLine($"Sequence running during shutdown: {WasSequenceRunningDuringShutdown}");
        builder.AppendLine($"Serial connected during shutdown: {WasSerialConnectedDuringShutdown}");
        builder.AppendLine($"Disconnect error: {(string.IsNullOrWhiteSpace(ShutdownDisconnectError) ? "(none)" : ShutdownDisconnectError)}");
        builder.AppendLine($"File flush error: {(string.IsNullOrWhiteSpace(ShutdownFileFlushError) ? "(none)" : ShutdownFileFlushError)}");
        builder.AppendLine($"Profile save error: {(string.IsNullOrWhiteSpace(ShutdownProfileSaveError) ? "(none)" : ShutdownProfileSaveError)}");
        if (cleanupErrors.Count > 0)
        {
            builder.AppendLine("Errors:");
            foreach (var error in cleanupErrors)
            {
                builder.AppendLine($"  {error}");
            }
        }

        return builder.ToString();
    }

    private static string FormatDiagnosticTime(DateTimeOffset time)
    {
        return time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    public Task RefreshPortsAsync()
    {
        Interlocked.Increment(ref _portRefreshGeneration);
        return _portRefreshOperation.RunAsync();
    }

    private async Task RefreshPortsOnceAsync()
    {
        var refreshGeneration = Volatile.Read(ref _portRefreshGeneration);

        try
        {
            var ports = await _serialService.GetAvailablePortsAsync(CancellationToken.None);
            RunOnUiThread(() =>
            {
                if (refreshGeneration != Volatile.Read(ref _portRefreshGeneration))
                {
                    return;
                }

                var duplicateMockCount = Math.Max(
                    0,
                    ports.Count(IsMockPortName) - 1);
                DuplicateMockPortEntryCount = duplicateMockCount;

                var normalizedPorts = NormalizePortList(ports, ShowMockTestPort);
                UpdateAvailableActualPorts(normalizedPorts);
                _lastPortRefreshIncludedMock = normalizedPorts.Any(IsMockPortSelectorValue);
                OnPropertyChanged(nameof(LastPortRefreshIncludedMock));

                var selectedActualPort = GetActualPortName(SelectedPort);
                var selectedMissingAfterRefresh = !string.IsNullOrWhiteSpace(selectedActualPort) &&
                    !ContainsActualPort(normalizedPorts, selectedActualPort);
                var displayedPorts = normalizedPorts.ToList();
                if (selectedMissingAfterRefresh &&
                    selectedActualPort is not null &&
                    !IsMockPortName(selectedActualPort))
                {
                    displayedPorts.Add(selectedActualPort);
                }

                SynchronizePortNames(displayedPorts);

                RefreshBridgePortOptionsFromCurrentPorts();
                NotifyBridgePropertiesChanged();

                if (IsConnected)
                {
                    UpdateSelectedPortAvailability();
                    _lastPortRefreshResult = $"Ports refreshed while connected; preserved {SelectedPort ?? "(none)"}.";
                    NotifyPortSelectionDiagnosticsChanged();
                    RefreshDiagnostics();
                    return;
                }

                if (string.IsNullOrWhiteSpace(selectedActualPort))
                {
                    var lastSuccessfulActualPort = GetActualPortName(_lastSuccessfulSerialSettings?.PortName);
                    var fallbackPort = !string.IsNullOrWhiteSpace(lastSuccessfulActualPort) &&
                        ContainsActualPort(normalizedPorts, lastSuccessfulActualPort)
                            ? NormalizePortSelectorValue(lastSuccessfulActualPort, ShowMockTestPort)
                            : null;
                    var previousSuppress = _suppressSettingsApplyRecording;
                    _suppressSettingsApplyRecording = true;
                    try
                    {
                        SelectedPort = fallbackPort;
                        _lastPortRefreshResult = !string.IsNullOrWhiteSpace(lastSuccessfulActualPort) &&
                            string.Equals(
                                GetActualPortName(SelectedPort),
                                lastSuccessfulActualPort,
                                StringComparison.OrdinalIgnoreCase)
                                ? $"Ports refreshed; auto-selected last successful port {SelectedPort ?? "(none)"}."
                                : "Ports refreshed; waiting for port selection.";
                    }
                    finally
                    {
                        _suppressSettingsApplyRecording = previousSuppress;
                    }
                }
                else if (selectedMissingAfterRefresh)
                {
                    _lastPortRefreshResult = $"Selected port {selectedActualPort} not found after refresh; preserving selection.";
                    SetStatus($"Selected port {selectedActualPort} not found after refresh.");
                }
                else
                {
                    _lastPortRefreshResult = $"Ports refreshed; preserved selected port {selectedActualPort}.";
                }

                UpdateSelectedPortAvailability();
                NotifyPortSelectionDiagnosticsChanged();
                RefreshDiagnostics();
            });
        }
        catch (Exception ex)
        {
            RunOnUiThread(() =>
            {
                if (refreshGeneration != Volatile.Read(ref _portRefreshGeneration))
                {
                    return;
                }

                _lastPortRefreshResult = $"Port scan failed: {ex.Message}";
                NotifyPortSelectionDiagnosticsChanged();
                RefreshDiagnostics();
            });
            if (refreshGeneration == Volatile.Read(ref _portRefreshGeneration))
            {
                SetStatus($"Port scan failed: {ex.Message}");
            }
        }
    }

    private void SynchronizePortNames(IReadOnlyList<string> desiredPorts)
    {
        for (var desiredIndex = 0; desiredIndex < desiredPorts.Count; desiredIndex++)
        {
            var desiredPort = desiredPorts[desiredIndex];
            if (desiredIndex < PortNames.Count &&
                string.Equals(PortNames[desiredIndex], desiredPort, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var existingIndex = -1;
            for (var index = desiredIndex + 1; index < PortNames.Count; index++)
            {
                if (string.Equals(PortNames[index], desiredPort, StringComparison.OrdinalIgnoreCase))
                {
                    existingIndex = index;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                PortNames.Move(existingIndex, desiredIndex);
            }
            else
            {
                PortNames.Insert(desiredIndex, desiredPort);
            }
        }

        while (PortNames.Count > desiredPorts.Count)
        {
            PortNames.RemoveAt(PortNames.Count - 1);
        }
    }

    private void UpdateAvailableActualPorts(IEnumerable<string> ports)
    {
        _lastAvailableActualPorts.Clear();
        foreach (var port in ports)
        {
            var actualPort = GetActualPortName(port);
            if (!string.IsNullOrWhiteSpace(actualPort))
            {
                _lastAvailableActualPorts.Add(actualPort);
            }
        }
    }

    private void RecordPortSelectionChange(string reason)
    {
        _lastPortSelectionChangeReason = reason;
        UpdateSelectedPortAvailability();
        NotifyPortSelectionDiagnosticsChanged();
    }

    private void UpdateSelectedPortAvailability()
    {
        var actualPort = GetActualPortName(SelectedPort);
        _selectedPortAvailable = !string.IsNullOrWhiteSpace(actualPort) &&
            _lastAvailableActualPorts.Contains(actualPort);
    }

    private void NotifyPortSelectionDiagnosticsChanged()
    {
        OnPropertyChanged(nameof(LastPortRefreshIncludedMock));
        OnPropertyChanged(nameof(LastSuccessfulPort));
        OnPropertyChanged(nameof(LastSuccessfulBaudRate));
        OnPropertyChanged(nameof(LastPortSelectionChangeReason));
        OnPropertyChanged(nameof(LastDisconnectPreservedPort));
        OnPropertyChanged(nameof(LastPortRefreshResult));
        OnPropertyChanged(nameof(SelectedPortAvailable));
        NotifyConnectionSelectionCommandState();
    }

    private void NotifyConnectionSelectionCommandState()
    {
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanToggleConnection));
        OnPropertyChanged(nameof(CanStartMockStress));
        ConnectCommand.NotifyCanExecuteChanged();
        ToggleConnectionCommand.NotifyCanExecuteChanged();
        StartMockStressCommand.NotifyCanExecuteChanged();
    }

    private static IReadOnlyList<string> NormalizePortList(IEnumerable<string> ports, bool includeMock)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddPort(string port)
        {
            if (string.IsNullOrWhiteSpace(port))
            {
                return;
            }

            var trimmed = port.Trim();
            var actualPortName = GetActualPortName(trimmed);
            if (string.IsNullOrWhiteSpace(actualPortName))
            {
                return;
            }

            if (IsMockPortName(actualPortName))
            {
                if (includeMock && seen.Add(MockPortName))
                {
                    result.Add(MockPortDisplayName);
                }

                return;
            }

            if (seen.Add(actualPortName))
            {
                result.Add(actualPortName);
            }
        }

        foreach (var port in ports)
        {
            AddPort(port);
        }

        return result;
    }

    private static bool ContainsActualPort(IEnumerable<string> portSelectorValues, string? actualPortName)
    {
        if (string.IsNullOrWhiteSpace(actualPortName))
        {
            return false;
        }

        return portSelectorValues.Any(port =>
            string.Equals(GetActualPortName(port), actualPortName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizePortSelectorValue(string? value, bool showMockTestPort)
    {
        var actualPortName = GetActualPortName(value);
        if (string.IsNullOrWhiteSpace(actualPortName))
        {
            return null;
        }

        if (IsMockPortName(actualPortName))
        {
            return showMockTestPort ? MockPortDisplayName : null;
        }

        return actualPortName.Trim();
    }

    private static string? GetActualPortName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return IsMockPortName(trimmed) || string.Equals(trimmed, MockPortDisplayName, StringComparison.OrdinalIgnoreCase)
            ? MockPortName
            : trimmed;
    }

    private static bool IsMockPortSelectorValue(string? value)
    {
        return IsMockPortName(GetActualPortName(value));
    }

    private static bool IsMockPortName(string? value)
    {
        return string.Equals(value?.Trim(), MockPortName, StringComparison.OrdinalIgnoreCase);
    }

    private async Task LoadStartupProfileAsync()
    {
        try
        {
            var profile = await _profileService.LoadAsync(CurrentProfilePath, CancellationToken.None);
            RunOnUiThread(() =>
            {
                if (!IsConnected)
                {
                    ApplyProfile(profile);
                }

                RefreshProfileProperties();
                SetStatus(ProfileStatusText);
                SetFooter(CreateFooterStatus());
                RefreshDiagnostics();
            });
        }
        catch (Exception ex)
        {
            RunOnUiThread(() =>
            {
                _lastBackgroundError = $"Startup profile load failed: {ex.Message}";
                SetStatus(_lastBackgroundError);
                RefreshProfileProperties();
                RefreshDiagnostics();
            });
        }
    }

    private async Task SaveProfileAsync()
    {
        try
        {
            var profile = CreateProfileFromCurrentState();
            await _profileService.SaveAsync(CurrentProfilePath, profile, CancellationToken.None);
            RefreshProfileProperties();
            SetStatus(ProfileStatusText);
            RefreshDiagnostics();
        }
        catch (Exception ex)
        {
            _lastBackgroundError = $"Save profile failed: {ex.Message}";
            SetStatus(_lastBackgroundError);
            RefreshProfileProperties();
            RefreshDiagnostics();
        }
    }

    private async Task LoadProfileAsync()
    {
        if (IsConnected)
        {
            SetStatus("Disconnect before loading a profile.");
            return;
        }

        IsBusy = true;
        try
        {
            var profile = await _profileService.LoadAsync(CurrentProfilePath, CancellationToken.None);
            ApplyProfile(profile);
            RecordSettingsApplyStatus(
                "Profile load",
                "Profile loaded. Immediate settings applied; reconnect-only settings apply on next connect.");
            RefreshProfileProperties();
            SetStatus(ProfileStatusText);
            SetFooter(CreateFooterStatus());
            RefreshDiagnostics();
        }
        catch (Exception ex)
        {
            _lastBackgroundError = $"Load profile failed: {ex.Message}";
            SetStatus(_lastBackgroundError);
            RefreshProfileProperties();
            RefreshDiagnostics();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task ResetProfileAsync()
    {
        if (IsConnected)
        {
            SetStatus("Disconnect before resetting profile settings.");
            return Task.CompletedTask;
        }

        IsBusy = true;
        try
        {
            ApplyProfile(_profileService.CreateDefaultProfile());
            RecordSettingsApplyStatus(
                "Reset to defaults",
                "Default settings applied. Reconnect-only settings apply on next connect.");
            RefreshProfileProperties();
            SetStatus("Default profile settings applied. Use Save Profile to persist them.");
            SetFooter(CreateFooterStatus());
            RefreshDiagnostics();
        }
        finally
        {
            IsBusy = false;
        }

        return Task.CompletedTask;
    }

    private Task FindNextSearchMatchAsync()
    {
        AddCurrentSearchTextToHistory();
        RunManualVisibleLogSearch(SearchMove.Next);
        RequestXtermSearch(SearchMove.Next);
        return Task.CompletedTask;
    }

    private Task FindPreviousSearchMatchAsync()
    {
        AddCurrentSearchTextToHistory();
        RunManualVisibleLogSearch(SearchMove.Previous);
        RequestXtermSearch(SearchMove.Previous);
        return Task.CompletedTask;
    }

    private Task RefreshSearchResultsAsync()
    {
        AddCurrentSearchTextToHistory();
        RunManualVisibleLogSearch(SearchMove.None);
        if (CanSearch())
        {
            SetStatus($"Search results refreshed: {SearchText}");
        }

        return Task.CompletedTask;
    }

    public async Task FindNextFromShortcutAsync(string source)
    {
        await RunSearchShortcutAsync(SearchMove.Next, source, "Find next");
    }

    public async Task FindPreviousFromShortcutAsync(string source)
    {
        await RunSearchShortcutAsync(SearchMove.Previous, source, "Find previous");
    }

    public void RecordSearchFocusShortcut(string source)
    {
        RecordSearchShortcutAction("Focus search", source);
    }

    public void RecordSearchEscapeShortcut(string source)
    {
        RecordSearchShortcutAction("Leave search", source);
    }

    private async Task RunSearchShortcutAsync(SearchMove move, string source, string action)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                RecordSearchShortcutAction($"{action}: empty search text", source);
                SetStatus("Search text is empty.");
                return;
            }

            if (move == SearchMove.Previous)
            {
                await FindPreviousSearchMatchAsync();
            }
            else
            {
                await FindNextSearchMatchAsync();
            }

            RecordSearchShortcutAction(action, source);
        }
        catch (Exception ex)
        {
            RecordSearchShortcutError($"{action} shortcut failed: {ex.Message}", source);
        }
    }

    public bool RecallSearchHistory(int direction)
    {
        if (_searchHistory.Count == 0 || direction == 0)
        {
            return false;
        }

        if (_searchHistoryCursor < 0)
        {
            _searchHistoryCursor = _searchHistory.Count;
            _searchHistoryDraft = SearchText;
        }

        if (direction < 0)
        {
            if (_searchHistoryCursor <= 0)
            {
                return true;
            }

            _searchHistoryCursor--;
            ApplySearchHistoryText(_searchHistory[_searchHistoryCursor]);
            return true;
        }

        if (_searchHistoryCursor < _searchHistory.Count - 1)
        {
            _searchHistoryCursor++;
            ApplySearchHistoryText(_searchHistory[_searchHistoryCursor]);
            return true;
        }

        ApplySearchHistoryText(_searchHistoryDraft);
        ResetSearchHistoryNavigation();
        return true;
    }

    private void AddCurrentSearchTextToHistory()
    {
        var term = SearchText.Trim();
        if (string.IsNullOrWhiteSpace(term))
        {
            return;
        }

        if (_searchHistory.Count > 0 &&
            string.Equals(_searchHistory[^1], term, StringComparison.Ordinal))
        {
            ResetSearchHistoryNavigation();
            return;
        }

        var existingIndex = _searchHistory.FindIndex(value => string.Equals(value, term, StringComparison.Ordinal));
        if (existingIndex >= 0)
        {
            _searchHistory.RemoveAt(existingIndex);
        }

        _searchHistory.Add(term);
        while (_searchHistory.Count > MaxSearchHistoryCount)
        {
            _searchHistory.RemoveAt(0);
        }

        ResetSearchHistoryNavigation();
    }

    private void ApplySearchHistoryText(string text)
    {
        _isApplyingSearchHistory = true;
        try
        {
            SearchText = text;
        }
        finally
        {
            _isApplyingSearchHistory = false;
        }
    }

    private void ResetSearchHistoryNavigation()
    {
        _searchHistoryCursor = -1;
        _searchHistoryDraft = string.Empty;
    }

    private bool CanSearch()
    {
        return !string.IsNullOrWhiteSpace(SearchText);
    }

    private void RunManualVisibleLogSearch(SearchMove move)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            SearchMatchCount = 0;
            CurrentSearchMatchIndex = 0;
            CurrentSearchMatchedLine = string.Empty;
            UpdateSearchResults(Array.Empty<string>(), Array.Empty<int>());

            return;
        }

        try
        {
            var lines = Log.GetVisibleSearchLinesSnapshot();
            var comparison = IsSearchCaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            var matchingLineIndexes = new List<int>();

            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Contains(SearchText, comparison))
                {
                    matchingLineIndexes.Add(i);
                }
            }

            SearchMatchCount = matchingLineIndexes.Count;
            UpdateSearchResults(lines, matchingLineIndexes);

            if (matchingLineIndexes.Count == 0)
            {
                CurrentSearchMatchIndex = 0;
                CurrentSearchMatchedLine = string.Empty;
                SelectedSearchResult = null;

                if (move is SearchMove.Next or SearchMove.Previous)
                {
                    SetStatus($"Search found no matches: {SearchText}");
                }

                ClearSearchError();
                return;
            }

            var currentIndex = CurrentSearchMatchIndex > 0
                ? CurrentSearchMatchIndex - 1
                : -1;

            currentIndex = move switch
            {
                SearchMove.Next => currentIndex < 0 ? 0 : (currentIndex + 1) % matchingLineIndexes.Count,
                SearchMove.Previous => currentIndex < 0 ? matchingLineIndexes.Count - 1 : (currentIndex - 1 + matchingLineIndexes.Count) % matchingLineIndexes.Count,
                _ => Math.Clamp(currentIndex < 0 ? 0 : currentIndex, 0, matchingLineIndexes.Count - 1)
            };

            CurrentSearchMatchIndex = currentIndex + 1;
            CurrentSearchMatchedLine = lines[matchingLineIndexes[currentIndex]];
            SelectSearchResultByMatchIndex(CurrentSearchMatchIndex);

            ClearSearchError();

            if (move is SearchMove.Next or SearchMove.Previous)
            {
                SetStatus($"Search match {CurrentSearchMatchIndex:N0} of {SearchMatchCount:N0}: {SearchText}");
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _searchErrorCount);
            _lastSearchError = $"Search failed: {ex.Message}";
            SearchMatchCount = 0;
            CurrentSearchMatchIndex = 0;
            CurrentSearchMatchedLine = string.Empty;
            SearchResults.Clear();
            SearchResultStatusText = $"Search failed: {ex.Message}";
            SelectedSearchResult = null;
            OnPropertyChanged(nameof(SearchResultVisibleCount));
            OnPropertyChanged(nameof(SearchErrorCount));
            OnPropertyChanged(nameof(LastSearchError));
            SetStatus(_lastSearchError);
            RefreshDiagnostics();
        }
    }

    private void UpdateSearchResults(IReadOnlyList<string> lines, IReadOnlyList<int> matchingLineIndexes)
    {
        try
        {
            var previousSelection = SelectedSearchResult;
            SearchResults.Clear();
            AreSearchResultsStale = false;

            if (string.IsNullOrWhiteSpace(SearchText))
            {
                SearchResultStatusText = "Enter search text.";
                SelectedSearchResult = null;
                OnPropertyChanged(nameof(SearchResultVisibleCount));
                RecordSearchResultsRebuild();
                return;
            }

            if (matchingLineIndexes.Count == 0)
            {
                SearchResultStatusText = "Manual · No matches";
                SelectedSearchResult = null;
                OnPropertyChanged(nameof(SearchResultVisibleCount));
                RecordSearchResultsRebuild();
                return;
            }

            var visibleCount = Math.Min(MaxVisibleSearchResults, matchingLineIndexes.Count);
            for (var i = 0; i < visibleCount; i++)
            {
                var lineIndex = matchingLineIndexes[i];
                var fullText = lineIndex >= 0 && lineIndex < lines.Count
                    ? lines[lineIndex]
                    : string.Empty;
                SearchResults.Add(CreateVisibleSearchResult(i + 1, lineIndex, fullText));
            }

            SearchResultStatusText = matchingLineIndexes.Count > MaxVisibleSearchResults
                ? $"Manual · {visibleCount:N0}/{matchingLineIndexes.Count:N0} shown"
                : $"Manual · {visibleCount:N0} shown";
            OnPropertyChanged(nameof(SearchResultVisibleCount));
            RestoreSearchResultSelection(previousSelection);
            RecordSearchResultsRebuild();
            _lastSearchResultBuildError = string.Empty;
            OnPropertyChanged(nameof(LastSearchResultBuildError));
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _searchResultBuildErrorCount);
            _lastSearchResultBuildError = $"Search result build failed: {ex.Message}";
            SearchResultStatusText = _lastSearchResultBuildError;
            OnPropertyChanged(nameof(SearchResultBuildErrorCount));
            OnPropertyChanged(nameof(LastSearchResultBuildError));
            SetStatus(_lastSearchResultBuildError);
            RefreshDiagnostics();
        }
    }

    private void RestoreSearchResultSelection(VisibleSearchResult? previousSelection)
    {
        if (SearchResults.Count == 0)
        {
            if (previousSelection is not null)
            {
                Interlocked.Increment(ref _searchResultSelectionLostCount);
                OnPropertyChanged(nameof(SearchResultSelectionLostCount));
            }

            SelectedSearchResult = null;
            return;
        }

        VisibleSearchResult? restored = null;
        if (previousSelection is not null)
        {
            restored = SearchResults.FirstOrDefault(result =>
                    result.VisibleLineIndex == previousSelection.VisibleLineIndex &&
                    string.Equals(result.FullText, previousSelection.FullText, StringComparison.Ordinal))
                ?? SearchResults.FirstOrDefault(result =>
                    string.Equals(result.FullText, previousSelection.FullText, StringComparison.Ordinal));
        }

        restored ??= SearchResults.FirstOrDefault(result => result.MatchIndex == CurrentSearchMatchIndex);
        SelectedSearchResult = restored;
        if (previousSelection is not null && restored is null)
        {
            Interlocked.Increment(ref _searchResultSelectionLostCount);
            OnPropertyChanged(nameof(SearchResultSelectionLostCount));
        }
    }

    private void MarkSearchResultsStale()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            AreSearchResultsStale = false;
            SearchResultStatusText = "Enter search text.";
            return;
        }

        AreSearchResultsStale = true;
        SearchResultStatusText = SearchResults.Count == 0
            ? "Manual · Refresh to update"
            : $"Stale · Refresh to update ({SearchResults.Count:N0} shown)";
    }

    private void InvalidateSearchResultsForCriteriaChange()
    {
        SearchMatchCount = 0;
        CurrentSearchMatchIndex = 0;
        CurrentSearchMatchedLine = string.Empty;
        SearchResults.Clear();
        SelectedSearchResult = null;
        OnPropertyChanged(nameof(SearchResultVisibleCount));
        MarkSearchResultsStale();
    }

    private void RecordSearchResultsRebuild()
    {
        Interlocked.Increment(ref _searchResultsRebuildCount);
        OnPropertyChanged(nameof(SearchResultsRebuildCount));
    }

    private static VisibleSearchResult CreateVisibleSearchResult(int matchIndex, int visibleLineIndex, string fullText)
    {
        var timeText = string.Empty;
        var directionText = string.Empty;
        var messagePreview = fullText;

        if (fullText.Length >= 26 && fullText[0] == '[' && fullText[24] == ']')
        {
            timeText = fullText.Substring(12, 12);
            var rest = fullText.Length > 26 ? fullText[26..] : string.Empty;
            ParseVisibleSearchResultBody(rest, out directionText, out messagePreview);
        }
        else
        {
            ParseVisibleSearchResultBody(fullText, out directionText, out messagePreview);
        }

        return new VisibleSearchResult(
            matchIndex,
            visibleLineIndex,
            timeText,
            directionText,
            messagePreview,
            fullText);
    }

    private static void ParseVisibleSearchResultBody(string text, out string directionText, out string messagePreview)
    {
        directionText = string.Empty;
        messagePreview = text;

        if (text.StartsWith("RX <", StringComparison.Ordinal) ||
            text.StartsWith("TX >", StringComparison.Ordinal))
        {
            directionText = text[..2];
            messagePreview = text.Length > 5 ? text[5..] : string.Empty;
            return;
        }

        if (text.StartsWith("MARK >", StringComparison.Ordinal))
        {
            directionText = "MARK";
            messagePreview = text.Length > 7 ? text[7..] : string.Empty;
            return;
        }

        if (text.StartsWith("SYS", StringComparison.Ordinal))
        {
            directionText = "SYS";
            messagePreview = text.Length > 4 ? text[4..] : string.Empty;
        }
    }

    private static bool IsVisibleMarkLine(string? line)
    {
        return !string.IsNullOrEmpty(line) &&
            line.Contains("MARK >", StringComparison.Ordinal);
    }

    private static bool IsVisibleTxLine(string? line)
    {
        return !string.IsNullOrEmpty(line) &&
            line.Contains("TX >", StringComparison.Ordinal);
    }

    private void SelectSearchResultByMatchIndex(int matchIndex)
    {
        if (matchIndex <= 0)
        {
            SelectedSearchResult = null;
            return;
        }

        SelectedSearchResult = SearchResults.FirstOrDefault(result => result.MatchIndex == matchIndex);
    }

    private void ClearSearchError()
    {
        if (string.IsNullOrWhiteSpace(_lastSearchError))
        {
            return;
        }

        _lastSearchError = string.Empty;
        OnPropertyChanged(nameof(LastSearchError));
    }

    private void RequestXtermSearch(SearchMove move)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return;
        }

        try
        {
            var requestId = Interlocked.Increment(ref _xtermSearchRequestId);
            var direction = move == SearchMove.Previous ? "previous" : "next";
            XtermSearchRequested?.Invoke(
                this,
                new XtermSearchRequest(
                    requestId,
                    SearchText,
                    IsSearchCaseSensitive,
                    direction));
        }
        catch (Exception ex)
        {
            RecordXtermSearchError($"xterm search request failed: {ex.Message}");
        }
    }

    public Task JumpToSearchResultAsync(VisibleSearchResult? result)
    {
        if (result is null)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            RecordSearchResultJumpError("Search result jump failed: enter search text first.");
            return Task.CompletedTask;
        }

        try
        {
            SelectedSearchResult = result;
            CurrentSearchMatchIndex = result.MatchIndex;
            CurrentSearchMatchedLine = result.FullText;

            var requestId = Interlocked.Increment(ref _xtermSearchRequestId);
            XtermSearchRequested?.Invoke(
                this,
                new XtermSearchRequest(
                    requestId,
                    SearchText,
                    IsSearchCaseSensitive,
                    "indexed",
                    Math.Max(0, result.MatchIndex - 1)));
            SetStatus($"Search result {result.MatchIndex:N0} selected: {SearchText}");
            RefreshDiagnostics();
        }
        catch (Exception ex)
        {
            RecordSearchResultJumpError($"Search result jump failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private void RecordSearchResultJumpError(string message)
    {
        Interlocked.Increment(ref _searchResultJumpErrorCount);
        _lastSearchResultJumpError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(SearchResultJumpErrorCount));
        OnPropertyChanged(nameof(LastSearchResultJumpError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    private async Task ToggleConnectionAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync();
            return;
        }

        await ConnectAsync();
    }

    private async Task RefreshBridgePortsAsync()
    {
        try
        {
            var ports = await _serialService.GetAvailablePortsAsync(CancellationToken.None);
            RunOnUiThread(() =>
            {
                var devicePort = GetActualPortName(SelectedPort);
                var candidates = ports
                    .Where(port => !string.IsNullOrWhiteSpace(port))
                    .Select(port => port.Trim())
                    .Where(port => !IsMockPortName(port))
                    .Where(port => !string.Equals(port, devicePort, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(port => port, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                ReplaceBridgePortOptions(candidates);

                NotifyBridgePropertiesChanged();
                SetStatus($"Bridge ports refreshed: {BridgePortNames.Count:N0} candidate(s).");
            });
        }
        catch (Exception ex)
        {
            SetStatus($"Bridge port refresh failed: {ex.Message}");
        }
    }

    private void RefreshBridgePortOptionsFromCurrentPorts()
    {
        var devicePort = GetActualPortName(SelectedPort);
        var candidates = PortNames
            .Select(GetActualPortName)
            .Where(port => !string.IsNullOrWhiteSpace(port))
            .Cast<string>()
            .Where(port => !IsMockPortName(port))
            .Where(port => !string.Equals(port, devicePort, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(port => port, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ReplaceBridgePortOptions(candidates);
    }

    private void ReplaceBridgePortOptions(IReadOnlyList<string> candidates)
    {
        var preservedPort = SelectedBridgePort;
        if (string.IsNullOrWhiteSpace(preservedPort) && IsBridgeActive)
        {
            preservedPort = _bridgeService.VirtualPortName;
        }

        var desiredPorts = candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!string.IsNullOrWhiteSpace(preservedPort) &&
            !desiredPorts.Contains(preservedPort, StringComparer.OrdinalIgnoreCase))
        {
            desiredPorts.Add(preservedPort);
        }

        for (var index = BridgePortNames.Count - 1; index >= 0; index--)
        {
            if (!desiredPorts.Contains(BridgePortNames[index], StringComparer.OrdinalIgnoreCase))
            {
                BridgePortNames.RemoveAt(index);
            }
        }

        foreach (var port in desiredPorts)
        {
            if (!BridgePortNames.Contains(port, StringComparer.OrdinalIgnoreCase))
            {
                BridgePortNames.Add(port);
            }
        }

        SelectedBridgePort = preservedPort;
    }

    private async Task StartBridgeAsync()
    {
        // A bridge start is a one-shot user action, never an armed state that can
        // survive a failed start, disconnect, reconnect, or application restart.
        _currentBridgeSettings.Enabled = false;
        await StartBridgeCoreAsync();
        _currentBridgeSettings.Enabled = _bridgeService.IsRunning;
        NotifyBridgePropertiesChanged();
    }

    private async Task StartBridgeCoreAsync()
    {
        var devicePort = GetActualPortName(SelectedPort);
        var virtualPort = SelectedBridgePort?.Trim();
        if (!_serialService.IsConnected)
        {
            SetStatus("Connect the device COM port before starting the bridge.");
            return;
        }

        if (string.IsNullOrWhiteSpace(virtualPort))
        {
            SetStatus("Select the app-side virtual COM port.");
            return;
        }

        if (string.Equals(devicePort, virtualPort, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Bridge virtual port must be different from the device port.");
            return;
        }

        try
        {
            _bridgeLogProcessor.ResetStream();
            await _bridgeService.StartAsync(
                _currentBridgeSettings.Clone(),
                CreateCurrentSettings(),
                ForwardBridgeBytesToDeviceAsync,
                _connectionCancellation?.Token ?? CancellationToken.None);
            _serialService.SetRawBridgePriorityEnabled(true);
            SetStatus($"Bidirectional raw bridge active: {devicePort} ↔ {virtualPort}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Bridge start canceled.");
        }
        catch (Exception ex)
        {
            _lastBackgroundError = $"Bridge start failed: {ex.Message}";
            SetStatus(_lastBackgroundError);
        }
        finally
        {
            NotifyBridgePropertiesChanged();
            RefreshDiagnostics();
        }
    }

    private Task StopBridgeAsync()
    {
        return StopBridgeCoreAsync(disableRequested: true, CancellationToken.None);
    }

    private async Task StopBridgeCoreAsync(bool disableRequested, CancellationToken cancellationToken)
    {
        _serialService.SetRawBridgePriorityEnabled(false);
        _bridgeLogProcessor.ResetStream();
        if (disableRequested)
        {
            _currentBridgeSettings.Enabled = false;
        }

        try
        {
            await _bridgeService.StopAsync(cancellationToken);
            if (disableRequested)
            {
                SetStatus("Serial bridge stopped.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastBackgroundError = $"Bridge stop failed: {ex.Message}";
            SetStatus(_lastBackgroundError);
        }
        finally
        {
            NotifyBridgePropertiesChanged();
            RefreshDiagnostics();
        }
    }

    private async Task ForwardBridgeBytesToDeviceAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        await _serialService.SendBytesAsync(bytes, FormatBytesAsHex(bytes), cancellationToken);
        if (!_bridgeLogProcessor.TryEnqueue(bytes, SelectedRxDisplayMode, SelectedRxEncoding))
        {
            Volatile.Write(ref _backgroundStatusSnapshotDirty, 1);
        }
    }

    private void NotifyBridgePropertiesChanged()
    {
        OnPropertyChanged(nameof(SelectedBridgePort));
        OnPropertyChanged(nameof(BridgeRequestedEnabled));
        OnPropertyChanged(nameof(IsBridgeActive));
        OnPropertyChanged(nameof(CanEditBridgePort));
        OnPropertyChanged(nameof(CanStartBridge));
        OnPropertyChanged(nameof(CanStopBridge));
        OnPropertyChanged(nameof(BridgeStateText));
        OnPropertyChanged(nameof(BridgeIndicatorText));
        OnPropertyChanged(nameof(BridgeRouteText));
        OnPropertyChanged(nameof(BridgeStatusText));
        OnPropertyChanged(nameof(BridgeDeviceToVirtualByteCount));
        OnPropertyChanged(nameof(BridgeDeviceToVirtualChunkCount));
        OnPropertyChanged(nameof(BridgeVirtualToDeviceByteCount));
        OnPropertyChanged(nameof(BridgeVirtualToDeviceChunkCount));
        OnPropertyChanged(nameof(BridgeDroppedByteCount));
        OnPropertyChanged(nameof(BridgeDroppedChunkCount));
        OnPropertyChanged(nameof(BridgeDroppedDeviceToVirtualByteCount));
        OnPropertyChanged(nameof(BridgeDroppedDeviceToVirtualChunkCount));
        OnPropertyChanged(nameof(BridgeDroppedVirtualToDeviceByteCount));
        OnPropertyChanged(nameof(BridgeDroppedVirtualToDeviceChunkCount));
        OnPropertyChanged(nameof(BridgeErrorCount));
        OnPropertyChanged(nameof(BridgePendingChunkCount));
        OnPropertyChanged(nameof(BridgePendingDeviceToVirtualChunkCount));
        OnPropertyChanged(nameof(BridgePendingVirtualToDeviceChunkCount));
        OnPropertyChanged(nameof(BridgePendingDeviceToVirtualByteCount));
        OnPropertyChanged(nameof(BridgePendingVirtualToDeviceByteCount));
        OnPropertyChanged(nameof(BridgeOldestPendingChunkAgeMs));
        OnPropertyChanged(nameof(ManualTxState));
        OnPropertyChanged(nameof(IsManualTxBusy));
        OnPropertyChanged(nameof(CanSendManualTx));
        OnPropertyChanged(nameof(ManualTxStateText));
        OnPropertyChanged(nameof(BridgePendingText));
        OnPropertyChanged(nameof(BridgeDroppedChunksText));
        OnPropertyChanged(nameof(BridgeDroppedBytesText));
        OnPropertyChanged(nameof(BridgeErrorsText));
        OnPropertyChanged(nameof(BridgeVisualLogPendingCount));
        OnPropertyChanged(nameof(BridgeVisualLogDroppedCount));
        OnPropertyChanged(nameof(BridgeVisualLogStatusText));
        OnPropertyChanged(nameof(IsRawBridgePriorityEnabled));
        OnPropertyChanged(nameof(BridgePriorityDroppedPipelineByteCount));
        OnPropertyChanged(nameof(BridgePriorityDroppedPipelineChunkCount));
        OnPropertyChanged(nameof(BridgePriorityPipelineStatusText));
        OnPropertyChanged(nameof(BridgeLastError));
        StartBridgeCommand.NotifyCanExecuteChanged();
        StopBridgeCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        SendSavedCommandCommand.NotifyCanExecuteChanged();
        RunCommandSequenceCommand.NotifyCanExecuteChanged();
    }

    private async Task ConnectAsync()
    {
        await _connectionLifecycleGate.WaitAsync();
        try
        {
            await ConnectCoreAsync();
        }
        finally
        {
            _connectionLifecycleGate.Release();
        }
    }

    public TimestampDisplayFormatOption SelectedTimestampDisplayFormatOption
    {
        get => TimestampDisplayFormatOptions.FirstOrDefault(option => option.Format == _currentUiSettings.TimestampDisplayFormat)
            ?? TimestampDisplayFormatOptions[0];
        set
        {
            if (value is null || _currentUiSettings.TimestampDisplayFormat == value.Format)
            {
                return;
            }

            _currentUiSettings.TimestampDisplayFormat = value.Format;
            SetVisibleLogRebuildReason("timestamp format change");
            Log.SetTimestampDisplayFormat(value.Format);
            _lastTimestampDisplayModeChangeTimeText = FormatDiagnosticTime(DateTimeOffset.Now);
            _lastTimestampDisplayModeError = string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LastTimestampDisplayModeChangeTimeText));
            OnPropertyChanged(nameof(LastTimestampDisplayModeError));
            RefreshSelectedEventContextText();
            MarkSearchResultsStale();
            RecordSettingsChange("Timestamp format", SettingsApplyBehavior.Immediate, value.DisplayName);
        }
    }

    private void QueueAutomaticReceiveReconnect()
    {
        Interlocked.Increment(ref _automaticReceiveReconnectRequestVersion);
        if (_automaticReceiveReconnectCancellation.IsCancellationRequested ||
            Volatile.Read(ref _shutdownStarted) != 0 ||
            Interlocked.CompareExchange(ref _automaticReceiveReconnectWorkerRunning, 1, 0) != 0)
        {
            return;
        }

        _automaticReceiveReconnectTask = RunAutomaticReceiveReconnectAsync(
            _automaticReceiveReconnectCancellation.Token);
    }

    private async Task RunAutomaticReceiveReconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AutomaticReceiveReconnectDebounce, cancellationToken);
            while (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _shutdownStarted) == 0)
            {
                if (!RequiresAutomaticReceiveReconnect(SelectedRxDisplayMode, HexGroupTimeoutMs))
                {
                    return;
                }

                var requestedVersion = Volatile.Read(ref _automaticReceiveReconnectRequestVersion);
                await _connectionLifecycleGate.WaitAsync(cancellationToken);
                try
                {
                    if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _shutdownStarted) != 0)
                    {
                        return;
                    }

                    if (!RequiresAutomaticReceiveReconnect(SelectedRxDisplayMode, HexGroupTimeoutMs))
                    {
                        return;
                    }

                    SetStatus($"Applying {FormatRxDisplayModeName(SelectedRxDisplayMode)} mode; reconnecting...");
                    await DisconnectCoreAsync(CancellationToken.None, updateBusy: true);
                    if (IsConnected || _serialService.IsConnected)
                    {
                        SetStatus("Automatic mode reconnect stopped because the serial port did not disconnect cleanly.");
                        return;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    await ConnectCoreAsync();
                }
                finally
                {
                    _connectionLifecycleGate.Release();
                }

                if (!IsConnected)
                {
                    return;
                }

                if (!RequiresAutomaticReceiveReconnect(SelectedRxDisplayMode, HexGroupTimeoutMs))
                {
                    SetStatus($"Reconnected in {FormatRxDisplayModeName(_appliedRxDisplayMode)} mode.");
                    return;
                }

                if (requestedVersion == Volatile.Read(ref _automaticReceiveReconnectRequestVersion))
                {
                    SetStatus("Receive mode still differs after reconnect; retrying once settings settle.");
                }

                await Task.Delay(AutomaticReceiveReconnectDebounce, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("MainViewModel.RunAutomaticReceiveReconnectAsync", ex);
            RecordSettingsApplyError($"Automatic mode reconnect failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _automaticReceiveReconnectWorkerRunning, 0);
            if (!cancellationToken.IsCancellationRequested &&
                Volatile.Read(ref _shutdownStarted) == 0 &&
                RequiresAutomaticReceiveReconnect(SelectedRxDisplayMode, HexGroupTimeoutMs))
            {
                QueueAutomaticReceiveReconnect();
            }
        }
    }

    private async Task ConnectCoreAsync()
    {
        var requestedSelectedPort = SelectedPort;
        var settings = CreateCurrentSettings();
        var requestedRxDisplayMode = NormalizeRxDisplayMode(SelectedRxDisplayMode);
        var requestedHexGroupTimeoutMs = HexGroupTimeoutMs;
        if (string.IsNullOrWhiteSpace(settings.PortName))
        {
            SetStatus("Select a COM port.");
            return;
        }

        if (!SelectedPortAvailable)
        {
            SetStatus("Select an available serial port.");
            return;
        }

        if (!BaudRates.Contains(SelectedBaudRate))
        {
            SetStatus("Select a baud rate.");
            return;
        }

        if (IsConnected || _serialService.IsConnected)
        {
            SetStatus("Already connected.");
            return;
        }

        IsBusy = true;
        RecordConnectRequested(settings);
        try
        {
            if (_connectionCancellation is not null && !IsConnected)
            {
                await DisconnectCoreAsync(CancellationToken.None, updateBusy: false);
            }

            _connectionCancellation = new CancellationTokenSource();
            ApplySizeRotationSettings();
            ApplySessionFileNaming(requestNewFile: false);
            if (FileLoggingEnabled)
            {
                await TryStartFileLoggingAsync(settings.SaveDirectory, CancellationToken.None);
            }

            await _eventDetector.StartAsync(
                EventRules,
                _currentEventContextSettings,
                CancellationToken.None);
            ApplySessionFileNaming(requestNewFile: false);
            await _serialService.ConnectAsync(
                settings,
                new SerialReceiveOptions
                {
                    UseNativeIdleTimeout = requestedRxDisplayMode == RxDisplayMode.Hex,
                    IdleTimeoutMs = requestedHexGroupTimeoutMs
                },
                _connectionCancellation.Token);
            ApplyRxDisplayRuntime(
                requestedRxDisplayMode,
                requestedHexGroupTimeoutMs,
                "serial connection mode applied");
            await _logPipeline.StartAsync(_serialService.ReceivedBytes, settings, _connectionCancellation.Token);
            _observeLogsTask = Task.Run(() => ObserveLogsAsync(_connectionCancellation.Token), CancellationToken.None);
            _observeEventsTask = Task.Run(() => ObserveEventsAsync(CancellationToken.None), CancellationToken.None);
            _observeEventContextsTask = Task.Run(() => ObserveEventContextsAsync(CancellationToken.None), CancellationToken.None);

            IsConnected = true;
            OnPropertyChanged(nameof(HexGroupTimeoutAppliedText));
            OnPropertyChanged(nameof(HexGroupTimeoutHeaderText));
            if (!RequiresAutomaticReceiveReconnect(SelectedRxDisplayMode, HexGroupTimeoutMs))
            {
                ClearPendingReconnectSettings();
            }
            RecordConnectSucceeded(settings);
            SetStatus($"Connected to {settings.PortName} at {settings.BaudRate} bps");
            SetFooter(CreateFooterStatus());
        }
        catch (Exception ex)
        {
            string cleanupError = string.Empty;
            try
            {
                await DisconnectCoreAsync(CancellationToken.None, updateBusy: false);
            }
            catch (Exception cleanupEx)
            {
                cleanupError = $"Connect cleanup failed: {cleanupEx.Message}";
                RuntimeDiagnostics.RecordError("MainViewModel.ConnectAsync.Cleanup", cleanupEx);
            }

            RestoreConnectionSelection(requestedSelectedPort, settings);
            RecordConnectFailure(settings, ex, cleanupError);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RestoreConnectionSelection(string? requestedSelectedPort, SerialSettings requestedSettings)
    {
        var actualRequestedPort = GetActualPortName(requestedSelectedPort) ?? requestedSettings.PortName;
        var displayPort = IsMockPortName(actualRequestedPort)
            ? ShowMockTestPort ? MockPortDisplayName : MockPortName
            : actualRequestedPort;

        _currentSerialSettings = requestedSettings.Clone();
        _selectedBaudRate = requestedSettings.BaudRate;
        _selectedTxLineEnding = requestedSettings.TxLineEnding;
        _selectedPort = string.IsNullOrWhiteSpace(displayPort) ? null : displayPort;

        if (!string.IsNullOrWhiteSpace(actualRequestedPort) &&
            !IsMockPortName(actualRequestedPort) &&
            !ContainsActualPort(PortNames, actualRequestedPort))
        {
            PortNames.Add(actualRequestedPort);
        }

        OnPropertyChanged(nameof(SelectedPort));
        OnPropertyChanged(nameof(SelectedBaudRate));
        OnPropertyChanged(nameof(SelectedTxLineEnding));
        OnPropertyChanged(nameof(SelectedDataBits));
        OnPropertyChanged(nameof(SelectedParity));
        OnPropertyChanged(nameof(SelectedStopBits));
        OnPropertyChanged(nameof(SelectedHandshake));
        OnPropertyChanged(nameof(DtrEnable));
        OnPropertyChanged(nameof(RtsEnable));
        OnPropertyChanged(nameof(SelectedRxLineEnding));
        OnPropertyChanged(nameof(SelectedRxEncoding));
        OnPropertyChanged(nameof(ConnectionStateText));
        OnPropertyChanged(nameof(CompactConnectionStatusText));
        OnPropertyChanged(nameof(CurrentPortIsMock));
        RecordPortSelectionChange($"Restored port selection: {_selectedPort ?? "(none)"}");
    }

    private void RecordConnectRequested(SerialSettings settings)
    {
        _lastConnectRequestedPort = settings.PortName;
        _lastConnectRequestedBaud = settings.BaudRate;
        _lastConnectResult = "connecting";
        _lastConnectFailureReason = string.Empty;
        _lastConnectExceptionType = string.Empty;
        _lastConnectFailureTimeText = "(none)";
        _selectedPortAfterConnectFailure = "(none)";
        NotifyConnectDiagnosticsChanged();
        RefreshDiagnostics();
    }

    private void RecordConnectSucceeded(SerialSettings settings)
    {
        _lastConnectRequestedPort = settings.PortName;
        _lastConnectRequestedBaud = settings.BaudRate;
        _lastSuccessfulSerialSettings = settings.Clone();
        _lastSuccessfulPort = string.IsNullOrWhiteSpace(settings.PortName) ? "(none)" : settings.PortName;
        _lastSuccessfulBaudRate = settings.BaudRate;
        _lastConnectResult = "connected";
        _lastConnectFailureReason = string.Empty;
        _lastConnectExceptionType = string.Empty;
        _lastConnectFailureTimeText = "(none)";
        _selectedPortAfterConnectFailure = "(none)";
        NotifyConnectDiagnosticsChanged();
        NotifyPortSelectionDiagnosticsChanged();
        RefreshDiagnostics();
    }

    private void RecordConnectFailure(SerialSettings settings, Exception exception, string cleanupError)
    {
        var kind = ClassifyConnectFailure(exception);
        var reason = CreateConnectFailureReason(settings.PortName, kind);
        var status = CreateConnectFailureStatus(settings.PortName, kind);
        var relevantException = GetRelevantConnectException(exception);

        RuntimeDiagnostics.RecordError("MainViewModel.ConnectAsync", exception);

        _lastConnectRequestedPort = settings.PortName;
        _lastConnectRequestedBaud = settings.BaudRate;
        _lastConnectResult = string.IsNullOrWhiteSpace(cleanupError)
            ? "failed"
            : $"failed; {cleanupError}";
        _lastConnectFailureReason = reason;
        _lastConnectExceptionType = relevantException.GetType().Name;
        _lastConnectFailureTimeText = FormatDiagnosticTime(DateTimeOffset.Now);
        _selectedPortAfterConnectFailure = SelectedPort ?? "(none)";
        _lastBackgroundError = string.IsNullOrWhiteSpace(cleanupError)
            ? status
            : $"{status} {cleanupError}";

        NotifyConnectDiagnosticsChanged();
        SetStatus(_lastBackgroundError);
        RefreshDiagnostics();
        RequestConnectFailureDialog(settings, reason);
    }

    private void RequestConnectFailureDialog(SerialSettings settings, string reason)
    {
        try
        {
            ConnectFailureDialogRequested?.Invoke(
                this,
                new ConnectFailureDialogRequest(
                    settings.PortName,
                    settings.BaudRate,
                    $"{settings.PortName} @ {settings.BaudRate:N0} bps{Environment.NewLine}{reason}"));
        }
        catch (Exception ex)
        {
            _lastBackgroundError = $"Connect failure dialog failed: {ex.Message}";
            RuntimeDiagnostics.RecordError("MainViewModel.RequestConnectFailureDialog", ex);
            SetStatus(_lastBackgroundError);
            RefreshDiagnostics();
        }
    }

    private void NotifyConnectDiagnosticsChanged()
    {
        OnPropertyChanged(nameof(LastConnectRequestedPort));
        OnPropertyChanged(nameof(LastConnectRequestedBaud));
        OnPropertyChanged(nameof(LastConnectResult));
        OnPropertyChanged(nameof(LastConnectFailureReason));
        OnPropertyChanged(nameof(LastConnectExceptionType));
        OnPropertyChanged(nameof(LastConnectFailureTimeText));
        OnPropertyChanged(nameof(SelectedPortAfterConnectFailure));
    }

    private static string CreateConnectFailureReason(string portName, ConnectFailureKind kind)
    {
        return kind switch
        {
            ConnectFailureKind.AccessDenied =>
                $"Port {portName} is already in use or access was denied. Close other serial terminals and try again.",
            ConnectFailureKind.PortNotFound =>
                $"Selected port {portName} was not found. Refresh ports and check the cable or driver.",
            ConnectFailureKind.OpenFailed =>
                $"Port {portName} could not be opened. Check the cable, driver, and whether another terminal is connected.",
            _ =>
                $"Port {portName} could not be opened. See Diagnostics for details."
        };
    }

    private static string CreateConnectFailureStatus(string portName, ConnectFailureKind kind)
    {
        return kind switch
        {
            ConnectFailureKind.AccessDenied => $"Connect failed: {portName} is in use or access denied.",
            ConnectFailureKind.PortNotFound => $"Connect failed: {portName} not found.",
            ConnectFailureKind.OpenFailed => $"Connect failed: {portName} could not be opened.",
            _ => $"Connect failed: {portName}. See Diagnostics."
        };
    }

    private static ConnectFailureKind ClassifyConnectFailure(Exception exception)
    {
        var relevantException = GetRelevantConnectException(exception);
        var message = CreateExceptionMessageChain(exception).ToLowerInvariant();

        if (relevantException is UnauthorizedAccessException ||
            message.Contains("access denied", StringComparison.Ordinal) ||
            message.Contains("access is denied", StringComparison.Ordinal) ||
            message.Contains("unauthorized", StringComparison.Ordinal) ||
            message.Contains("already in use", StringComparison.Ordinal) ||
            message.Contains("being used", StringComparison.Ordinal) ||
            message.Contains("busy", StringComparison.Ordinal))
        {
            return ConnectFailureKind.AccessDenied;
        }

        if (relevantException is FileNotFoundException or DirectoryNotFoundException ||
            message.Contains("not found", StringComparison.Ordinal) ||
            message.Contains("does not exist", StringComparison.Ordinal) ||
            message.Contains("not exist", StringComparison.Ordinal) ||
            message.Contains("cannot find", StringComparison.Ordinal) ||
            message.Contains("no such", StringComparison.Ordinal) ||
            message.Contains("invalid port", StringComparison.Ordinal))
        {
            return ConnectFailureKind.PortNotFound;
        }

        if (relevantException is IOException ||
            message.Contains("could not open", StringComparison.Ordinal) ||
            message.Contains("cannot open", StringComparison.Ordinal) ||
            message.Contains("failed to open", StringComparison.Ordinal) ||
            message.Contains("open failed", StringComparison.Ordinal) ||
            message.Contains("semaphore timeout", StringComparison.Ordinal))
        {
            return ConnectFailureKind.OpenFailed;
        }

        return ConnectFailureKind.Unknown;
    }

    private static Exception GetRelevantConnectException(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current;
    }

    private static string CreateExceptionMessageChain(Exception exception)
    {
        var builder = new StringBuilder();
        var current = exception;
        while (current is not null)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(current.Message);
            }

            current = current.InnerException;
        }

        return builder.ToString();
    }

    private Task DisconnectAsync()
    {
        return DisconnectAsync(CancellationToken.None);
    }

    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _connectionLifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await DisconnectCoreAsync(cancellationToken, updateBusy: true);
        }
        finally
        {
            _connectionLifecycleGate.Release();
        }
    }

    private async Task DisconnectCoreAsync(CancellationToken cancellationToken, bool updateBusy)
    {
        if (!IsConnected &&
            _connectionCancellation is null &&
            _serialService.ConnectionState == SerialConnectionState.Disconnected)
        {
            return;
        }

        if (updateBusy)
        {
            IsBusy = true;
        }

        var selectedPortBeforeDisconnect = SelectedPort;
        var settingsBeforeDisconnect = CreateCurrentSettings();
        var cleanupErrors = new List<string>();
        try
        {
            _sequenceCancellation?.Cancel();
            if (IsSequenceRunning)
            {
                await RunDisconnectCleanupAsync("Command sequence stop", () => WaitForSequenceStopAsync(cancellationToken), cleanupErrors);
            }

            await RunDisconnectCleanupAsync("Serial bridge stop", () => StopBridgeCoreAsync(disableRequested: true, cancellationToken), cleanupErrors);

            _connectionCancellation?.Cancel();
            await RunDisconnectCleanupAsync("Log pipeline stop", () => _logPipeline.StopAsync(cancellationToken), cleanupErrors);
            await RunDisconnectCleanupAsync("Serial disconnect", () => _serialService.DisconnectAsync(cancellationToken), cleanupErrors);

            if (_observeLogsTask is not null)
            {
                await RunDisconnectCleanupAsync("Log observer stop", () => _observeLogsTask.WaitAsync(cancellationToken), cleanupErrors);
            }

            await RunDisconnectCleanupAsync("Event detector stop", () => _eventDetector.StopAsync(cancellationToken), cleanupErrors);
            if (_observeEventsTask is not null)
            {
                await RunDisconnectCleanupAsync("Event observer stop", () => _observeEventsTask.WaitAsync(cancellationToken), cleanupErrors);
            }

            if (_observeEventContextsTask is not null)
            {
                await RunDisconnectCleanupAsync("Event context observer stop", () => _observeEventContextsTask.WaitAsync(cancellationToken), cleanupErrors);
            }

            _connectionCancellation?.Dispose();
            _connectionCancellation = null;
            _observeLogsTask = null;
            _observeEventsTask = null;
            _observeEventContextsTask = null;
            await RunDisconnectCleanupAsync("File writer stop", () => _fileLogWriter.StopAsync(cancellationToken), cleanupErrors);
            IsConnected = _serialService.IsConnected;
            if (!IsConnected)
            {
                ApplyRxDisplayRuntime(
                    SelectedRxDisplayMode,
                    HexGroupTimeoutMs,
                    "disconnected receive mode applied");
                ClearPendingReconnectSettings();
            }

            OnPropertyChanged(nameof(HexGroupTimeoutAppliedText));
            OnPropertyChanged(nameof(HexGroupTimeoutHeaderText));
            RecordDisconnectPortPreservation(selectedPortBeforeDisconnect, settingsBeforeDisconnect);
            if (cleanupErrors.Count == 0)
            {
                SetStatus(IsConnected ? "Disconnect incomplete: serial service is still connected." : "Disconnected");
            }
            else
            {
                var message = string.Join(" ", cleanupErrors);
                _lastBackgroundError = IsConnected
                    ? $"Disconnect incomplete: {message}"
                    : $"Disconnected with cleanup warnings: {message}";
                SetStatus(_lastBackgroundError);
                RefreshDiagnostics();
            }

            SetFooter(CreateFooterStatus());
            if (!ShowMockTestPort)
            {
                _ = RefreshPortsAsync();
            }
        }
        finally
        {
            if (updateBusy)
            {
                IsBusy = false;
            }
        }
    }

    private void RecordDisconnectPortPreservation(string? selectedPortBeforeDisconnect, SerialSettings settingsBeforeDisconnect)
    {
        var expectedPort = GetActualPortName(selectedPortBeforeDisconnect) ?? settingsBeforeDisconnect.PortName;
        var currentPort = GetActualPortName(SelectedPort) ?? _currentSerialSettings.PortName;
        _lastDisconnectPreservedPort = string.IsNullOrWhiteSpace(expectedPort) ||
            string.Equals(expectedPort, currentPort, StringComparison.OrdinalIgnoreCase);

        if (!_lastDisconnectPreservedPort && !string.IsNullOrWhiteSpace(expectedPort))
        {
            RestoreConnectionSelection(selectedPortBeforeDisconnect, settingsBeforeDisconnect);
            currentPort = GetActualPortName(SelectedPort) ?? _currentSerialSettings.PortName;
            _lastDisconnectPreservedPort = string.Equals(expectedPort, currentPort, StringComparison.OrdinalIgnoreCase);
        }

        _lastPortSelectionChangeReason = _lastDisconnectPreservedPort
            ? $"Disconnect preserved port: {(string.IsNullOrWhiteSpace(expectedPort) ? "(none)" : expectedPort)}"
            : $"Disconnect could not preserve port: {(string.IsNullOrWhiteSpace(expectedPort) ? "(none)" : expectedPort)}";
        UpdateSelectedPortAvailability();
        NotifyPortSelectionDiagnosticsChanged();
    }

    private async Task RunDisconnectCleanupAsync(string label, Func<Task> cleanup, ICollection<string> cleanupErrors)
    {
        try
        {
            await cleanup();
        }
        catch (Exception ex)
        {
            var message = $"{label} failed: {ex.Message}";
            cleanupErrors.Add(message);
            RuntimeDiagnostics.RecordError($"MainViewModel.DisconnectAsync.{label}", ex);
        }
    }

    private async Task ObserveLogsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in _logPipeline.Logs.ReadAllAsync(cancellationToken))
            {
                VerifyMockSequence(line);
                if (line.IsPartialRxTerminator)
                {
                    FanOutLogLine(line, fileEligible: false, detectEvent: false);
                    continue;
                }

                FanOutLogLine(line, fileEligible: true, detectEvent: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _lastBackgroundError = $"Log observer failed: {ex.Message}";
            RuntimeDiagnostics.RecordError("MainViewModel.ObserveLogsAsync", ex);
            SetStatus(_lastBackgroundError);
            RefreshDiagnostics();
        }
    }

    private async Task ObserveEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var detectedEvent in _eventDetector.DetectedEvents.ReadAllAsync(cancellationToken))
            {
                _eventBatchDispatcher.Post(detectedEvent);
                QueueEventNotification(detectedEvent);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _lastBackgroundError = $"Event observer failed: {ex.Message}";
            RuntimeDiagnostics.RecordError("MainViewModel.ObserveEventsAsync", ex);
            SetStatus(_lastBackgroundError);
            RefreshDiagnostics();
        }
    }

    private void QueueEventNotification(DetectedEvent detectedEvent)
    {
        if (!detectedEvent.TrayNotificationEnabled &&
            !detectedEvent.SoundNotificationEnabled &&
            !detectedEvent.PopupNotificationEnabled)
        {
            return;
        }

        var key = $"{detectedEvent.RuleName}\u001F{detectedEvent.Keyword}";
        TimeSpan delay;
        PendingEventNotification scheduledState;
        lock (_eventNotificationGate)
        {
            if (!_pendingEventNotifications.TryGetValue(key, out var pending))
            {
                pending = new PendingEventNotification();
                _pendingEventNotifications.Add(key, pending);
            }

            pending.LatestEvent = detectedEvent;
            pending.EventCount++;
            pending.ShowTray = detectedEvent.TrayNotificationEnabled;
            pending.PlaySound = detectedEvent.SoundNotificationEnabled;
            pending.ShowPopup = detectedEvent.PopupNotificationEnabled;
            pending.CooldownSeconds = Math.Clamp(detectedEvent.NotificationCooldownSeconds, 5, 3_600);
            if (pending.IsScheduled)
            {
                return;
            }

            var cooldownRemaining = pending.LastNotificationTime == DateTimeOffset.MinValue
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(pending.CooldownSeconds) - (DateTimeOffset.UtcNow - pending.LastNotificationTime);
            delay = cooldownRemaining > EventNotificationGroupingInterval
                ? cooldownRemaining
                : EventNotificationGroupingInterval;
            pending.IsScheduled = true;
            scheduledState = pending;
        }

        _ = DispatchPendingEventNotificationAsync(key, scheduledState, delay, _eventNotificationCancellation.Token);
    }

    private async Task DispatchPendingEventNotificationAsync(
        string key,
        PendingEventNotification scheduledState,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);

            EventNotificationRequest? request = null;
            lock (_eventNotificationGate)
            {
                if (!_pendingEventNotifications.TryGetValue(key, out var pending) ||
                    !ReferenceEquals(pending, scheduledState) ||
                    pending.LatestEvent is null ||
                    pending.EventCount <= 0)
                {
                    return;
                }

                var latestEvent = pending.LatestEvent;
                var count = pending.EventCount;
                var title = count == 1
                    ? $"Serial event: {latestEvent.RuleName}"
                    : $"Serial event: {latestEvent.RuleName} ({count:N0})";
                var latestMessage = FormatEventNotificationMessage(latestEvent);
                var message = count == 1
                    ? $"{latestEvent.TimeText}  {TruncateStatusText(latestMessage, 220)}"
                    : $"{count:N0} events grouped. Latest: {latestEvent.TimeText}  {TruncateStatusText(latestMessage, 180)}";
                request = new EventNotificationRequest(
                    title,
                    message,
                    count,
                    pending.ShowTray,
                    pending.PlaySound,
                    pending.ShowPopup);

                pending.LatestEvent = null;
                pending.EventCount = 0;
                pending.IsScheduled = false;
                pending.LastNotificationTime = DateTimeOffset.UtcNow;
            }

            Interlocked.Increment(ref _eventNotificationBatchCount);
            Interlocked.Add(ref _eventNotificationEventCount, request.EventCount);
            RunOnUiThread(() => EventNotificationRequested?.Invoke(this, request));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("MainViewModel.DispatchPendingEventNotificationAsync", ex);
        }
    }

    private string FormatEventNotificationMessage(DetectedEvent detectedEvent)
    {
        if (detectedEvent.SourceLogLine?.ContentMode == LogRuleMatchMode.Hex &&
            detectedEvent.Direction == LogDirection.Rx &&
            detectedEvent.SourceLogLine?.RawBytes is { Length: > 0 } rawBytes)
        {
            return $"HEX: {LogRuleMatcher.FormatBytesPreview(rawBytes, 64)}";
        }

        return detectedEvent.Message;
    }

    private void ClearPendingEventNotifications()
    {
        lock (_eventNotificationGate)
        {
            _pendingEventNotifications.Clear();
        }
    }

    private async Task ObserveEventContextsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var context in _eventDetector.CompletedEventContexts.ReadAllAsync(cancellationToken))
            {
                var dropped = _eventContextBatchDispatcher.Post(context);
                if (dropped > 0)
                {
                    Interlocked.Add(ref _eventContextUiDroppedCount, dropped);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RunOnUiThread(() => RecordEventContextUiError($"Event context observer failed: {ex.Message}"));
        }
    }

    private async Task SendCurrentCommandAsync()
    {
        var text = Commands.CurrentCommandText.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var sent = await SendCommandAsync(new TxCommand(text, text), addToHistory: true);
        if (sent)
        {
            Commands.CurrentCommandText = string.Empty;
        }
    }

    private async Task SendSavedCommandAsync(object? parameter)
    {
        if (parameter is TxCommand command)
        {
            SelectedSavedCommand = command;
            await SendCommandAsync(command, addToHistory: true);
        }
    }

    private async Task AddMarkerAsync()
    {
        await AddMarkerAsync(
            MarkerText,
            string.IsNullOrWhiteSpace(MarkerText) ? "Quick marker" : "Custom marker");
    }

    public async Task AddDefaultMarkerAsync()
    {
        await AddMarkerAsync(null, "Quick marker");
    }

    public string GetVisibleLogPlainTextSnapshot(out int lineCount)
    {
        var lines = Log.GetVisibleSearchLinesSnapshot();
        lineCount = lines.Count;
        return lines.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, lines);
    }

    public bool TryGetVisibleLogSinceLastTxPlainTextSnapshot(
        out string text,
        out int lineCount,
        out int characterCount)
    {
        var lines = Log.GetVisibleSearchLinesSnapshot();
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            if (!IsVisibleTxLine(lines[index]))
            {
                continue;
            }

            var segment = lines.Skip(index).ToArray();
            text = string.Join(Environment.NewLine, segment);
            lineCount = segment.Length;
            characterCount = text.Length;
            return true;
        }

        text = string.Empty;
        lineCount = 0;
        characterCount = 0;
        return false;
    }

    public bool TryGetVisibleLogSinceLastMarkPlainTextSnapshot(
        out string text,
        out int lineCount,
        out int characterCount)
    {
        var lines = Log.GetVisibleSearchLinesSnapshot();
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            if (!IsVisibleMarkLine(lines[index]))
            {
                continue;
            }

            var segment = lines.Skip(index).ToArray();
            text = string.Join(Environment.NewLine, segment);
            lineCount = segment.Length;
            characterCount = text.Length;
            return true;
        }

        text = string.Empty;
        lineCount = 0;
        characterCount = 0;
        return false;
    }

    public Task ClearScreenFromXtermContextMenuAsync()
    {
        RecordXtermContextMenuAction("Clear screen");
        return ClearScreenAsync();
    }

    public void SearchSelectedTextFromXterm(string? selectedText)
    {
        try
        {
            var searchText = NormalizeSelectedSearchText(selectedText);
            if (string.IsNullOrWhiteSpace(searchText))
            {
                RecordXtermContextMenuError("Search selected text ignored: no xterm selection.");
                return;
            }

            _lastSearchSelectedTextLength = searchText.Length;
            OnPropertyChanged(nameof(LastSearchSelectedTextLength));
            SearchText = searchText;
            AddCurrentSearchTextToHistory();
            RunManualVisibleLogSearch(SearchMove.Next);
            RequestXtermSearch(SearchMove.Next);
            RecordXtermContextMenuAction($"Search selected text ({searchText.Length:N0} chars)");
            RefreshDiagnostics();
        }
        catch (Exception ex)
        {
            RecordXtermContextMenuError($"Search selected text failed: {ex.Message}");
        }
    }

    public void RecordXtermContextMenuAction(string action)
    {
        _lastXtermContextMenuAction = action;
        _lastXtermContextMenuError = string.Empty;
        OnPropertyChanged(nameof(LastXtermContextMenuAction));
        OnPropertyChanged(nameof(LastXtermContextMenuError));
        SetStatus(action);
        RefreshDiagnostics();
    }

    public void RecordXtermContextMenuError(string message)
    {
        Interlocked.Increment(ref _xtermContextMenuErrorCount);
        _lastXtermContextMenuError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(XtermContextMenuErrorCount));
        OnPropertyChanged(nameof(LastXtermContextMenuError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    public void RecordCopyAllVisibleSuccess(int lineCount)
    {
        _lastCopyVisibleLineCount = Math.Max(0, lineCount);
        OnPropertyChanged(nameof(LastCopyVisibleLineCount));
        RecordXtermContextMenuAction($"Copied all visible log lines: {_lastCopyVisibleLineCount:N0}");
    }

    public void RecordCopySinceLastTxSuccess(int lineCount, int characterCount)
    {
        _lastCopySinceTxActionTimeText = DateTimeOffset.Now.LocalDateTime.ToString(
            "yyyy-MM-dd HH:mm:ss.fff",
            CultureInfo.InvariantCulture);
        _lastCopySinceTxLineCount = Math.Max(0, lineCount);
        _lastCopySinceTxCharacterCount = Math.Max(0, characterCount);
        _lastCopySinceTxResult = "copied";
        _lastCopySinceTxError = string.Empty;
        OnPropertyChanged(nameof(LastCopySinceTxActionTimeText));
        OnPropertyChanged(nameof(LastCopySinceTxLineCount));
        OnPropertyChanged(nameof(LastCopySinceTxCharacterCount));
        OnPropertyChanged(nameof(LastCopySinceTxResult));
        OnPropertyChanged(nameof(LastCopySinceTxError));
        RecordXtermContextMenuAction(
            $"Copied since last TX: {_lastCopySinceTxLineCount:N0} lines, {_lastCopySinceTxCharacterCount:N0} chars");
    }

    public void RecordCopySinceLastTxNoTx()
    {
        _lastCopySinceTxActionTimeText = DateTimeOffset.Now.LocalDateTime.ToString(
            "yyyy-MM-dd HH:mm:ss.fff",
            CultureInfo.InvariantCulture);
        _lastCopySinceTxLineCount = 0;
        _lastCopySinceTxCharacterCount = 0;
        _lastCopySinceTxResult = "no tx found";
        _lastCopySinceTxError = string.Empty;
        _lastXtermContextMenuAction = "Copy since last TX: no TX found";
        _lastXtermContextMenuError = string.Empty;
        OnPropertyChanged(nameof(LastCopySinceTxActionTimeText));
        OnPropertyChanged(nameof(LastCopySinceTxLineCount));
        OnPropertyChanged(nameof(LastCopySinceTxCharacterCount));
        OnPropertyChanged(nameof(LastCopySinceTxResult));
        OnPropertyChanged(nameof(LastCopySinceTxError));
        OnPropertyChanged(nameof(LastXtermContextMenuAction));
        OnPropertyChanged(nameof(LastXtermContextMenuError));
        SetStatus("No TX found in visible buffer.");
        RefreshDiagnostics();
    }

    public void RecordCopySinceLastTxError(string message)
    {
        var safeMessage = string.IsNullOrWhiteSpace(message)
            ? "Copy since last TX failed."
            : message.Trim();

        _lastCopySinceTxActionTimeText = DateTimeOffset.Now.LocalDateTime.ToString(
            "yyyy-MM-dd HH:mm:ss.fff",
            CultureInfo.InvariantCulture);
        _lastCopySinceTxResult = "error";
        _lastCopySinceTxError = safeMessage;
        Interlocked.Increment(ref _copySinceTxErrorCount);
        _lastBackgroundError = safeMessage;
        OnPropertyChanged(nameof(LastCopySinceTxActionTimeText));
        OnPropertyChanged(nameof(LastCopySinceTxResult));
        OnPropertyChanged(nameof(LastCopySinceTxError));
        OnPropertyChanged(nameof(CopySinceTxErrorCount));
        RecordXtermContextMenuError(safeMessage);
    }

    public void RecordCopySinceLastMarkSuccess(int lineCount, int characterCount)
    {
        _lastCopySinceMarkActionTimeText = DateTimeOffset.Now.LocalDateTime.ToString(
            "yyyy-MM-dd HH:mm:ss.fff",
            CultureInfo.InvariantCulture);
        _lastCopySinceMarkLineCount = Math.Max(0, lineCount);
        _lastCopySinceMarkCharacterCount = Math.Max(0, characterCount);
        _lastCopySinceMarkResult = "copied";
        _lastCopySinceMarkError = string.Empty;
        OnPropertyChanged(nameof(LastCopySinceMarkActionTimeText));
        OnPropertyChanged(nameof(LastCopySinceMarkLineCount));
        OnPropertyChanged(nameof(LastCopySinceMarkCharacterCount));
        OnPropertyChanged(nameof(LastCopySinceMarkResult));
        OnPropertyChanged(nameof(LastCopySinceMarkError));
        RecordXtermContextMenuAction(
            $"Copied since last MARK: {_lastCopySinceMarkLineCount:N0} lines, {_lastCopySinceMarkCharacterCount:N0} chars");
    }

    public void RecordCopySinceLastMarkNoMark()
    {
        _lastCopySinceMarkActionTimeText = DateTimeOffset.Now.LocalDateTime.ToString(
            "yyyy-MM-dd HH:mm:ss.fff",
            CultureInfo.InvariantCulture);
        _lastCopySinceMarkLineCount = 0;
        _lastCopySinceMarkCharacterCount = 0;
        _lastCopySinceMarkResult = "no mark found";
        _lastCopySinceMarkError = string.Empty;
        _lastXtermContextMenuAction = "Copy since last MARK: no MARK found";
        _lastXtermContextMenuError = string.Empty;
        OnPropertyChanged(nameof(LastCopySinceMarkActionTimeText));
        OnPropertyChanged(nameof(LastCopySinceMarkLineCount));
        OnPropertyChanged(nameof(LastCopySinceMarkCharacterCount));
        OnPropertyChanged(nameof(LastCopySinceMarkResult));
        OnPropertyChanged(nameof(LastCopySinceMarkError));
        OnPropertyChanged(nameof(LastXtermContextMenuAction));
        OnPropertyChanged(nameof(LastXtermContextMenuError));
        SetStatus("No MARK found in visible buffer.");
        RefreshDiagnostics();
    }

    public void RecordCopySinceLastMarkError(string message)
    {
        var safeMessage = string.IsNullOrWhiteSpace(message)
            ? "Copy since last MARK failed."
            : message.Trim();

        _lastCopySinceMarkActionTimeText = DateTimeOffset.Now.LocalDateTime.ToString(
            "yyyy-MM-dd HH:mm:ss.fff",
            CultureInfo.InvariantCulture);
        _lastCopySinceMarkResult = "error";
        _lastCopySinceMarkError = safeMessage;
        Interlocked.Increment(ref _copySinceMarkErrorCount);
        _lastBackgroundError = safeMessage;
        OnPropertyChanged(nameof(LastCopySinceMarkActionTimeText));
        OnPropertyChanged(nameof(LastCopySinceMarkResult));
        OnPropertyChanged(nameof(LastCopySinceMarkError));
        OnPropertyChanged(nameof(CopySinceMarkErrorCount));
        RecordXtermContextMenuError(safeMessage);
    }

    public void RecordDisconnectConfirmationResult(string result)
    {
        _lastDisconnectConfirmationResult = string.IsNullOrWhiteSpace(result)
            ? "skipped"
            : result.Trim();
        _lastDisconnectConfirmationError = string.Empty;
        OnPropertyChanged(nameof(LastDisconnectConfirmationResult));
        OnPropertyChanged(nameof(LastDisconnectConfirmationError));
        RefreshDiagnostics();
    }

    public void RecordDisconnectConfirmationError(string message)
    {
        var safeMessage = string.IsNullOrWhiteSpace(message)
            ? "Disconnect confirmation failed."
            : message.Trim();

        Interlocked.Increment(ref _disconnectConfirmationErrorCount);
        _lastDisconnectConfirmationError = safeMessage;
        _lastBackgroundError = safeMessage;
        OnPropertyChanged(nameof(DisconnectConfirmationErrorCount));
        OnPropertyChanged(nameof(LastDisconnectConfirmationError));
        SetStatus(safeMessage);
        RefreshDiagnostics();
    }

    private void RecordTimestampDisplayModeError(string message)
    {
        var safeMessage = string.IsNullOrWhiteSpace(message)
            ? "Timestamp display mode update failed."
            : message.Trim();

        Interlocked.Increment(ref _timestampDisplayModeErrorCount);
        _lastTimestampDisplayModeError = safeMessage;
        _lastBackgroundError = safeMessage;
        OnPropertyChanged(nameof(TimestampDisplayModeErrorCount));
        OnPropertyChanged(nameof(LastTimestampDisplayModeError));
        SetStatus(safeMessage);
        RefreshDiagnostics();
    }

    private async Task SetSessionAsync()
    {
        try
        {
            if (FileLoggingEnabled)
            {
                RecordSessionError("Log file name can be changed only while LOG is OFF.");
                return;
            }

            if (!LogFileNamePolicy.TryValidate(SessionName, out var logFileName, out var validationError))
            {
                RecordSessionError(validationError);
                return;
            }

            if (string.IsNullOrWhiteSpace(logFileName))
            {
                RecordSessionError("Log file name is empty.");
                return;
            }

            CurrentSessionName = logFileName;
            _sessionStartedTime = DateTimeOffset.Now;
            OnPropertyChanged(nameof(SessionStartedTimeText));
            ApplySessionFileNaming(requestNewFile: true);
            if (IsConnected)
            {
                await AddMarkerAsync($"Log file name: {logFileName}", "Log file name marker");
                RecordSessionAction($"Log file name set: {logFileName}");
            }
            else
            {
                RecordSessionAction($"Log file name ready for next connection: {logFileName}");
            }
        }
        catch (Exception ex)
        {
            RecordSessionError($"Set session failed: {ex.Message}");
        }

    }

    private async Task EndSessionAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(CurrentSessionName))
            {
                RecordSessionError("No active session to end.");
                return;
            }

            var endedSessionName = CurrentSessionName;
            if (IsConnected)
            {
                await AddMarkerAsync($"Session end: {endedSessionName}", "Session end marker");
            }

            CurrentSessionName = string.Empty;
            _sessionStartedTime = null;
            OnPropertyChanged(nameof(SessionStartedTimeText));
            ApplySessionFileNaming(requestNewFile: true);
            RecordSessionAction($"Session ended: {endedSessionName}");
        }
        catch (Exception ex)
        {
            RecordSessionError($"End session failed: {ex.Message}");
        }

    }

    private Task AddMarkerAsync(string? markerText, string action)
    {
        try
        {
            if (!IsConnected)
            {
                RecordMarkerError("Marker insert failed: serial monitor is disconnected.");
                return Task.CompletedTask;
            }

            var insertedAt = DateTimeOffset.Now;
            var text = FormatMarkerText(insertedAt, markerText);
            var markerLine = new LogLine(insertedAt, LogDirection.Mark, text);

            FanOutLogLine(markerLine, fileEligible: true, detectEvent: true);
            RecordMarkerSuccess(text, action, insertedAt);
            SetFooter(CreateFooterStatus());
        }
        catch (Exception ex)
        {
            RecordMarkerError($"Marker insert failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private static string FormatMarkerText(DateTimeOffset insertedAt, string? markerText)
    {
        var timestamp = insertedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var trimmedText = markerText?.Trim();
        return string.IsNullOrWhiteSpace(trimmedText)
            ? $"[MARK TIME] {timestamp}"
            : $"[MARK TIME] {timestamp} | {trimmedText}";
    }

    public async Task<bool> SendSavedCommandShortcutAsync(string shortcutText)
    {
        if (IsManualTxBusy)
        {
            SetStatus("TX waiting for bridge idle; saved command shortcut ignored.");
            return true;
        }

        if (!TryNormalizeSavedCommandShortcut(shortcutText, out var normalizedShortcut, out var error) ||
            string.IsNullOrWhiteSpace(normalizedShortcut))
        {
            RecordCommandEditError($"Shortcut ignored: {error}");
            return true;
        }

        var matchingCommands = Commands.SavedCommands
            .Where(command =>
                TryNormalizeSavedCommandShortcut(command.OptionalShortcut, out var commandShortcut, out _) &&
                string.Equals(commandShortcut, normalizedShortcut, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matchingCommands.Length == 0)
        {
            return false;
        }

        if (matchingCommands.Length > 1)
        {
            RecordCommandEditError($"Shortcut conflict: {normalizedShortcut} is assigned to multiple saved commands.");
            return true;
        }

        SelectedSavedCommand = matchingCommands[0];
        await SendCommandAsync(matchingCommands[0], addToHistory: true);
        return true;
    }

    private bool CanRunSelectedCommandSequence()
    {
        return IsConnected &&
            !IsBusy &&
            !IsBridgeActive &&
            !IsSequenceRunning &&
            SelectedCommandSequence is { } sequence &&
            sequence.Steps.Count > 0;
    }

    private async Task RunSelectedCommandSequenceAsync()
    {
        if (!CanRunSelectedCommandSequence() || SelectedCommandSequence is null)
        {
            RecordSequenceError(IsConnected
                ? "Sequence run failed: select a sequence with at least one step."
                : "Sequence run failed: serial port is disconnected.");
            return;
        }

        var sequence = CloneCommandSequence(SelectedCommandSequence);
        var sequenceSendMode = NormalizeTxSendMode(SelectedTxSendMode);
        if (sequenceSendMode == TxSendMode.Hex &&
            !TryValidateHexSequenceSteps(sequence.Steps, out var validationError))
        {
            RecordSequenceError($"Sequence run failed: {validationError}");
            return;
        }

        _sequenceCancellation?.Dispose();
        _sequenceCancellation = new CancellationTokenSource();
        var cancellationToken = _sequenceCancellation.Token;

        IsSequenceRunning = true;
        RunningSequenceName = sequence.Name;
        CurrentSequenceStepText = "(starting)";
        CompletedSequenceSteps = 0;
        _lastSequenceError = string.Empty;
        _lastSequenceActionStatus = $"Running sequence: {sequence.Name}";
        Interlocked.Increment(ref _sequenceRunCount);
        OnPropertyChanged(nameof(SequenceRunCount));
        OnPropertyChanged(nameof(LastSequenceError));
        OnPropertyChanged(nameof(LastSequenceActionStatus));
        SetStatus($"Running sequence: {sequence.Name}");
        RefreshDiagnostics();

        try
        {
            for (var index = 0; index < sequence.Steps.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsConnected)
                {
                    RecordSequenceError("Sequence stopped: serial port is disconnected.");
                    return;
                }

                var step = sequence.Steps[index];
                CurrentSequenceStepText = $"{index + 1:N0}/{sequence.Steps.Count:N0} {step.DisplayName}";
                var sent = await SendCommandAsync(new TxCommand(step.DisplayName, step.CommandText)
                {
                    LineEndingMode = step.LineEndingMode
                }, addToHistory: false, modeOverride: sequenceSendMode);

                if (!sent)
                {
                    RecordSequenceError($"Sequence stopped: step failed ({step.DisplayName}).");
                    return;
                }

                CompletedSequenceSteps = index + 1;
                if (step.DelayAfterMs > 0)
                {
                    await Task.Delay(step.DelayAfterMs, cancellationToken);
                }
            }

            RecordSequenceStatus($"Sequence completed: {sequence.Name}");
        }
        catch (OperationCanceledException)
        {
            RecordSequenceStatus($"Sequence stopped: {sequence.Name}");
        }
        catch (Exception ex)
        {
            RecordSequenceError($"Sequence failed: {ex.Message}");
        }
        finally
        {
            IsSequenceRunning = false;
            RunningSequenceName = "(none)";
            CurrentSequenceStepText = "(none)";
            _sequenceCancellation?.Dispose();
            _sequenceCancellation = null;
            NotifyCommandStates();
            RefreshDiagnostics();
        }
    }

    private Task StopCommandSequenceAsync()
    {
        if (!IsSequenceRunning)
        {
            return Task.CompletedTask;
        }

        Interlocked.Increment(ref _sequenceStopCount);
        OnPropertyChanged(nameof(SequenceStopCount));
        _sequenceCancellation?.Cancel();
        RecordSequenceStatus("Stopping command sequence...");
        RefreshDiagnostics();
        return Task.CompletedTask;
    }

    private async Task<bool> SendCommandAsync(TxCommand sourceCommand, bool addToHistory, TxSendMode? modeOverride = null)
    {
        var commandText = sourceCommand.CommandText.Trim();
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return false;
        }

        if (!IsConnected)
        {
            RecordTxError("TX failed: serial port is disconnected.");
            return false;
        }

        var lineEndingMode = sourceCommand.LineEndingMode ?? SelectedTxLineEnding;
        var sendMode = NormalizeTxSendMode(modeOverride ?? SelectedTxSendMode);
        var command = new TxCommand(sourceCommand.Name, commandText)
        {
            LineEndingMode = lineEndingMode,
            OptionalShortcut = sourceCommand.OptionalShortcut
        };

        try
        {
            var txRawBytes = Encoding.UTF8.GetBytes(command.CommandText + ToLineEnding(lineEndingMode));
            var txByteCount = txRawBytes.Length;
            var hexParseError = string.Empty;

            switch (sendMode)
            {
                case TxSendMode.Hex:
                    if (!HexPayloadParser.TryParse(command.CommandText, out var hexPayload, out hexParseError))
                    {
                        RecordTxAttemptDiagnostics(sendMode, command.CommandText, 0, hexParseError);
                        RecordTxError("Invalid HEX TX input.");
                        return false;
                    }

                    txByteCount = hexPayload.Length;
                    txRawBytes = hexPayload;
                    break;

                default:
                    break;
            }

            var txDisplayText = FormatTxLogText(sendMode, command.CommandText, txRawBytes);

            if (IsBridgeActive)
            {
                var transmitResult = await _bridgeService.QueueManualTransmitAsync(
                    token => _serialService.SendBytesAsync(txRawBytes, txDisplayText, token),
                    CancellationToken.None);
                if (transmitResult != ManualTransmitResult.Sent)
                {
                    var message = transmitResult switch
                    {
                        ManualTransmitResult.Busy => "TX busy: another manual TX is already waiting or sending.",
                        ManualTransmitResult.Canceled => "TX canceled: bridge stopped or the device disconnected.",
                        ManualTransmitResult.BridgeNotRunning => "TX canceled: bridge state changed; retry the command.",
                        _ => "TX failed in the bridge transmit scheduler."
                    };
                    RecordTxError(message);
                    return false;
                }
            }
            else
            {
                await _serialService.SendBytesAsync(txRawBytes, txDisplayText, CancellationToken.None);
            }

            var txLine = LogLine.Tx(
                txDisplayText,
                txRawBytes,
                contentMode: sendMode == TxSendMode.Hex
                    ? LogRuleMatchMode.Hex
                    : LogRuleMatchMode.Terminal);
            FanOutLogLine(txLine, fileEligible: true, detectEvent: true);
            RecordTxSuccess(txDisplayText, sendMode, command.CommandText, txByteCount, hexParseError);
            if (addToHistory)
            {
                Commands.AddToHistory(command.CommandText);
            }

            SetFooter(CreateFooterStatus());
            return true;
        }
        catch (Exception ex)
        {
            RecordTxError($"Send failed: {ex.Message}");
            IsConnected = _serialService.IsConnected;
            return false;
        }
    }

    private bool CanSendCurrentCommand()
    {
        return CanSendManualTx && !string.IsNullOrWhiteSpace(Commands.CurrentCommandText);
    }

    private static string FormatBytesAsHex(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(bytes.Length * 3);
        for (var index = 0; index < bytes.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append(bytes[index].ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    internal static string FormatTxLogText(TxSendMode mode, string commandText, byte[] rawBytes)
    {
        ArgumentNullException.ThrowIfNull(commandText);
        ArgumentNullException.ThrowIfNull(rawBytes);
        return mode == TxSendMode.Hex
            ? FormatBytesAsHex(rawBytes)
            : commandText;
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

    public bool NavigateCommandHistory(int direction)
    {
        return Commands.NavigateHistory(direction);
    }

    public void SelectCommandHistoryEntry(CommandHistoryEntry? entry)
    {
        Commands.SelectHistoryEntry(entry);
    }

    public async Task SendCommandHistoryEntryAsync(CommandHistoryEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.CommandText))
        {
            return;
        }

        if (IsManualTxBusy)
        {
            SetStatus("TX waiting for bridge idle; history resend ignored.");
            return;
        }

        Commands.SelectHistoryEntry(entry);
        await SendCommandAsync(new TxCommand(entry.CommandText, entry.CommandText), addToHistory: true);
    }

    public void ClearCommandHistory()
    {
        Commands.ClearHistory();
        SetStatus("Command history cleared.");
        RefreshDiagnostics();
    }

    public void SelectEventFromUi(object? selectedItem)
    {
        try
        {
            if (selectedItem is null)
            {
                SelectedEvent = null;
                return;
            }

            if (selectedItem is DetectedEvent detectedEvent)
            {
                SelectedEvent = detectedEvent;
                SetStatus($"Selected event: {detectedEvent.RuleName} {detectedEvent.DirectionText}");
                return;
            }

            RecordEventSelectionError($"Event selection ignored unexpected item type: {selectedItem.GetType().Name}");
        }
        catch (Exception ex)
        {
            RecordEventSelectionError($"Event selection failed: {ex.Message}");
        }
    }

    public void RefreshSelectedEventContextForUi()
    {
        RefreshSelectedEventContextText();
        OnPropertyChanged(nameof(SelectedEventContextAvailable));
        OnPropertyChanged(nameof(SelectedEventRuleName));
        OnPropertyChanged(nameof(SelectedEventContextLineCount));
        OnPropertyChanged(nameof(SelectedEventContextHeaderText));
        CopyEventContextCommand.NotifyCanExecuteChanged();
        RefreshDiagnostics();
    }

    public bool SelectLatestEventFromUi()
    {
        try
        {
            var latestEvent = Events.Events.LastOrDefault();
            if (latestEvent is null)
            {
                SetStatus("No detected events are available to select.");
                return false;
            }

            SelectedEvent = latestEvent;
            Interlocked.Increment(ref _latestEventSelectCount);
            OnPropertyChanged(nameof(LatestEventSelectCount));
            SetStatus($"Selected latest event: {latestEvent.RuleName} {latestEvent.DirectionText}");
            RefreshDiagnostics();
            return true;
        }
        catch (Exception ex)
        {
            RecordEventSelectionError($"Select latest event failed: {ex.Message}");
            return false;
        }
    }

    public bool AddLogRule(LogRule rule)
    {
        if (!TryNormalizeLogRule(rule, out var normalized, out var error))
        {
            RecordRuleEditError(error);
            return false;
        }

        LogRules.Add(normalized);
        SelectedLogRule = normalized;
        ApplyLogRuleChanges($"Added log rule: {normalized.Name}");
        return true;
    }

    public bool ReplaceSelectedLogRule(LogRule replacement)
    {
        if (SelectedLogRule is null)
        {
            RecordRuleEditError("Select a log rule before editing.");
            return false;
        }

        if (!TryNormalizeLogRule(replacement, out var normalized, out var error))
        {
            RecordRuleEditError(error);
            return false;
        }

        var index = LogRules.IndexOf(SelectedLogRule);
        if (index < 0)
        {
            RecordRuleEditError("Selected log rule was not found.");
            return false;
        }

        LogRules[index] = normalized;
        SelectedLogRule = normalized;
        ApplyLogRuleChanges($"Updated log rule: {normalized.Name}");
        return true;
    }

    public bool DeleteSelectedLogRule()
    {
        if (SelectedLogRule is null)
        {
            RecordRuleEditError("Select a log rule before deleting.");
            return false;
        }

        var deletedName = SelectedLogRule.Name;
        var index = LogRules.IndexOf(SelectedLogRule);
        if (index < 0)
        {
            RecordRuleEditError("Selected log rule was not found.");
            return false;
        }

        LogRules.RemoveAt(index);
        SelectedLogRule = LogRules.Count == 0
            ? null
            : LogRules[Math.Min(index, LogRules.Count - 1)];
        ApplyLogRuleChanges($"Deleted log rule: {deletedName}");
        return true;
    }

    public bool UpdateLogRuleColorFromUi(LogRule rule, string foregroundColor)
    {
        if (rule is null)
        {
            RecordRuleColorChangeError("Select a log rule before changing color.");
            return false;
        }

        var index = LogRules.IndexOf(rule);
        if (index < 0)
        {
            RecordRuleColorChangeError("Selected log rule was not found for color update.");
            return false;
        }

        var previousColor = LogRules[index].ForegroundColor;
        var replacement = CloneLogRule(LogRules[index]);
        replacement.ForegroundColor = NormalizeHighlightColorName(foregroundColor, out var usedFallback);
        if (usedFallback)
        {
            Interlocked.Increment(ref _invalidRuleColorFallbackCount);
            OnPropertyChanged(nameof(InvalidRuleColorFallbackCount));
        }

        if (!TryNormalizeLogRule(replacement, out var normalized, out var error))
        {
            RecordRuleColorChangeError(error);
            return false;
        }

        LogRules[index] = normalized;
        SelectedLogRule = normalized;
        _lastRuleColorChange = $"{normalized.Name}: {previousColor} -> {normalized.ForegroundColor}";
        _lastRuleColorChangeError = string.Empty;
        OnPropertyChanged(nameof(LastRuleColorChange));
        OnPropertyChanged(nameof(LastRuleColorChangeError));
        ApplyLogRuleChanges($"Updated log rule color: {normalized.Name} -> {normalized.ForegroundColor}");
        return true;
    }

    public void ApplyLogRuleChangesFromUi(string status)
    {
        ApplyLogRuleChanges(status);
    }

    public bool AddEventRule(EventRule rule)
    {
        if (!TryNormalizeEventRule(rule, out var normalized, out var error))
        {
            RecordRuleEditError(error);
            return false;
        }

        EventRules.Add(normalized);
        SelectedEventRule = normalized;
        ApplyEventRuleChanges($"Added event rule: {normalized.Name}");
        return true;
    }

    public bool ReplaceSelectedEventRule(EventRule replacement)
    {
        if (SelectedEventRule is null)
        {
            RecordRuleEditError("Select an event rule before editing.");
            return false;
        }

        if (!TryNormalizeEventRule(replacement, out var normalized, out var error))
        {
            RecordRuleEditError(error);
            return false;
        }

        var index = EventRules.IndexOf(SelectedEventRule);
        if (index < 0)
        {
            RecordRuleEditError("Selected event rule was not found.");
            return false;
        }

        EventRules[index] = normalized;
        SelectedEventRule = normalized;
        ApplyEventRuleChanges($"Updated event rule: {normalized.Name}");
        return true;
    }

    public bool DeleteSelectedEventRule()
    {
        if (SelectedEventRule is null)
        {
            RecordRuleEditError("Select an event rule before deleting.");
            return false;
        }

        var deletedName = SelectedEventRule.Name;
        var index = EventRules.IndexOf(SelectedEventRule);
        if (index < 0)
        {
            RecordRuleEditError("Selected event rule was not found.");
            return false;
        }

        EventRules.RemoveAt(index);
        SelectedEventRule = EventRules.Count == 0
            ? null
            : EventRules[Math.Min(index, EventRules.Count - 1)];
        ApplyEventRuleChanges($"Deleted event rule: {deletedName}");
        return true;
    }

    public void ApplyEventRuleChangesFromUi(string status)
    {
        ApplyEventRuleChanges(status);
    }

    public bool AddHighlightRule(HighlightRule rule)
    {
        if (!TryNormalizeHighlightRule(rule, out var normalized, out var error))
        {
            RecordRuleEditError(error);
            return false;
        }

        HighlightRules.Add(normalized);
        SelectedHighlightRule = normalized;
        ApplyHighlightRuleChanges($"Added highlight rule: {normalized.Name}");
        return true;
    }

    public bool ReplaceSelectedHighlightRule(HighlightRule replacement)
    {
        if (SelectedHighlightRule is null)
        {
            RecordRuleEditError("Select a highlight rule before editing.");
            return false;
        }

        if (!TryNormalizeHighlightRule(replacement, out var normalized, out var error))
        {
            RecordRuleEditError(error);
            return false;
        }

        var index = HighlightRules.IndexOf(SelectedHighlightRule);
        if (index < 0)
        {
            RecordRuleEditError("Selected highlight rule was not found.");
            return false;
        }

        HighlightRules[index] = normalized;
        SelectedHighlightRule = normalized;
        ApplyHighlightRuleChanges($"Updated highlight rule: {normalized.Name}");
        return true;
    }

    public bool DeleteSelectedHighlightRule()
    {
        if (SelectedHighlightRule is null)
        {
            RecordRuleEditError("Select a highlight rule before deleting.");
            return false;
        }

        var deletedName = SelectedHighlightRule.Name;
        var index = HighlightRules.IndexOf(SelectedHighlightRule);
        if (index < 0)
        {
            RecordRuleEditError("Selected highlight rule was not found.");
            return false;
        }

        HighlightRules.RemoveAt(index);
        SelectedHighlightRule = HighlightRules.Count == 0
            ? null
            : HighlightRules[Math.Min(index, HighlightRules.Count - 1)];
        ApplyHighlightRuleChanges($"Deleted highlight rule: {deletedName}");
        return true;
    }

    public void ApplyHighlightRuleChangesFromUi(string status)
    {
        ApplyHighlightRuleChanges(status);
    }

    public bool AddSavedCommand(TxCommand command)
    {
        if (!TryNormalizeSavedCommand(command, out var normalized, out var error))
        {
            RecordCommandEditError(error);
            return false;
        }

        Commands.SavedCommands.Add(normalized);
        SelectedSavedCommand = normalized;
        ApplySavedCommandChanges($"Added saved command: {normalized.Name}");
        return true;
    }

    public bool ReplaceSelectedSavedCommand(TxCommand replacement)
    {
        if (SelectedSavedCommand is null)
        {
            RecordCommandEditError("Select a saved command before editing.");
            return false;
        }

        if (!TryNormalizeSavedCommand(replacement, out var normalized, out var error))
        {
            RecordCommandEditError(error);
            return false;
        }

        var index = Commands.SavedCommands.IndexOf(SelectedSavedCommand);
        if (index < 0)
        {
            RecordCommandEditError("Selected saved command was not found.");
            return false;
        }

        Commands.SavedCommands[index] = normalized;
        SelectedSavedCommand = normalized;
        ApplySavedCommandChanges($"Updated saved command: {normalized.Name}");
        return true;
    }

    public bool DeleteSelectedSavedCommand()
    {
        if (SelectedSavedCommand is null)
        {
            RecordCommandEditError("Select a saved command before deleting.");
            return false;
        }

        var deletedName = SelectedSavedCommand.Name;
        var index = Commands.SavedCommands.IndexOf(SelectedSavedCommand);
        if (index < 0)
        {
            RecordCommandEditError("Selected saved command was not found.");
            return false;
        }

        Commands.SavedCommands.RemoveAt(index);
        SelectedSavedCommand = Commands.SavedCommands.Count == 0
            ? null
            : Commands.SavedCommands[Math.Min(index, Commands.SavedCommands.Count - 1)];
        ApplySavedCommandChanges($"Deleted saved command: {deletedName}");
        return true;
    }

    public bool AddCommandSequence(CommandSequence sequence)
    {
        if (!EnsureCanEditCommandSequences("Add sequence"))
        {
            return false;
        }

        if (!TryNormalizeCommandSequence(sequence, out var normalized, out var error))
        {
            RecordSequenceError(error);
            return false;
        }

        CommandSequences.Add(normalized);
        SelectedCommandSequence = normalized;
        SelectedCommandSequenceStep = normalized.Steps.FirstOrDefault();
        ApplyCommandSequenceChanges($"Added sequence: {normalized.Name}");
        return true;
    }

    public bool ReplaceSelectedCommandSequence(CommandSequence replacement)
    {
        if (!EnsureCanEditCommandSequences("Edit sequence"))
        {
            return false;
        }

        if (SelectedCommandSequence is null)
        {
            RecordSequenceError("Select a sequence before editing.");
            return false;
        }

        if (!TryNormalizeCommandSequence(replacement, out var normalized, out var error))
        {
            RecordSequenceError(error);
            return false;
        }

        var index = CommandSequences.IndexOf(SelectedCommandSequence);
        if (index < 0)
        {
            RecordSequenceError("Selected sequence was not found.");
            return false;
        }

        CommandSequences[index] = normalized;
        SelectedCommandSequence = normalized;
        SelectedCommandSequenceStep = normalized.Steps.FirstOrDefault();
        ApplyCommandSequenceChanges($"Updated sequence: {normalized.Name}");
        return true;
    }

    public bool DeleteSelectedCommandSequence()
    {
        if (!EnsureCanEditCommandSequences("Delete sequence"))
        {
            return false;
        }

        if (SelectedCommandSequence is null)
        {
            RecordSequenceError("Select a sequence before deleting.");
            return false;
        }

        var deletedName = SelectedCommandSequence.Name;
        var index = CommandSequences.IndexOf(SelectedCommandSequence);
        if (index < 0)
        {
            RecordSequenceError("Selected sequence was not found.");
            return false;
        }

        CommandSequences.RemoveAt(index);
        SelectedCommandSequence = CommandSequences.Count == 0
            ? null
            : CommandSequences[Math.Min(index, CommandSequences.Count - 1)];
        ApplyCommandSequenceChanges($"Deleted sequence: {deletedName}");
        return true;
    }

    public bool AddCommandSequenceStep(CommandSequenceStep step)
    {
        if (!EnsureCanEditCommandSequences("Add sequence step"))
        {
            return false;
        }

        if (SelectedCommandSequence is null)
        {
            RecordSequenceError("Select a sequence before adding a step.");
            return false;
        }

        if (!TryNormalizeCommandSequenceStep(step, out var normalized, out var error))
        {
            RecordSequenceError(error);
            return false;
        }

        SelectedCommandSequence.Steps.Add(normalized);
        SelectedCommandSequenceStep = normalized;
        ApplyCommandSequenceChanges($"Added sequence step: {normalized.DisplayName}");
        return true;
    }

    public bool ReplaceSelectedCommandSequenceStep(CommandSequenceStep replacement)
    {
        if (!EnsureCanEditCommandSequences("Edit sequence step"))
        {
            return false;
        }

        if (SelectedCommandSequence is null || SelectedCommandSequenceStep is null)
        {
            RecordSequenceError("Select a sequence step before editing.");
            return false;
        }

        if (!TryNormalizeCommandSequenceStep(replacement, out var normalized, out var error))
        {
            RecordSequenceError(error);
            return false;
        }

        var index = SelectedCommandSequence.Steps.IndexOf(SelectedCommandSequenceStep);
        if (index < 0)
        {
            RecordSequenceError("Selected sequence step was not found.");
            return false;
        }

        SelectedCommandSequence.Steps[index] = normalized;
        SelectedCommandSequenceStep = normalized;
        ApplyCommandSequenceChanges($"Updated sequence step: {normalized.DisplayName}");
        return true;
    }

    public bool DeleteSelectedCommandSequenceStep()
    {
        if (!EnsureCanEditCommandSequences("Delete sequence step"))
        {
            return false;
        }

        if (SelectedCommandSequence is null || SelectedCommandSequenceStep is null)
        {
            RecordSequenceError("Select a sequence step before deleting.");
            return false;
        }

        var deletedName = SelectedCommandSequenceStep.DisplayName;
        var index = SelectedCommandSequence.Steps.IndexOf(SelectedCommandSequenceStep);
        if (index < 0)
        {
            RecordSequenceError("Selected sequence step was not found.");
            return false;
        }

        SelectedCommandSequence.Steps.RemoveAt(index);
        SelectedCommandSequenceStep = SelectedCommandSequence.Steps.Count == 0
            ? null
            : SelectedCommandSequence.Steps[Math.Min(index, SelectedCommandSequence.Steps.Count - 1)];
        ApplyCommandSequenceChanges($"Deleted sequence step: {deletedName}");
        return true;
    }

    public bool MoveSelectedCommandSequenceStep(int direction)
    {
        if (!EnsureCanEditCommandSequences("Move sequence step"))
        {
            return false;
        }

        if (SelectedCommandSequence is null || SelectedCommandSequenceStep is null || direction == 0)
        {
            return false;
        }

        var index = SelectedCommandSequence.Steps.IndexOf(SelectedCommandSequenceStep);
        var newIndex = index + direction;
        if (index < 0 || newIndex < 0 || newIndex >= SelectedCommandSequence.Steps.Count)
        {
            return false;
        }

        SelectedCommandSequence.Steps.Move(index, newIndex);
        SelectedCommandSequenceStep = SelectedCommandSequence.Steps[newIndex];
        ApplyCommandSequenceChanges($"Moved sequence step: {SelectedCommandSequenceStep.DisplayName}");
        return true;
    }

    private bool EnsureCanEditCommandSequences(string action)
    {
        if (CanEditCommandSequences)
        {
            return true;
        }

        RecordSequenceError(IsSequenceRunning
            ? $"{action} failed: stop the running sequence before editing sequences."
            : $"{action} failed: wait for the current operation to finish.");
        return false;
    }

    public void RecordEventSelectionError(string message)
    {
        Interlocked.Increment(ref _eventSelectionErrorCount);
        _lastEventSelectionError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(EventSelectionErrorCount));
        OnPropertyChanged(nameof(LastEventSelectionError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    public void RecordEventListScrollError(string message)
    {
        Interlocked.Increment(ref _eventListScrollErrorCount);
        _lastEventListScrollError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(EventListScrollErrorCount));
        OnPropertyChanged(nameof(LastEventListScrollError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    private void RecordListUpdateError(string message)
    {
        Interlocked.Increment(ref _listUpdateErrorCount);
        _lastListUpdateError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(ListUpdateErrorCount));
        OnPropertyChanged(nameof(LastListUpdateError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    public void SetActiveInspectorTab(string? tabName)
    {
        ActiveInspectorTabText = string.IsNullOrWhiteSpace(tabName)
            ? "(unknown)"
            : tabName;
        RefreshDiagnostics(forceDetailed: IsDiagnosticsTabActive);
    }

    public void RecordInspectorTabLayoutError(string message)
    {
        Interlocked.Increment(ref _inspectorTabLayoutErrorCount);
        _lastInspectorTabLayoutError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(InspectorTabLayoutErrorCount));
        OnPropertyChanged(nameof(LastInspectorTabLayoutError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    public void RecordSearchTabLayoutError(string message)
    {
        Interlocked.Increment(ref _searchTabLayoutErrorCount);
        _lastSearchTabLayoutError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(SearchTabLayoutErrorCount));
        OnPropertyChanged(nameof(LastSearchTabLayoutError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    public void RecordContextRenderRefresh()
    {
        Interlocked.Increment(ref _contextRefreshCount);
        _lastContextRefreshError = string.Empty;
        OnPropertyChanged(nameof(ContextRefreshCount));
        OnPropertyChanged(nameof(LastContextRefreshError));
        RefreshDiagnostics();
    }

    public void RecordContextTabActivated()
    {
        Interlocked.Increment(ref _contextTabActivatedCount);
        OnPropertyChanged(nameof(ContextTabActivatedCount));
        RefreshDiagnostics();
    }

    public void RecordContextVisualRefresh(int textLength, string selectedEventId, string selectedEventSummary)
    {
        Interlocked.Increment(ref _contextVisualRefreshCount);
        _lastContextVisualRefreshTextLength = Math.Max(0, textLength);
        _lastContextVisualRefreshEventId = string.IsNullOrWhiteSpace(selectedEventId) ? "(none)" : selectedEventId;
        _lastContextVisualRefreshEventSummary = string.IsNullOrWhiteSpace(selectedEventSummary) ? "(none)" : selectedEventSummary;
        _lastContextVisualRefreshTimeText = DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        _lastContextRenderError = string.Empty;
        OnPropertyChanged(nameof(ContextVisualRefreshCount));
        OnPropertyChanged(nameof(LastContextVisualRefreshTextLength));
        OnPropertyChanged(nameof(LastContextVisualRefreshEventId));
        OnPropertyChanged(nameof(LastContextVisualRefreshEventSummary));
        OnPropertyChanged(nameof(LastContextVisualRefreshTimeText));
        OnPropertyChanged(nameof(LastContextRenderError));
        RefreshDiagnostics();
    }

    public void SetContextWebViewReady(bool isReady)
    {
        IsContextWebViewReady = isReady;
        RefreshDiagnostics();
    }

    public void RecordContextWebViewUpdate(int textLength, string selectedEventSummary)
    {
        Interlocked.Increment(ref _contextWebViewUpdateCount);
        _lastContextWebViewTextLength = Math.Max(0, textLength);
        _lastContextWebViewUpdateEventSummary = string.IsNullOrWhiteSpace(selectedEventSummary) ? "(none)" : selectedEventSummary;
        _lastContextWebViewUpdateTimeText = DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        _lastContextWebViewUpdateError = string.Empty;
        OnPropertyChanged(nameof(ContextWebViewUpdateCount));
        OnPropertyChanged(nameof(LastContextWebViewTextLength));
        OnPropertyChanged(nameof(LastContextWebViewUpdateEventSummary));
        OnPropertyChanged(nameof(LastContextWebViewUpdateTimeText));
        OnPropertyChanged(nameof(LastContextWebViewUpdateError));
        RefreshDiagnostics();
    }

    public void RecordContextWebViewUpdateError(string message)
    {
        Interlocked.Increment(ref _contextWebViewUpdateErrorCount);
        _lastContextWebViewUpdateError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(ContextWebViewUpdateErrorCount));
        OnPropertyChanged(nameof(LastContextWebViewUpdateError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    public void RecordContextRenderRefreshError(string message)
    {
        Interlocked.Increment(ref _contextRefreshErrorCount);
        Interlocked.Increment(ref _contextRenderErrorCount);
        _lastContextRefreshError = message;
        _lastContextRenderError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(ContextRefreshErrorCount));
        OnPropertyChanged(nameof(ContextRenderErrorCount));
        OnPropertyChanged(nameof(LastContextRefreshError));
        OnPropertyChanged(nameof(LastContextRenderError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    public void RecordXtermFitResizeSuccess()
    {
        Interlocked.Increment(ref _xtermFitResizeCount);
        _lastXtermLayoutError = string.Empty;
        OnPropertyChanged(nameof(XtermFitResizeCount));
        OnPropertyChanged(nameof(LastXtermLayoutError));
    }

    public void RecordXtermScrollbackApplied(int size)
    {
        Volatile.Write(ref _lastAppliedXtermScrollbackSize, Math.Max(0, size));
        OnPropertyChanged(nameof(LastAppliedXtermScrollbackSize));
    }

    public void RecordXtermLayoutError(string message)
    {
        Interlocked.Increment(ref _xtermLayoutErrorCount);
        _lastXtermLayoutError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(XtermLayoutErrorCount));
        OnPropertyChanged(nameof(LastXtermLayoutError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    public void RecordAutoScrollAction(string action, bool? atBottom)
    {
        _lastAutoScrollActionTimeText = FormatDiagnosticTime(DateTimeOffset.Now);
        _lastAutoScrollError = string.Empty;
        _lastXtermAtBottom = atBottom;
        OnPropertyChanged(nameof(LastAutoScrollActionTimeText));
        OnPropertyChanged(nameof(LastAutoScrollError));
        OnPropertyChanged(nameof(XtermAtBottomText));
        RefreshDiagnostics();
    }

    public void RecordAutoScrollError(string message)
    {
        _lastAutoScrollActionTimeText = FormatDiagnosticTime(DateTimeOffset.Now);
        _lastAutoScrollError = string.IsNullOrWhiteSpace(message)
            ? "Auto Scroll failed."
            : message.Trim();
        _lastXtermLayoutError = _lastAutoScrollError;
        _lastBackgroundError = _lastAutoScrollError;
        Interlocked.Increment(ref _xtermLayoutErrorCount);
        OnPropertyChanged(nameof(LastAutoScrollActionTimeText));
        OnPropertyChanged(nameof(LastAutoScrollError));
        OnPropertyChanged(nameof(LastXtermLayoutError));
        OnPropertyChanged(nameof(XtermLayoutErrorCount));
        SetStatus(_lastAutoScrollError);
        RefreshDiagnostics();
    }

    private void SetVisibleLogRebuildReason(string reason)
    {
        var normalized = string.IsNullOrWhiteSpace(reason)
            ? "full re-render"
            : reason.Trim();
        if (string.Equals(_lastVisibleLogRebuildReason, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _lastVisibleLogRebuildReason = normalized;
        OnPropertyChanged(nameof(LastVisibleLogRebuildReason));
    }

    private void ApplyEventRuleChanges(string status)
    {
        ClearPendingEventNotifications();
        _bridgeLogProcessor.ResetStream();
        _eventDetector.UpdateRules(EventRules.Select(CloneEventRule).ToArray());
        RecordRuleEditStatus(status);
        NotifyRuleEditorStateChanged();
    }

    private void ApplyHighlightRuleChanges(string status)
    {
        _bridgeLogProcessor.ResetStream();
        Log.SetHighlightRules(HighlightRules.Select(CloneHighlightRule).ToArray());
        RefreshVisibleLogFilterOptions(preserveSelection: true, applyFilter: true);
        RecordAutomaticRuleRerenderSuppressed();
        RecordRuleEditStatus($"{status}. Applies to new logs only.");
        NotifyRuleEditorStateChanged();
    }

    private void ApplyLogRuleChanges(string status)
    {
        ClearPendingEventNotifications();
        _bridgeLogProcessor.ResetStream();
        RebuildProjectedRulesFromLogRules();
        _eventDetector.UpdateRules(EventRules.Select(CloneEventRule).ToArray());
        Log.SetHighlightRules(HighlightRules.Select(CloneHighlightRule).ToArray());
        RecordAutomaticRuleRerenderSuppressed();
        RefreshVisibleLogFilterOptions(preserveSelection: true, applyFilter: true);
        RecordRuleEditStatus(FormatRuleChangeStatus(status));
        NotifyRuleEditorStateChanged();
    }

    private void RebuildProjectedRulesFromLogRules()
    {
        EventRules.Clear();
        HighlightRules.Clear();

        foreach (var rule in LogRules)
        {
            if (rule.UseForEvent && !string.IsNullOrWhiteSpace(rule.Keyword))
            {
                EventRules.Add(CreateEventRuleFromLogRule(rule));
            }

            if (rule.UseForHighlight && !string.IsNullOrWhiteSpace(rule.Keyword))
            {
                HighlightRules.Add(CreateHighlightRuleFromLogRule(rule));
            }
        }

        SelectedEventRule = EventRules.FirstOrDefault();
        SelectedHighlightRule = HighlightRules.FirstOrDefault();
    }

    private void RefreshVisibleLogFilterOptions(bool preserveSelection, bool applyFilter)
    {
        var previousKey = preserveSelection
            ? SelectedViewFilterOption?.Key ?? VisibleLogFilterOption.AllKey
            : VisibleLogFilterOption.AllKey;

        VisibleLogFilterOption selected;
        bool selectionChanged;
        _isRefreshingVisibleLogFilterOptions = true;
        try
        {
            VisibleLogFilterOptions.Clear();
            VisibleLogFilterOptions.Add(VisibleLogFilterOption.All());

            foreach (var rule in LogRules.Where(IsRuleAvailableAsViewFilter))
            {
                var displayName = string.IsNullOrWhiteSpace(rule.Name)
                    ? rule.Keyword.Trim()
                    : rule.Name.Trim();
                VisibleLogFilterOptions.Add(new VisibleLogFilterOption(
                    BuildViewFilterKey(rule),
                    displayName,
                    CreateHighlightRuleFromLogRule(rule)));
            }

            selected = VisibleLogFilterOptions.FirstOrDefault(option => string.Equals(option.Key, previousKey, StringComparison.Ordinal))
                ?? VisibleLogFilterOptions.First();
            selectionChanged = !string.Equals(
                _selectedViewFilterOption?.Key ?? VisibleLogFilterOption.AllKey,
                selected.Key,
                StringComparison.Ordinal);

            if (!ReferenceEquals(_selectedViewFilterOption, selected))
            {
                _selectedViewFilterOption = selected;
                OnPropertyChanged(nameof(SelectedViewFilterOption));
            }
        }
        finally
        {
            _isRefreshingVisibleLogFilterOptions = false;
        }

        OnPropertyChanged(nameof(AvailableViewFilterCount));
        OnPropertyChanged(nameof(CurrentVisibleFilterText));

        if (applyFilter && (!selected.IsAll || selectionChanged))
        {
            ApplySelectedVisibleLogFilter(recordStatus: false);
        }
    }

    private void ApplySelectedVisibleLogFilter(bool recordStatus)
    {
        try
        {
            var option = SelectedViewFilterOption ?? VisibleLogFilterOptions.FirstOrDefault() ?? VisibleLogFilterOption.All();
            RecordAutomaticRuleRerenderSuppressed();
            Log.SetViewFilter(option.Rule, rebuildExisting: false);
            _lastVisibleFilterChangeTimeText = FormatDiagnosticTime(DateTimeOffset.Now);
            _lastVisibleFilterError = string.Empty;
            OnPropertyChanged(nameof(CurrentVisibleFilterText));
            OnPropertyChanged(nameof(LastVisibleFilterChangeTimeText));
            OnPropertyChanged(nameof(LastVisibleFilterError));
            OnPropertyChanged(nameof(AvailableViewFilterCount));
            SetFooter(CreateFooterStatus());
            if (recordStatus)
            {
                SetStatus(option.IsAll
                    ? "Visible log filter: ALL. Applies to new logs only."
                    : $"Visible log filter: {option.DisplayName}. Applies to new logs only. Press Clear for a clean filtered view.");
            }

            RefreshDiagnostics();
        }
        catch (Exception ex)
        {
            RecordVisibleFilterError($"Visible log filter update failed: {ex.Message}");
        }
    }

    private void RecordVisibleFilterError(string message)
    {
        Interlocked.Increment(ref _visibleFilterErrorCount);
        _lastVisibleFilterError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(VisibleFilterErrorCount));
        OnPropertyChanged(nameof(LastVisibleFilterError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    private bool IsRuleAvailableAsViewFilter(LogRule rule)
    {
        return rule.Enabled &&
            rule.Mode == ToLogRuleMode(SelectedRxDisplayMode) &&
            rule.UseAsViewFilter &&
            !string.IsNullOrWhiteSpace(rule.Keyword);
    }

    private static string BuildViewFilterKey(LogRule rule)
    {
        var name = string.IsNullOrWhiteSpace(rule.Name)
            ? rule.Keyword
            : rule.Name;
        return string.Join(
            "|",
            name.Trim().ToUpperInvariant(),
            rule.Keyword.Trim().ToUpperInvariant(),
            rule.Mode,
            rule.MatchDirection,
            rule.CaseSensitive);
    }

    private static bool IsInvalidHexLogRule(LogRule rule)
    {
        return rule.Mode == LogRuleMatchMode.Hex &&
            !LogRuleMatcher.TryParseHexPattern(rule.Keyword, out _, out _);
    }

    private static string GetHexRuleParseError(LogRule rule)
    {
        if (rule.Mode != LogRuleMatchMode.Hex)
        {
            return string.Empty;
        }

        return LogRuleMatcher.TryParseHexPattern(rule.Keyword, out _, out var error)
            ? string.Empty
            : error;
    }

    private static string FormatRuleName(LogRule rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.Name))
        {
            return rule.Name.Trim();
        }

        return string.IsNullOrWhiteSpace(rule.Keyword)
            ? "(unnamed)"
            : rule.Keyword.Trim();
    }

    private void ApplySavedCommandChanges(string status)
    {
        RecordCommandEditStatus(status);
        NotifyCommandEditorStateChanged();
        NotifyCommandStates();
    }

    private void ApplyCommandSequenceChanges(string status)
    {
        RefreshSelectedCommandSequenceStepNumbers();
        RecordSequenceStatus(status);
        NotifyCommandSequenceStateChanged();
        NotifyCommandStates();
    }

    private void RecordAutomaticRuleRerenderSuppressed()
    {
        Interlocked.Increment(ref _automaticRuleRerenderSuppressedCount);
        _lastRuleChangeLiveOnlyTimeText = FormatDiagnosticTime(DateTimeOffset.Now);
        OnPropertyChanged(nameof(AutomaticRuleRerenderSuppressedCount));
        OnPropertyChanged(nameof(LastRuleChangeLiveOnlyTimeText));
    }

    private string FormatRuleChangeStatus(string status)
    {
        Interlocked.Increment(ref _ruleChangesSinceClearCount);
        _lastRuleChangeLiveOnlyTimeText = FormatDiagnosticTime(DateTimeOffset.Now);
        OnPropertyChanged(nameof(RuleChangesSinceClearCount));
        OnPropertyChanged(nameof(LastRuleChangeLiveOnlyTimeText));

        return $"{status}. Applies to new logs only.";
    }

    private void RecordRuleEditStatus(string message)
    {
        _lastRuleEditStatus = message;
        _lastRuleEditError = string.Empty;
        OnPropertyChanged(nameof(LastRuleEditStatus));
        OnPropertyChanged(nameof(LastRuleEditError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    private void RecordRuleEditError(string message)
    {
        Interlocked.Increment(ref _ruleEditErrorCount);
        _lastRuleEditError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(RuleEditErrorCount));
        OnPropertyChanged(nameof(LastRuleEditError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    public void RecordRuleColorChangeError(string message)
    {
        var safeMessage = string.IsNullOrWhiteSpace(message)
            ? "Rule color change failed."
            : message.Trim();

        Interlocked.Increment(ref _ruleColorChangeErrorCount);
        _lastRuleColorChangeError = safeMessage;
        _lastBackgroundError = safeMessage;
        OnPropertyChanged(nameof(RuleColorChangeErrorCount));
        OnPropertyChanged(nameof(LastRuleColorChangeError));
        SetStatus(safeMessage);
        RefreshDiagnostics();
    }

    private void RecordCommandEditStatus(string message)
    {
        _lastCommandEditStatus = message;
        _lastCommandEditError = string.Empty;
        OnPropertyChanged(nameof(LastCommandEditStatus));
        OnPropertyChanged(nameof(LastCommandEditError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    private void RecordCommandEditError(string message)
    {
        Interlocked.Increment(ref _commandEditErrorCount);
        _lastCommandEditError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(CommandEditErrorCount));
        OnPropertyChanged(nameof(LastCommandEditError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    private void RecordSequenceStatus(string message)
    {
        _lastSequenceActionStatus = message;
        _lastSequenceError = string.Empty;
        OnPropertyChanged(nameof(LastSequenceActionStatus));
        OnPropertyChanged(nameof(LastSequenceError));
        OnPropertyChanged(nameof(SequenceStatusText));
        SetStatus(message);
        RefreshDiagnostics();
    }

    private void RecordSequenceError(string message)
    {
        Interlocked.Increment(ref _sequenceErrorCount);
        _lastSequenceError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(SequenceErrorCount));
        OnPropertyChanged(nameof(LastSequenceError));
        OnPropertyChanged(nameof(SequenceStatusText));
        SetStatus(message);
        RefreshDiagnostics();
    }

    private void NotifyRuleEditorStateChanged()
    {
        OnPropertyChanged(nameof(LogRuleEditorCount));
        OnPropertyChanged(nameof(EventRuleEditorCount));
        OnPropertyChanged(nameof(HighlightRuleEditorCount));
        OnPropertyChanged(nameof(ActiveEventLogRuleCount));
        OnPropertyChanged(nameof(ActiveHighlightRuleCount));
        OnPropertyChanged(nameof(ActiveViewFilterRuleCount));
        OnPropertyChanged(nameof(TerminalLogRuleCount));
        OnPropertyChanged(nameof(HexLogRuleCount));
        OnPropertyChanged(nameof(InvalidHexLogRuleCount));
        OnPropertyChanged(nameof(LastInvalidHexLogRuleName));
        OnPropertyChanged(nameof(LastInvalidHexLogRuleError));
        OnPropertyChanged(nameof(RuleMigrationResult));
        OnPropertyChanged(nameof(UnifiedLogRuleColorCount));
        OnPropertyChanged(nameof(InvalidRuleColorFallbackCount));
        OnPropertyChanged(nameof(AutomaticRuleRerenderSuppressedCount));
        OnPropertyChanged(nameof(LastRuleChangeLiveOnlyTimeText));
        OnPropertyChanged(nameof(RuleChangesSinceClearCount));
        OnPropertyChanged(nameof(HasSelectedLogRule));
        OnPropertyChanged(nameof(HasSelectedEventRule));
        OnPropertyChanged(nameof(HasSelectedHighlightRule));
        SetFooter(CreateFooterStatus());
        RefreshDiagnostics();
    }

    private void NotifyCommandEditorStateChanged()
    {
        OnPropertyChanged(nameof(SavedCommandEditorCount));
        OnPropertyChanged(nameof(HasSelectedSavedCommand));
        RefreshDiagnostics();
    }

    private void NotifyCommandSequenceStateChanged()
    {
        OnPropertyChanged(nameof(CommandSequenceCount));
        OnPropertyChanged(nameof(HasSelectedCommandSequence));
        OnPropertyChanged(nameof(HasSelectedCommandSequenceStep));
        OnPropertyChanged(nameof(SelectedCommandSequenceStepCount));
        OnPropertyChanged(nameof(SelectedCommandSequenceStepCountText));
        OnPropertyChanged(nameof(SelectedCommandSequenceName));
        OnPropertyChanged(nameof(SequenceStatusText));
        OnPropertyChanged(nameof(SequenceRuntimeStateText));
        OnPropertyChanged(nameof(SequenceCurrentStepDisplayText));
        OnPropertyChanged(nameof(SequenceCompletedStepsText));
        OnPropertyChanged(nameof(LastSequenceActionStatus));
        RunCommandSequenceCommand.NotifyCanExecuteChanged();
        StopCommandSequenceCommand.NotifyCanExecuteChanged();
        RefreshDiagnostics();
    }

    private void RefreshSelectedCommandSequenceStepNumbers()
    {
        if (SelectedCommandSequence is null)
        {
            return;
        }

        for (var index = 0; index < SelectedCommandSequence.Steps.Count; index++)
        {
            SelectedCommandSequence.Steps[index].StepNumber = index + 1;
        }
    }

    private bool TryNormalizeLogRule(LogRule rule, out LogRule normalized, out string error)
    {
        normalized = new LogRule();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rule.Keyword))
        {
            error = "Log rule keyword is required.";
            return false;
        }

        normalized = CloneLogRule(rule);
        normalized.Keyword = normalized.Keyword.Trim();
        normalized.Name = string.IsNullOrWhiteSpace(normalized.Name)
            ? normalized.Keyword
            : normalized.Name.Trim();
        normalized.ForegroundColor = NormalizeHighlightColorName(normalized.ForegroundColor, out var foregroundFallback);
        normalized.BackgroundColor = NormalizeOptionalHighlightColorName(normalized.BackgroundColor, out var backgroundFallback);
        if (!Enum.IsDefined(normalized.Mode))
        {
            normalized.Mode = LogRuleMatchMode.Terminal;
        }

        if (!Enum.IsDefined(normalized.MatchDirection))
        {
            normalized.MatchDirection = HighlightMatchDirection.Both;
        }

        normalized.NotificationCooldownSeconds = Math.Clamp(normalized.NotificationCooldownSeconds, 5, 3_600);

        if (foregroundFallback || backgroundFallback)
        {
            Interlocked.Increment(ref _invalidRuleColorFallbackCount);
            OnPropertyChanged(nameof(InvalidRuleColorFallbackCount));
        }

        return true;
    }

    private string NormalizeHighlightColorName(string? colorName, out bool usedFallback)
    {
        usedFallback = false;
        if (string.IsNullOrWhiteSpace(colorName))
        {
            return "Default";
        }

        var trimmed = colorName.Trim();
        if (trimmed.Equals("None", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("(none)", StringComparison.OrdinalIgnoreCase))
        {
            return "Default";
        }

        if (trimmed.Equals("Grey", StringComparison.OrdinalIgnoreCase))
        {
            return "Gray";
        }

        foreach (var preset in HighlightColorPresets)
        {
            if (trimmed.Equals(preset, StringComparison.OrdinalIgnoreCase))
            {
                return preset;
            }
        }

        usedFallback = true;
        return "Default";
    }

    private string? NormalizeOptionalHighlightColorName(string? colorName, out bool usedFallback)
    {
        usedFallback = false;
        if (string.IsNullOrWhiteSpace(colorName) ||
            colorName.Trim().Equals("None", StringComparison.OrdinalIgnoreCase) ||
            colorName.Trim().Equals("(none)", StringComparison.OrdinalIgnoreCase) ||
            colorName.Trim().Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = NormalizeHighlightColorName(colorName, out usedFallback);
        return normalized.Equals("Default", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    private static string NormalizeRuleColorForCount(string? colorName)
    {
        if (string.IsNullOrWhiteSpace(colorName))
        {
            return "Default";
        }

        var trimmed = colorName.Trim();
        return trimmed.Equals("Grey", StringComparison.OrdinalIgnoreCase)
            ? "Gray"
            : trimmed;
    }

    private static bool TryNormalizeEventRule(EventRule rule, out EventRule normalized, out string error)
    {
        normalized = new EventRule();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rule.Keyword))
        {
            error = "Event rule keyword is required.";
            return false;
        }

        normalized = CloneEventRule(rule);
        normalized.Keyword = normalized.Keyword.Trim();
        normalized.Name = string.IsNullOrWhiteSpace(normalized.Name)
            ? normalized.Keyword
            : normalized.Name.Trim();
        if (!Enum.IsDefined(normalized.Mode))
        {
            normalized.Mode = LogRuleMatchMode.Terminal;
        }

        if (!Enum.IsDefined(normalized.MatchDirection))
        {
            normalized.MatchDirection = EventMatchDirection.RxOnly;
        }

        normalized.NotificationCooldownSeconds = Math.Clamp(normalized.NotificationCooldownSeconds, 5, 3_600);

        return true;
    }

    private static bool TryNormalizeHighlightRule(HighlightRule rule, out HighlightRule normalized, out string error)
    {
        normalized = new HighlightRule();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rule.Keyword))
        {
            error = "Highlight rule keyword is required.";
            return false;
        }

        normalized = CloneHighlightRule(rule);
        normalized.Keyword = normalized.Keyword.Trim();
        normalized.Name = string.IsNullOrWhiteSpace(normalized.Name)
            ? normalized.Keyword
            : normalized.Name.Trim();
        normalized.ForegroundColor = string.IsNullOrWhiteSpace(normalized.ForegroundColor)
            ? "Default"
            : normalized.ForegroundColor.Trim();
        normalized.BackgroundColor = string.IsNullOrWhiteSpace(normalized.BackgroundColor)
            ? null
            : normalized.BackgroundColor.Trim();
        if (!Enum.IsDefined(normalized.Mode))
        {
            normalized.Mode = LogRuleMatchMode.Terminal;
        }

        if (!Enum.IsDefined(normalized.MatchDirection))
        {
            normalized.MatchDirection = HighlightMatchDirection.Both;
        }

        return true;
    }

    private static bool TryNormalizeSavedCommand(TxCommand command, out TxCommand normalized, out string error)
    {
        normalized = new TxCommand();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(command.Name))
        {
            error = "Saved command name is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(command.CommandText))
        {
            error = "Saved command text is required.";
            return false;
        }

        normalized = CloneTxCommand(command);
        normalized.CommandText = normalized.CommandText.Trim();
        normalized.Name = normalized.Name.Trim();
        if (!TryNormalizeSavedCommandShortcut(normalized.OptionalShortcut, out var normalizedShortcut, out var shortcutError))
        {
            error = shortcutError;
            return false;
        }

        normalized.OptionalShortcut = normalizedShortcut;
        return true;
    }

    private static bool TryNormalizeCommandSequence(CommandSequence sequence, out CommandSequence normalized, out string error)
    {
        normalized = new CommandSequence();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(sequence.Name))
        {
            error = "Sequence name is required.";
            return false;
        }

        normalized = CloneCommandSequence(sequence);
        normalized.Name = normalized.Name.Trim();
        normalized.Steps.Clear();
        for (var index = 0; index < sequence.Steps.Count; index++)
        {
            if (!TryNormalizeCommandSequenceStep(
                    sequence.Steps[index],
                    out var normalizedStep,
                    out error))
            {
                error = $"Sequence step {index + 1}: {error}";
                return false;
            }

            normalized.Steps.Add(normalizedStep);
        }

        return true;
    }

    private static bool TryNormalizeCommandSequenceStep(
        CommandSequenceStep step,
        out CommandSequenceStep normalized,
        out string error)
    {
        normalized = new CommandSequenceStep();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(step.CommandText))
        {
            error = "Sequence step command text is required.";
            return false;
        }

        if (step.DelayAfterMs is < 0 or > 600_000)
        {
            error = "Sequence step delay must be between 0 and 600,000 ms.";
            return false;
        }

        if (step.LineEndingMode.HasValue && !Enum.IsDefined(step.LineEndingMode.Value))
        {
            error = "Sequence step line ending is invalid.";
            return false;
        }

        normalized = CloneCommandSequenceStep(step);
        normalized.CommandText = normalized.CommandText.Trim();
        normalized.Name = string.IsNullOrWhiteSpace(normalized.Name) ? null : normalized.Name.Trim();
        normalized.Comment = string.IsNullOrWhiteSpace(normalized.Comment) ? null : normalized.Comment.Trim();
        return true;
    }

    private static bool TryValidateHexSequenceSteps(
        IEnumerable<CommandSequenceStep> steps,
        out string error)
    {
        var index = 0;
        foreach (var step in steps)
        {
            index++;
            if (!HexPayloadParser.TryParse(step.CommandText, out _, out var parseError))
            {
                error = $"step {index} ({step.DisplayName}): {parseError}";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    public static bool TryNormalizeSavedCommandShortcut(string? shortcutText, out string? normalizedShortcut, out string error)
    {
        normalizedShortcut = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(shortcutText))
        {
            return true;
        }

        var parts = shortcutText
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            error = "Shortcut must use Ctrl+digit or Alt+letter, for example Ctrl+1 or Alt+S.";
            return false;
        }

        var modifier = parts[0];
        var key = parts[1];
        if (key.Length != 1 || !char.IsLetterOrDigit(key[0]))
        {
            error = "Shortcut key must be a single letter or digit.";
            return false;
        }

        if (string.Equals(modifier, "Ctrl", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modifier, "Control", StringComparison.OrdinalIgnoreCase))
        {
            if (!char.IsDigit(key[0]))
            {
                error = "Ctrl shortcuts currently support digits only. Use Alt+letter for letter shortcuts.";
                return false;
            }

            normalizedShortcut = $"Ctrl+{char.ToUpperInvariant(key[0])}";
            return true;
        }

        if (string.Equals(modifier, "Alt", StringComparison.OrdinalIgnoreCase))
        {
            normalizedShortcut = $"Alt+{char.ToUpperInvariant(key[0])}";
            return true;
        }

        error = "Shortcut modifier must be Ctrl or Alt.";
        return false;
    }

    public void RecordXtermAppendSuccess(int appendedLineCount)
    {
        RecordXtermAppendSuccess(appendedLineCount, 0);
    }

    public void RecordXtermAppendSuccess(int appendedLineCount, int appendedCharacterCount)
    {
        if (appendedLineCount <= 0 && appendedCharacterCount <= 0)
        {
            return;
        }

        Interlocked.Add(ref _xtermAppendedLineCount, Math.Max(0, appendedLineCount));
        Interlocked.Increment(ref _xtermAppendBatchCount);
        Volatile.Write(ref _lastXtermAppendLineCount, Math.Max(0, appendedLineCount));
        Volatile.Write(ref _lastXtermAppendCharacterCount, Math.Max(0, appendedCharacterCount));
        UpdateMax(ref _maxXtermAppendLineCount, appendedLineCount);
        UpdateMax(ref _maxXtermAppendCharacterCount, appendedCharacterCount);
    }

    public void RecordXtermAppendDuration(TimeSpan duration)
    {
        var durationMs = Math.Max(0, (long)Math.Round(duration.TotalMilliseconds));
        Interlocked.Exchange(ref _lastXtermAppendDurationMs, durationMs);
        UpdateMax(ref _maxXtermAppendDurationMs, durationMs);
    }

    public void RecordXtermAppendQueued(int characterCount)
    {
        if (characterCount <= 0)
        {
            return;
        }

        var pending = Interlocked.Add(ref _xtermPendingCharacterCount, characterCount);
        UpdateMax(ref _maxXtermPendingCharacterCount, pending);
    }

    public void RecordXtermAppendDequeued(int characterCount)
    {
        if (characterCount <= 0)
        {
            return;
        }

        var pending = Interlocked.Add(ref _xtermPendingCharacterCount, -characterCount);
        if (pending < 0)
        {
            Interlocked.Exchange(ref _xtermPendingCharacterCount, 0);
        }

    }

    public void RecordWindowMinimizeState(bool isMinimized)
    {
        if (_isWindowMinimized == isMinimized)
        {
            return;
        }

        _isWindowMinimized = isMinimized;
        if (isMinimized)
        {
            _lastWindowMinimizeTimeText = FormatDiagnosticTime(DateTimeOffset.Now);
            SetStatus("Window minimized; xterm visual rendering suspended.");
        }
        else
        {
            _lastWindowRestoreTimeText = FormatDiagnosticTime(DateTimeOffset.Now);
            SetStatus("Window restored; xterm visual append resume queued.");
        }

        OnPropertyChanged(nameof(IsWindowMinimized));
        OnPropertyChanged(nameof(LastWindowMinimizeTimeText));
        OnPropertyChanged(nameof(LastWindowRestoreTimeText));
        RefreshDiagnostics();
    }

    public void SetVisualAppendSuspendedForMinimize(bool suspended)
    {
        if (_isVisualAppendSuspendedForMinimize == suspended)
        {
            return;
        }

        _isVisualAppendSuspendedForMinimize = suspended;
        OnPropertyChanged(nameof(IsVisualAppendSuspendedForMinimize));
        OnPropertyChanged(nameof(RenderingStateText));
        RefreshDiagnostics();
    }

    public void SetXtermNeedsFullRerenderAfterRestore(bool needed)
    {
        if (_xtermNeedsFullRerenderAfterRestore == needed)
        {
            return;
        }

        _xtermNeedsFullRerenderAfterRestore = needed;
        OnPropertyChanged(nameof(XtermNeedsFullRerenderAfterRestore));
        RefreshDiagnostics();
    }

    public void RecordMinimizedVisualAppendCoalesced(int lineCount, int characterCount)
    {
        if (lineCount > 0)
        {
            var totalLines = Interlocked.Add(ref _minimizedVisualCoalescedLineCount, lineCount);
            UpdateMax(ref _maxMinimizedVisualCoalescedLineCount, totalLines);
        }

        if (characterCount > 0)
        {
            var totalChars = Interlocked.Add(ref _minimizedVisualCoalescedCharacterCount, characterCount);
            UpdateMax(ref _maxMinimizedVisualCoalescedCharacterCount, totalChars);
        }
    }

    public void RecordSuspendedXtermQueueState(int lineCount, long characterCount)
    {
        Volatile.Write(ref _suspendedXtermPendingLineCount, Math.Max(0, lineCount));
        Interlocked.Exchange(ref _suspendedXtermPendingCharacterCount, Math.Max(0, characterCount));
    }

    public void RecordSuspendedXtermQueueCollapsed(string reason)
    {
        Interlocked.Increment(ref _suspendedXtermQueueCollapseCount);
        _lastSuspendedXtermQueueCollapseReason = string.IsNullOrWhiteSpace(reason)
            ? "suspended xterm queue exceeded its bound"
            : reason.Trim();
    }

    public void RecordRestoreRenderStarted()
    {
        RecordRestoreRenderStarted(
            "full re-render",
            pendingLineCount: Log.CurrentVisibleLineCount,
            lastRenderedSequenceId: LastRenderedSequenceId,
            pendingDeltaLineCount: Math.Max(0, Log.DisplayedLineCount - LastRenderedSequenceId));
    }

    public void RecordRestoreRenderStarted(
        string mode,
        int pendingLineCount,
        long lastRenderedSequenceId,
        long pendingDeltaLineCount)
    {
        _restoreRenderStartedTimeText = FormatDiagnosticTime(DateTimeOffset.Now);
        _restoreRenderCompletedTimeText = "(pending)";
        _restoreRenderDurationText = "(pending)";
        Volatile.Write(ref _restoreRenderedLineCount, 0);
        _lastRestoreRenderMode = string.IsNullOrWhiteSpace(mode) ? "delta append" : mode.Trim();
        Interlocked.Exchange(ref _lastRenderedSequenceId, Math.Max(0, lastRenderedSequenceId));
        Interlocked.Exchange(ref _pendingVisualDeltaLineCount, Math.Max(0, pendingDeltaLineCount));
        OnPropertyChanged(nameof(RestoreRenderStartedTimeText));
        OnPropertyChanged(nameof(RestoreRenderCompletedTimeText));
        OnPropertyChanged(nameof(RestoreRenderDurationText));
        OnPropertyChanged(nameof(RestoreRenderedLineCount));
        OnPropertyChanged(nameof(LastRestoreRenderMode));
        OnPropertyChanged(nameof(LastRenderedSequenceId));
        OnPropertyChanged(nameof(PendingVisualDeltaLineCount));
        SetStatus(_lastRestoreRenderMode.Contains("delta", StringComparison.OrdinalIgnoreCase)
            ? $"Catching up xterm visual logs: {pendingLineCount:N0} lines."
            : "Redrawing xterm from retained visible buffer.");
        RefreshDiagnostics();
    }

    public void RecordFullXtermRerenderRequested(string reason)
    {
        Interlocked.Increment(ref _fullXtermRerenderRequestCount);
        OnPropertyChanged(nameof(FullXtermRerenderRequestCount));
        RefreshDiagnostics();
    }

    public void RecordFullXtermRerenderCoalesced(string reason)
    {
        Interlocked.Increment(ref _fullXtermRerenderCoalescedCount);
        OnPropertyChanged(nameof(FullXtermRerenderCoalescedCount));
        RefreshDiagnostics();
    }

    public void RecordFullXtermRerenderCanceled(string reason, string error)
    {
        Interlocked.Increment(ref _fullXtermRerenderCanceledCount);
        _lastFullXtermRerenderReason = string.IsNullOrWhiteSpace(reason) ? "full re-render" : reason.Trim();
        _lastFullXtermRerenderError = string.IsNullOrWhiteSpace(error) ? "canceled" : error.Trim();
        OnPropertyChanged(nameof(FullXtermRerenderCanceledCount));
        OnPropertyChanged(nameof(LastFullXtermRerenderReason));
        OnPropertyChanged(nameof(LastFullXtermRerenderError));
        RefreshDiagnostics();
    }

    public void RecordFullXtermRerenderStarted(string reason, long generation)
    {
        _isFullXtermRerenderInProgress = true;
        _lastFullXtermRerenderReason = string.IsNullOrWhiteSpace(reason) ? "full re-render" : reason.Trim();
        _lastFullXtermFinalScrollAction = "(pending)";
        _lastFullXtermScrollRestoreAttempted = false;
        _lastFullXtermRerenderError = string.Empty;
        Interlocked.Exchange(ref _lastFullXtermRerenderGeneration, generation);
        Volatile.Write(ref _lastFullXtermClearCount, 0);
        Volatile.Write(ref _lastFullXtermVisibilityToggleCount, 0);
        OnPropertyChanged(nameof(IsFullXtermRerenderInProgress));
        OnPropertyChanged(nameof(LastFullXtermRerenderReason));
        OnPropertyChanged(nameof(LastFullXtermFinalScrollAction));
        OnPropertyChanged(nameof(LastFullXtermScrollRestoreAttempted));
        OnPropertyChanged(nameof(LastFullXtermRerenderError));
        OnPropertyChanged(nameof(LastFullXtermRerenderGeneration));
        OnPropertyChanged(nameof(LastFullXtermClearCount));
        OnPropertyChanged(nameof(LastFullXtermVisibilityToggleCount));
        RefreshDiagnostics();
    }

    public void RecordFullXtermRerenderCompleted(
        int renderedLineCount,
        TimeSpan duration,
        bool scrollRestoreAttempted,
        string finalScrollAction,
        int suppressedIntermediateAutoScrollCount,
        long generation,
        int clearCount,
        int visibilityToggleCount)
    {
        _isFullXtermRerenderInProgress = false;
        Volatile.Write(ref _lastFullXtermRerenderLineCount, Math.Max(0, renderedLineCount));
        _lastFullXtermRerenderDurationText = $"{duration.TotalMilliseconds:0} ms";
        _lastFullXtermScrollRestoreAttempted = scrollRestoreAttempted;
        _lastFullXtermFinalScrollAction = string.IsNullOrWhiteSpace(finalScrollAction)
            ? "unchanged"
            : finalScrollAction.Trim();
        _lastFullXtermRerenderError = string.Empty;
        Interlocked.Exchange(ref _lastFullXtermRerenderGeneration, generation);
        Volatile.Write(ref _lastFullXtermClearCount, Math.Max(0, clearCount));
        Volatile.Write(ref _lastFullXtermVisibilityToggleCount, Math.Max(0, visibilityToggleCount));
        if (suppressedIntermediateAutoScrollCount > 0)
        {
            Interlocked.Add(ref _suppressedIntermediateAutoScrollCount, suppressedIntermediateAutoScrollCount);
        }

        OnPropertyChanged(nameof(IsFullXtermRerenderInProgress));
        OnPropertyChanged(nameof(LastFullXtermRerenderLineCount));
        OnPropertyChanged(nameof(LastFullXtermRerenderDurationText));
        OnPropertyChanged(nameof(LastFullXtermScrollRestoreAttempted));
        OnPropertyChanged(nameof(LastFullXtermFinalScrollAction));
        OnPropertyChanged(nameof(SuppressedIntermediateAutoScrollCount));
        OnPropertyChanged(nameof(LastFullXtermRerenderError));
        OnPropertyChanged(nameof(LastFullXtermRerenderGeneration));
        OnPropertyChanged(nameof(LastFullXtermClearCount));
        OnPropertyChanged(nameof(LastFullXtermVisibilityToggleCount));
        RefreshDiagnostics();
    }

    public void RecordFullXtermRerenderEndedAfterError(
        string finalScrollAction,
        string error,
        long generation,
        bool canceled = false)
    {
        _isFullXtermRerenderInProgress = false;
        _lastFullXtermFinalScrollAction = string.IsNullOrWhiteSpace(finalScrollAction)
            ? "error"
            : finalScrollAction.Trim();
        _lastFullXtermRerenderError = string.IsNullOrWhiteSpace(error) ? "error" : error.Trim();
        if (generation > 0)
        {
            Interlocked.Exchange(ref _lastFullXtermRerenderGeneration, generation);
        }

        if (canceled)
        {
            Interlocked.Increment(ref _fullXtermRerenderCanceledCount);
        }

        OnPropertyChanged(nameof(IsFullXtermRerenderInProgress));
        OnPropertyChanged(nameof(LastFullXtermFinalScrollAction));
        OnPropertyChanged(nameof(LastFullXtermRerenderError));
        OnPropertyChanged(nameof(LastFullXtermRerenderGeneration));
        OnPropertyChanged(nameof(FullXtermRerenderCanceledCount));
        RefreshDiagnostics();
    }

    public void RecordRestoreRenderCompleted(int renderedLineCount, TimeSpan duration)
    {
        RecordRestoreRenderCompleted(renderedLineCount, duration, _lastRestoreRenderMode);
    }

    public void RecordRestoreRenderCompleted(int renderedLineCount, TimeSpan duration, string mode)
    {
        _restoreRenderCompletedTimeText = FormatDiagnosticTime(DateTimeOffset.Now);
        _restoreRenderDurationText = $"{duration.TotalMilliseconds:0} ms";
        _lastRestoreRenderMode = string.IsNullOrWhiteSpace(mode) ? _lastRestoreRenderMode : mode.Trim();
        Volatile.Write(ref _restoreRenderedLineCount, Math.Max(0, renderedLineCount));
        Interlocked.Exchange(ref _pendingVisualDeltaLineCount, Math.Max(0, Log.DisplayedLineCount - LastRenderedSequenceId));
        Interlocked.Exchange(ref _minimizedVisualCoalescedLineCount, 0);
        Interlocked.Exchange(ref _minimizedVisualCoalescedCharacterCount, 0);
        OnPropertyChanged(nameof(RestoreRenderCompletedTimeText));
        OnPropertyChanged(nameof(RestoreRenderDurationText));
        OnPropertyChanged(nameof(RestoreRenderedLineCount));
        OnPropertyChanged(nameof(LastRestoreRenderMode));
        OnPropertyChanged(nameof(PendingVisualDeltaLineCount));
        OnPropertyChanged(nameof(MinimizedVisualCoalescedLineCount));
        OnPropertyChanged(nameof(MinimizedVisualCoalescedCharacterCount));
        SetStatus(_lastRestoreRenderMode.Contains("delta", StringComparison.OrdinalIgnoreCase)
            ? $"xterm visual catch-up complete: {renderedLineCount:N0} lines."
            : $"xterm redraw complete: {renderedLineCount:N0} lines.");
        RefreshDiagnostics();
    }

    public void RecordRestoreFullRerenderSuppressed(string reason)
    {
        Interlocked.Increment(ref _restoreFullRerenderSuppressedCount);
        _lastRestoreRenderMode = "delta append";
        OnPropertyChanged(nameof(RestoreFullRerenderSuppressedCount));
        OnPropertyChanged(nameof(LastRestoreRenderMode));
        RefreshDiagnostics();
    }

    public void RecordWindowActivationRerenderSuppressed()
    {
        Interlocked.Increment(ref _windowActivationRerenderSuppressedCount);
        OnPropertyChanged(nameof(WindowActivationRerenderSuppressedCount));
    }

    public void RecordRenderedSequenceState(long lastRenderedSequenceId, long pendingDeltaLineCount)
    {
        Interlocked.Exchange(ref _lastRenderedSequenceId, Math.Max(0, lastRenderedSequenceId));
        Interlocked.Exchange(ref _pendingVisualDeltaLineCount, Math.Max(0, pendingDeltaLineCount));
    }

    private static void UpdateMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }

    private static void UpdateMax(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }

    public void RecordXtermAppendError(string message)
    {
        Interlocked.Increment(ref _xtermAppendErrorCount);
        _lastXtermAppendError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(XtermAppendErrorCount));
        OnPropertyChanged(nameof(LastXtermAppendError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    public void RecordXtermCopySuccess(int copiedCharacterCount)
    {
        Interlocked.Increment(ref _xtermCopyRequestCount);
        Interlocked.Add(ref _xtermCopiedCharacterCount, Math.Max(0, copiedCharacterCount));
        _lastXtermCopyError = string.Empty;
        OnPropertyChanged(nameof(XtermCopyRequestCount));
        OnPropertyChanged(nameof(XtermCopiedCharacterCount));
        OnPropertyChanged(nameof(LastXtermCopyError));
        RefreshDiagnostics();
    }

    public void RecordXtermCopyError(string message)
    {
        Interlocked.Increment(ref _xtermCopyRequestCount);
        Interlocked.Increment(ref _xtermCopyErrorCount);
        _lastXtermCopyError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(XtermCopyRequestCount));
        OnPropertyChanged(nameof(XtermCopyErrorCount));
        OnPropertyChanged(nameof(LastXtermCopyError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    public void RecordXtermSearchRequested()
    {
        Interlocked.Increment(ref _xtermSearchRequestCount);
        OnPropertyChanged(nameof(XtermSearchRequestCount));
        RefreshDiagnostics();
    }

    public void RecordXtermSearchResult(bool found)
    {
        if (found)
        {
            Interlocked.Increment(ref _xtermSearchHitCount);
        }

        _lastXtermSearchError = string.Empty;
        _lastSearchError = string.Empty;
        OnPropertyChanged(nameof(XtermSearchHitCount));
        OnPropertyChanged(nameof(LastXtermSearchError));
        OnPropertyChanged(nameof(LastSearchError));
        RefreshDiagnostics();
    }

    public void RecordXtermSearchError(string message)
    {
        Interlocked.Increment(ref _xtermSearchErrorCount);
        Interlocked.Increment(ref _searchErrorCount);
        _lastXtermSearchError = message;
        _lastSearchError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(XtermSearchErrorCount));
        OnPropertyChanged(nameof(LastXtermSearchError));
        OnPropertyChanged(nameof(SearchErrorCount));
        OnPropertyChanged(nameof(LastSearchError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    private void RecordSearchShortcutAction(string action, string source)
    {
        _lastSearchShortcutAction = string.IsNullOrWhiteSpace(action)
            ? "Search shortcut"
            : action.Trim();
        _lastSearchShortcutSource = string.IsNullOrWhiteSpace(source)
            ? "(unknown)"
            : source.Trim();
        _lastSearchShortcutTime = DateTimeOffset.Now;
        _lastSearchShortcutError = string.Empty;
        OnPropertyChanged(nameof(LastSearchShortcutAction));
        OnPropertyChanged(nameof(LastSearchShortcutSource));
        OnPropertyChanged(nameof(LastSearchShortcutTimeText));
        OnPropertyChanged(nameof(LastSearchShortcutError));
        RefreshDiagnostics();
    }

    private void RecordSearchShortcutError(string message, string source)
    {
        Interlocked.Increment(ref _searchShortcutErrorCount);
        _lastSearchShortcutAction = "Search shortcut error";
        _lastSearchShortcutSource = string.IsNullOrWhiteSpace(source)
            ? "(unknown)"
            : source.Trim();
        _lastSearchShortcutTime = DateTimeOffset.Now;
        _lastSearchShortcutError = string.IsNullOrWhiteSpace(message)
            ? "Search shortcut failed."
            : message.Trim();
        _lastBackgroundError = _lastSearchShortcutError;
        OnPropertyChanged(nameof(LastSearchShortcutAction));
        OnPropertyChanged(nameof(LastSearchShortcutSource));
        OnPropertyChanged(nameof(LastSearchShortcutTimeText));
        OnPropertyChanged(nameof(SearchShortcutErrorCount));
        OnPropertyChanged(nameof(LastSearchShortcutError));
        SetStatus(_lastSearchShortcutError);
        RefreshDiagnostics();
    }

    public void SetXtermReady(bool isReady)
    {
        IsXtermReady = isReady;
        RefreshDiagnostics();
    }

    private void RecordTxSuccess(
        string commandText,
        TxSendMode mode,
        string rawInput,
        int byteCount,
        string hexParseError)
    {
        Interlocked.Increment(ref _sentCommandCount);
        _lastSentCommandText = commandText;
        RecordTxAttemptDiagnostics(mode, rawInput, byteCount, hexParseError);
        _lastSentCommandTime = DateTimeOffset.Now;
        _lastTxError = string.Empty;
        OnPropertyChanged(nameof(SentCommandCount));
        OnPropertyChanged(nameof(LastSentCommandText));
        OnPropertyChanged(nameof(LastSentCommandTimeText));
        OnPropertyChanged(nameof(LastTxError));
        SetStatus(mode == TxSendMode.Hex
            ? $"Sent HEX TX: {byteCount:N0} bytes"
            : $"Sent TX command: {commandText}");
        RefreshDiagnostics();
    }

    private void RecordTxAttemptDiagnostics(
        TxSendMode mode,
        string rawInput,
        int byteCount,
        string hexParseError)
    {
        _lastTxMode = mode;
        _lastTxRawInput = rawInput;
        _lastTxByteCount = Math.Max(0, byteCount);
        _lastTxHexParseError = hexParseError;
        OnPropertyChanged(nameof(LastTxMode));
        OnPropertyChanged(nameof(LastTxRawInput));
        OnPropertyChanged(nameof(LastTxByteCount));
        OnPropertyChanged(nameof(LastTxHexParseError));
    }

    private void RecordTxError(string message)
    {
        Interlocked.Increment(ref _txErrorCount);
        _lastTxError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(TxErrorCount));
        OnPropertyChanged(nameof(LastTxError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    private void RecordMarkerSuccess(string markerText, string action, DateTimeOffset insertedAt)
    {
        Interlocked.Increment(ref _markerCount);
        _lastMarkerText = markerText;
        _lastMarkerAction = string.IsNullOrWhiteSpace(action) ? "Marker inserted" : action.Trim();
        _lastMarkerTime = insertedAt;
        _lastMarkerError = string.Empty;
        OnPropertyChanged(nameof(MarkerCount));
        OnPropertyChanged(nameof(LastMarkerText));
        OnPropertyChanged(nameof(LastMarkerAction));
        OnPropertyChanged(nameof(LastMarkerTimeText));
        OnPropertyChanged(nameof(LastMarkerError));
        SetStatus($"{_lastMarkerAction}: {markerText}");
        RefreshDiagnostics();
    }

    public void RecordMarkerError(string message)
    {
        Interlocked.Increment(ref _markerInsertErrorCount);
        _lastMarkerError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(MarkerInsertErrorCount));
        OnPropertyChanged(nameof(LastMarkerError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    private void RecordSessionAction(string message)
    {
        _lastSessionAction = message;
        _lastSessionError = string.Empty;
        OnPropertyChanged(nameof(CurrentSessionName));
        OnPropertyChanged(nameof(CurrentSessionDisplayText));
        OnPropertyChanged(nameof(SessionStartedTimeText));
        OnPropertyChanged(nameof(LastSessionAction));
        OnPropertyChanged(nameof(LastSessionError));
        OnPropertyChanged(nameof(ActiveLogFileName));
        SetStatus(message);
        RefreshDiagnostics();
    }

    private void RecordSessionError(string message)
    {
        Interlocked.Increment(ref _sessionErrorCount);
        _lastSessionError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(SessionErrorCount));
        OnPropertyChanged(nameof(LastSessionError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    private async Task ToggleLogRenderingPauseAsync()
    {
        if (IsViewFullyPaused)
        {
            ResumeLiveView();
            return;
        }

        lock (_viewPauseGate)
        {
            if (!_viewPause.BeginPause())
            {
                return;
            }
        }

        UpdateLogRenderingPauseState("Pausing view: finishing records accepted before the pause boundary");

        try
        {
            using var timeout = new CancellationTokenSource(ViewPauseDrainTimeout);
            if (!await DrainAcceptedViewWorkAsync(timeout.Token))
            {
                AbortViewPause("xterm drain did not complete");
                return;
            }

            lock (_viewPauseGate)
            {
                _viewPause.CompletePause();
            }

            UpdateLogRenderingPauseState("View paused: incoming records are omitted from the display");
        }
        catch (OperationCanceledException)
        {
            AbortViewPause($"drain timed out after {ViewPauseDrainTimeout.TotalSeconds:0} seconds");
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("MainViewModel.ToggleLogRenderingPauseAsync", ex);
            AbortViewPause($"drain failed: {ex.Message}");
        }
    }

    private void ResumeLiveView()
    {
        ViewPauseCompletion completion;
        lock (_viewPauseGate)
        {
            completion = _viewPause.GetCompletion();
            if (completion.OmittedFromView > 0 || completion.SkippedFromFile > 0)
            {
                var resumeLine = LogLine.System(completion.Summary);
                var dropped = _logBatchDispatcher.Post(resumeLine);
                RecordPendingUiDrops(dropped);
                if (FileLoggingEnabled && completion.SkippedFromFile > 0)
                {
                    _fileLogWriter.TryEnqueue(resumeLine);
                }
            }

            _viewPause.CompleteResume(completion);
        }

        UpdateLogRenderingPauseState(
            $"Live view resumed; {completion.OmittedFromView:N0} paused records omitted from display");
    }

    private void AbortViewPause(string reason)
    {
        ViewPauseCompletion completion;
        lock (_viewPauseGate)
        {
            completion = _viewPause.GetCompletion();
            var failureSummary =
                $"VIEW PAUSE FAILED - {reason}; PS {completion.OmittedFromView:N0} during transition";
            completion = completion with { Summary = failureSummary };
            var failureLine = LogLine.System(failureSummary);
            RecordPendingUiDrops(_logBatchDispatcher.Post(failureLine));
            if (FileLoggingEnabled && completion.SkippedFromFile > 0)
            {
                _fileLogWriter.TryEnqueue(failureLine);
            }

            _viewPause.CompleteResume(completion);
        }

        UpdateLogRenderingPauseState(reason);
    }

    private async Task<bool> DrainAcceptedViewWorkAsync(CancellationToken cancellationToken)
    {
        while (PendingVisualLineCount > 0 || BridgeVisualLogPendingCount > 0)
        {
            await Task.Delay(10, cancellationToken);
        }

        var handlers = ViewPauseDrainRequested;
        if (handlers is null)
        {
            return true;
        }

        foreach (Func<CancellationToken, Task<bool>> handler in handlers.GetInvocationList())
        {
            if (!await handler(cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    public void SetXtermAppendBackpressure(bool active)
    {
        if (_isXtermAppendBackpressureActive == active)
        {
            return;
        }

        _isXtermAppendBackpressureActive = active;
        _logBatchDispatcher.IsPaused = IsViewFullyPaused || _isXtermAppendBackpressureActive;
        OnPropertyChanged(nameof(IsXtermAppendBackpressureActive));
        OnPropertyChanged(nameof(IsEffectiveXtermAutoScrollEnabled));
        OnPropertyChanged(nameof(IsEventAutoScrollSuppressedByXtermBackpressure));
        OnPropertyChanged(nameof(RenderingPauseReason));
        OnPropertyChanged(nameof(RenderingStateText));
    }

    public void RecordXtermBackpressureEventAutoScrollSuppressed()
    {
        Interlocked.Increment(ref _xtermBackpressureEventAutoScrollSuppressedCount);
    }

    public void RecordXtermBackpressureAutoScrollSuppressed()
    {
        Interlocked.Increment(ref _xtermBackpressureAutoScrollSuppressedCount);
    }

    public void RecordXtermBackpressureFullRerenderDeferred()
    {
        Interlocked.Increment(ref _xtermBackpressureFullRerenderDeferredCount);
    }

    private Task ClearScreenAsync()
    {
        _logBatchDispatcher.ClearPending();
        Interlocked.Exchange(ref _pendingLogDropCount, 0);
        Interlocked.Exchange(ref _ruleChangesSinceClearCount, 0);
        Log.Clear();
        InvalidateSearchResultsForCriteriaChange();
        OnPropertyChanged(nameof(PendingVisualLineCount));
        OnPropertyChanged(nameof(RuleChangesSinceClearCount));
        SetFooter(CreateFooterStatus());
        return Task.CompletedTask;
    }

    private Task CopyDiagnosticsAsync()
    {
        try
        {
            RefreshDiagnostics(forceDetailed: true);

            var package = new DataPackage();
            package.SetText(DiagnosticsText);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            SetStatus("Diagnostics copied to clipboard");
        }
        catch (Exception ex)
        {
            SetStatus($"Copy diagnostics failed: {ex.Message}");
            _lastBackgroundError = $"Copy diagnostics failed: {ex.Message}";
            RefreshDiagnostics();
        }

        return Task.CompletedTask;
    }

    private Task CopyHelpAsync()
    {
        try
        {
            var package = new DataPackage();
            package.SetText(HelpGuideText);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            SetStatus("Help guide copied to clipboard");
        }
        catch (Exception ex)
        {
            SetStatus($"Copy help failed: {ex.Message}");
            _lastBackgroundError = $"Copy help failed: {ex.Message}";
            RefreshDiagnostics();
        }

        return Task.CompletedTask;
    }

    private Task CopyEventContextAsync()
    {
        try
        {
            if (SelectedEvent is null)
            {
                SetStatus("Select an event before copying event context.");
                return Task.CompletedTask;
            }

            if (string.IsNullOrWhiteSpace(SelectedEventContextText))
            {
                SetStatus("No event context text is available to copy.");
                return Task.CompletedTask;
            }

            var package = new DataPackage();
            package.SetText(SelectedEventContextText);
            Clipboard.SetContent(package);
            Clipboard.Flush();

            Interlocked.Increment(ref _copiedEventContextCount);
            _lastEventContextUiError = string.Empty;
            OnPropertyChanged(nameof(CopiedEventContextCount));
            OnPropertyChanged(nameof(LastEventContextUiError));
            SetStatus("Event context copied to clipboard");
            RefreshDiagnostics();
        }
        catch (Exception ex)
        {
            RecordEventContextUiError($"Copy event context failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private Task SelectLatestEventAsync()
    {
        SelectLatestEventFromUi();
        return Task.CompletedTask;
    }

    private bool CanSelectLatestEvent()
    {
        return Events.CurrentVisibleEventCount > 0;
    }

    private Task StartMockStressAsync()
    {
        if (!IsConnected ||
            !IsMockPortName(GetActualPortName(SelectedPort)))
        {
            SetStatus("Connect to MOCK before starting stress mode.");
            RefreshDiagnostics();
            return Task.CompletedTask;
        }

        ConfigureMockStressFromUi();
        _serialService.StartMockStress();
        SetStatus("Mock stress running.");
        RefreshMockStressProperties();
        RefreshDiagnostics();
        return Task.CompletedTask;
    }

    private Task StopMockStressAsync()
    {
        _serialService.StopMockStress();
        SetStatus("Mock stress stopped.");
        RefreshMockStressProperties();
        RefreshDiagnostics();
        return Task.CompletedTask;
    }

    private Task ResetMockStressCountersAsync()
    {
        _serialService.ResetMockStressCounters();
        ResetMockSequenceVerificationCounters();
        SetStatus("Mock stress counters reset.");
        RefreshMockStressProperties();
        RefreshDiagnostics();
        return Task.CompletedTask;
    }

    private async Task SendMockCrlfAsync()
    {
        if (!IsConnected ||
            !IsMockPortName(GetActualPortName(SelectedPort)))
        {
            SetStatus("Connect to MOCK before sending mock CRLF.");
            RefreshDiagnostics();
            return;
        }

        await _serialService.SendMockCrlfAsync(CancellationToken.None);
        SetStatus("MOCK CRLF sent.");
        RefreshMockStressProperties();
        RefreshDiagnostics();
    }

    private void ConfigureMockStressFromUi()
    {
        _serialService.ConfigureMockStress(
            SelectedMockStressLinesPerSecond,
            SelectedMockStressBurstSize,
            IsMockStressEventInjectionEnabled,
            IsMockStressInvalidByteInjectionEnabled,
            _selectedMockGeneratorPattern);
        RefreshMockStressProperties();
    }

    private void ResetMockSequenceVerificationCounters()
    {
        Interlocked.Exchange(ref _mockExpectedSequence, 1);
        Interlocked.Exchange(ref _mockLastParsedSequence, 0);
        Interlocked.Exchange(ref _mockMissingSequenceCount, 0);
        Interlocked.Exchange(ref _mockDuplicateSequenceCount, 0);
        Interlocked.Exchange(ref _mockOutOfOrderSequenceCount, 0);
        Interlocked.Exchange(ref _mockMalformedSequenceCount, 0);
        _lastMockSequenceError = string.Empty;
        RefreshMockStressProperties();
    }

    private void VerifyMockSequence(LogLine line)
    {
        if (_serialService.MockGeneratorPattern != MockGeneratorPattern.NormalLines)
        {
            return;
        }

        if (line.Direction != LogDirection.Rx)
        {
            return;
        }

        foreach (var mockLineText in MockStressLogLineSplitter.Split(line))
        {
            VerifyMockSequenceLine(mockLineText);
        }
    }

    private void VerifyMockSequenceLine(string mockLineText)
    {
        if (!TryParseMockSequence(mockLineText, out var sequence))
        {
            if (_serialService.IsMockStressRunning && LooksLikeMalformedMockStressLine(mockLineText))
            {
                Interlocked.Increment(ref _mockMalformedSequenceCount);
                _lastMockSequenceError = $"Malformed mock sequence line: {mockLineText}";
                RunOnUiThread(() =>
                {
                    SetStatus(_lastMockSequenceError);
                    RefreshMockStressProperties();
                    RefreshDiagnostics();
                });
            }

            return;
        }

        var expected = Interlocked.Read(ref _mockExpectedSequence);
        var previous = Interlocked.Read(ref _mockLastParsedSequence);

        if (sequence == expected)
        {
            Interlocked.Exchange(ref _mockExpectedSequence, expected + 1);
            Interlocked.Exchange(ref _mockLastParsedSequence, sequence);
            return;
        }

        if (sequence > expected)
        {
            var missing = sequence - expected;
            Interlocked.Add(ref _mockMissingSequenceCount, missing);
            Interlocked.Exchange(ref _mockExpectedSequence, sequence + 1);
            Interlocked.Exchange(ref _mockLastParsedSequence, sequence);
            _lastMockSequenceError = $"Missing mock sequence(s): expected {expected:D6}, got {sequence:D6}";
            RunOnUiThread(() =>
            {
                SetStatus(_lastMockSequenceError);
                RefreshMockStressProperties();
                RefreshDiagnostics();
            });
            return;
        }

        if (sequence == previous)
        {
            Interlocked.Increment(ref _mockDuplicateSequenceCount);
            _lastMockSequenceError = $"Duplicate mock sequence: {sequence:D6}";
        }
        else
        {
            Interlocked.Increment(ref _mockOutOfOrderSequenceCount);
            _lastMockSequenceError = $"Out-of-order mock sequence: expected {expected:D6}, got {sequence:D6}";
        }

        Interlocked.Exchange(ref _mockLastParsedSequence, sequence);
        RunOnUiThread(() =>
        {
            SetStatus(_lastMockSequenceError);
            RefreshMockStressProperties();
            RefreshDiagnostics();
        });
    }

    private static bool TryParseMockSequence(string text, out long sequence)
    {
        sequence = 0;
        if (string.IsNullOrWhiteSpace(text) || !char.IsDigit(text[0]))
        {
            return false;
        }

        var index = 0;
        while (index < text.Length && char.IsDigit(text[index]))
        {
            index++;
        }

        if (index < 6 || index >= text.Length || text[index] != ' ')
        {
            return false;
        }

        return long.TryParse(text.AsSpan(0, index), NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
    }

    private static bool LooksLikeMalformedMockStressLine(string text)
    {
        return text.Contains("mock", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("serial sample", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("threshold reached", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("simulated fault", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("bus fault", StringComparison.OrdinalIgnoreCase));
    }

    private bool CanCopyEventContext()
    {
        return SelectedEvent is not null && !string.IsNullOrWhiteSpace(SelectedEventContextText);
    }

    private Task OpenLogFolderAsync()
    {
        try
        {
            var folder = GetLogFolderPath();
            Directory.CreateDirectory(folder);

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true
            });

            RecordLogFileActionSuccess($"Opened log folder: {folder}");
        }
        catch (Exception ex)
        {
            RecordLogFileActionError($"Open log folder failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private async Task ToggleFileLoggingAsync()
    {
        await SetFileLoggingEnabledAsync(!FileLoggingEnabled, recordSettingChange: true);
    }

    private async Task SetFileLoggingEnabledAsync(bool enabled, bool recordSettingChange)
    {
        var gateEntered = false;
        Interlocked.Increment(ref _fileLoggingTransitionCount);
        OnPropertyChanged(nameof(CanEditLogFileName));
        try
        {
            await _connectionLifecycleGate.WaitAsync();
            gateEntered = true;

            if (_currentLogSettings.FileLoggingEnabled == enabled)
            {
                RecordLogToggleAction(enabled ? "Log saving already ON." : "Log saving already OFF.");
                return;
            }

            if (enabled)
            {
                if (!PrepareLogFileNameForNewRun())
                {
                    NotifyFileLoggingStateChanged();
                    return;
                }

                var started = true;
                if (IsConnected)
                {
                    started = await TryStartFileLoggingAsync(LogSaveDirectory, CancellationToken.None);
                }

                if (started)
                {
                    _currentLogSettings.FileLoggingEnabled = true;
                    if (recordSettingChange)
                    {
                        RecordSettingsChange("Log Save", SettingsApplyBehavior.Immediate, "ON");
                    }

                    RecordLogToggleAction(IsConnected ? "Log saving ON." : "Log saving ON; files open when connected/log lines arrive.");
                    SetStatus("Log saving ON.");
                }
            }
            else
            {
                _currentLogSettings.FileLoggingEnabled = false;
                if (recordSettingChange)
                {
                    RecordSettingsChange("Log Save", SettingsApplyBehavior.Immediate, "OFF");
                }

                NotifyFileLoggingStateChanged();
                await StopFileLoggingAsync(CancellationToken.None);
                RecordLogToggleAction("Log saving OFF.");
                SetStatus("Log saving OFF.");
            }

            NotifyFileLoggingStateChanged();
        }
        catch (Exception ex)
        {
            RecordLogToggleError($"Log saving toggle failed: {ex.Message}");
            SetStatus("Log saving toggle failed.");
        }
        finally
        {
            if (gateEntered)
            {
                _connectionLifecycleGate.Release();
            }

            Interlocked.Decrement(ref _fileLoggingTransitionCount);
            OnPropertyChanged(nameof(CanEditLogFileName));
        }
    }

    private async Task<bool> TryStartFileLoggingAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            ApplySizeRotationSettings();
            await _fileLogWriter.StartAsync(directory, cancellationToken);
            _fileWriterDroppedHealthBaseline = _fileLogWriter.DroppedLineCount;
            _fileWriterErrorHealthBaseline = _fileLogWriter.FileErrorCount;
            RefreshLogFileActionProperties();
            return true;
        }
        catch (Exception ex)
        {
            await StopFileLoggingAfterFailedStartAsync();
            _currentLogSettings.FileLoggingEnabled = false;
            RecordLogToggleError($"Log saving ON failed: {ex.Message}");
            SetStatus("Log saving failed; continuing without file logging.");
            NotifyFileLoggingStateChanged();
            return false;
        }
    }

    private async Task StopFileLoggingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _fileLogWriter.StopAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            RecordLogToggleError($"Log saving OFF cleanup failed: {ex.Message}");
        }
    }

    private async Task StopFileLoggingAfterFailedStartAsync()
    {
        try
        {
            await _fileLogWriter.StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("MainViewModel.StopFileLoggingAfterFailedStartAsync", ex);
        }
    }

    private void RecordLogToggleAction(string message)
    {
        _lastLogToggleAction = message;
        _lastLogToggleTimeText = FormatDiagnosticTime(DateTimeOffset.Now);
        _lastLogToggleError = string.Empty;
        OnPropertyChanged(nameof(LastLogToggleAction));
        OnPropertyChanged(nameof(LastLogToggleTimeText));
        OnPropertyChanged(nameof(LastLogToggleError));
        SetFooter(CreateFooterStatus());
        RefreshDiagnostics();
    }

    private void RecordLogToggleError(string message)
    {
        Interlocked.Increment(ref _logToggleErrorCount);
        _lastLogToggleAction = message;
        _lastLogToggleTimeText = FormatDiagnosticTime(DateTimeOffset.Now);
        _lastLogToggleError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(LastLogToggleAction));
        OnPropertyChanged(nameof(LastLogToggleTimeText));
        OnPropertyChanged(nameof(LastLogToggleError));
        OnPropertyChanged(nameof(LogToggleErrorCount));
        SetFooter(CreateFooterStatus());
        RefreshDiagnostics();
    }

    private Task OpenCurrentSerialLogAsync()
    {
        OpenLogFile(CurrentSerialLogPath, "serial log");
        return Task.CompletedTask;
    }

    private Task CopySerialLogPathAsync()
    {
        CopyLogPath(CurrentSerialLogPath, "serial log");
        return Task.CompletedTask;
    }

    public void SetLogSaveDirectoryFromPicker(string? selectedDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(selectedDirectory))
            {
                RecordSaveDirectoryAction("Log folder browse canceled.");
                return;
            }

            if (!TryValidateSaveDirectory(selectedDirectory, out var validatedDirectory))
            {
                var message = string.IsNullOrWhiteSpace(LastSettingsValidationError)
                    ? "Log folder browse failed: selected directory is invalid."
                    : $"Log folder browse failed: {LastSettingsValidationError}";
                RecordSaveDirectoryError(message);
                OnPropertyChanged(nameof(LogSaveDirectory));
                return;
            }

            LogSaveDirectory = validatedDirectory;
            RecordSaveDirectoryAction(IsConnected
                ? $"Selected log folder; reconnect required: {validatedDirectory}"
                : $"Selected log folder: {validatedDirectory}");
        }
        catch (Exception ex)
        {
            RecordSaveDirectoryError($"Log folder browse failed: {ex.Message}");
        }
    }

    public void RecordSaveDirectoryBrowseError(string message)
    {
        RecordSaveDirectoryError(message);
    }

    private bool CanOpenCurrentSerialLog()
    {
        return File.Exists(CurrentSerialLogPath);
    }

    private bool CanUseCurrentSerialLogPath()
    {
        return !string.IsNullOrWhiteSpace(CurrentSerialLogPath) && File.Exists(CurrentSerialLogPath);
    }

    private void OpenLogFile(string path, string label)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                RecordLogFileActionError($"Open {label} failed: no current log path.");
                return;
            }

            if (!File.Exists(path))
            {
                RecordLogFileActionError($"Open {label} failed: file does not exist: {path}");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });

            RecordLogFileActionSuccess($"Opened {label}: {path}");
        }
        catch (Exception ex)
        {
            RecordLogFileActionError($"Open {label} failed: {ex.Message}");
        }
    }

    private void CopyLogPath(string path, string label)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                RecordLogFileActionError($"Copy {label} path failed: no current log path.");
                return;
            }

            if (!File.Exists(path))
            {
                RecordLogFileActionError($"Copy {label} path failed: file does not exist: {path}");
                return;
            }

            var package = new DataPackage();
            package.SetText(path);
            Clipboard.SetContent(package);
            Clipboard.Flush();

            RecordLogFileActionSuccess($"Copied {label} path: {path}");
        }
        catch (Exception ex)
        {
            RecordLogFileActionError($"Copy {label} path failed: {ex.Message}");
        }
    }

    private void RecordLogFileActionSuccess(string message)
    {
        _lastLogFileActionStatus = message;
        _lastLogFileActionError = string.Empty;
        OnPropertyChanged(nameof(LastLogFileActionStatus));
        OnPropertyChanged(nameof(LastLogFileActionError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    private void RecordLogFileActionError(string message)
    {
        Interlocked.Increment(ref _logFileActionErrorCount);
        _lastLogFileActionStatus = message;
        _lastLogFileActionError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(LastLogFileActionStatus));
        OnPropertyChanged(nameof(LogFileActionErrorCount));
        OnPropertyChanged(nameof(LastLogFileActionError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    private void RecordSaveDirectoryAction(string message)
    {
        _lastSaveDirectoryAction = message;
        _lastSaveDirectoryError = string.Empty;
        OnPropertyChanged(nameof(LastSaveDirectoryAction));
        OnPropertyChanged(nameof(LastSaveDirectoryError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    private void RecordSaveDirectoryError(string message)
    {
        Interlocked.Increment(ref _saveDirectoryBrowseErrorCount);
        _lastSaveDirectoryAction = message;
        _lastSaveDirectoryError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(LastSaveDirectoryAction));
        OnPropertyChanged(nameof(SaveDirectoryBrowseErrorCount));
        OnPropertyChanged(nameof(LastSaveDirectoryError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    private void ApplySizeRotationSettings()
    {
        EnsureDefaultSizeRotationBytes();
        var maxFileSizeBytes = SizeRotationEnabled
            ? _currentLogSettings.SizeRotationBytes.GetValueOrDefault()
            : 0;
        _fileLogWriter.MaximumFileSizeBytes = Math.Max(0, maxFileSizeBytes);
    }

    private void EnsureDefaultSizeRotationBytes()
    {
        if (_currentLogSettings.SizeRotationBytes is null or < MinSizeRotationBytes or > MaxSizeRotationBytes)
        {
            _currentLogSettings.SizeRotationBytes = LogSettings.DefaultSizeRotationBytes;
            OnPropertyChanged(nameof(SizeRotationMegabytesText));
            return;
        }

        var normalizedBytes = LogSettings.FloorToWholeMegabytes(_currentLogSettings.SizeRotationBytes.Value);
        if (_currentLogSettings.SizeRotationBytes.Value != normalizedBytes)
        {
            _currentLogSettings.SizeRotationBytes = normalizedBytes;
            OnPropertyChanged(nameof(SizeRotationMegabytesText));
        }
    }

    private void ApplySessionFileNaming(bool requestNewFile)
    {
        try
        {
            var logFileName = ActiveLogFileName;
            var isActive = !string.IsNullOrWhiteSpace(logFileName);
            _fileLogWriter.UpdateLogFileName(logFileName, requestNewFile);

            _lastSessionFileAction = isActive
                ? $"Exact log file name active: {logFileName}"
                : "Automatic timestamp log file name active.";
            _lastSessionFileNamingError = string.Empty;
            OnPropertyChanged(nameof(LastSessionFileAction));
            OnPropertyChanged(nameof(LastSessionFileNamingError));
            RefreshLogFileActionProperties();
        }
        catch (Exception ex)
        {
            RecordSessionFileNamingError($"Log file naming failed: {ex.Message}");
        }
    }

    private void RecordSessionFileNamingError(string message)
    {
        Interlocked.Increment(ref _sessionFileNamingErrorCount);
        _lastSessionFileAction = message;
        _lastSessionFileNamingError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(LastSessionFileAction));
        OnPropertyChanged(nameof(SessionFileNamingErrorCount));
        OnPropertyChanged(nameof(LastSessionFileNamingError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    private bool TryParseIntSetting(string settingName, string? value, int minValue, int maxValue, out int parsed)
    {
        parsed = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            RecordSettingsValidationError($"{settingName} must not be empty. Valid range: {minValue:N0} to {maxValue:N0}.");
            return false;
        }

        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
        {
            RecordSettingsValidationError($"{settingName} must be a whole number. Valid range: {minValue:N0} to {maxValue:N0}.");
            return false;
        }

        return ValidateIntRange(settingName, parsed, minValue, maxValue);
    }

    private bool ValidateIntRange(string settingName, int value, int minValue, int maxValue)
    {
        if (value < minValue || value > maxValue)
        {
            RecordSettingsValidationError($"{settingName} must be between {minValue:N0} and {maxValue:N0}.");
            return false;
        }

        return true;
    }

    private bool TryValidateSaveDirectory(string directory, out string validatedDirectory)
    {
        validatedDirectory = string.Empty;
        if (string.IsNullOrWhiteSpace(directory))
        {
            RecordSettingsValidationError("Log save directory must not be empty.");
            return false;
        }

        try
        {
            validatedDirectory = Path.GetFullPath(directory.Trim());
            Directory.CreateDirectory(validatedDirectory);

            var probePath = Path.Combine(validatedDirectory, $".serialmonitor_write_test_{Guid.NewGuid():N}.tmp");
            using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            if (File.Exists(probePath))
            {
                File.Delete(probePath);
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            RecordSettingsValidationError($"Log save directory is invalid or not writable: {ex.Message}");
            validatedDirectory = string.Empty;
            return false;
        }
    }

    private static string FormatByteCount(long bytes)
    {
        const long gib = 1024L * 1024 * 1024;
        const long mib = 1024L * 1024;

        if (bytes % gib == 0)
        {
            return $"{bytes / gib:N0} GB";
        }

        if (bytes % mib == 0)
        {
            return $"{bytes / mib:N0} MB";
        }

        return $"{bytes:N0} bytes";
    }

    private void RecordSettingsChange(string settingName, SettingsApplyBehavior behavior, string valueText)
    {
        if (_suppressSettingsApplyRecording)
        {
            return;
        }

        var safeName = string.IsNullOrWhiteSpace(settingName) ? "Setting" : settingName.Trim();
        var safeValue = string.IsNullOrWhiteSpace(valueText) ? "(blank)" : valueText.Trim();
        _lastSettingsChange = $"{safeName}: {safeValue}";
        _lastSettingsApplyError = string.Empty;

        switch (behavior)
        {
            case SettingsApplyBehavior.Immediate:
                _pendingReconnectSettings.Remove(safeName);
                _pendingRestartSettings.Remove(safeName);
                _lastSettingsApplyStatus = $"{safeName}: applied immediately.";
                break;
            case SettingsApplyBehavior.ReconnectRequired:
                _pendingReconnectSettings.Add(safeName);
                _pendingRestartSettings.Remove(safeName);
                _lastSettingsApplyStatus = $"{safeName}: saved. Reconnect required.";
                break;
            case SettingsApplyBehavior.AutomaticReconnect:
                _pendingReconnectSettings.Add(safeName);
                _pendingRestartSettings.Remove(safeName);
                _lastSettingsApplyStatus = $"{safeName}: saved. Reconnecting automatically.";
                break;
            case SettingsApplyBehavior.NextSession:
                _pendingRestartSettings.Add(safeName);
                _lastSettingsApplyStatus = $"{safeName}: saved. Applies on next start.";
                break;
            case SettingsApplyBehavior.ProfileOnly:
                _pendingRestartSettings.Add(safeName);
                _lastSettingsApplyStatus = $"{safeName}: profile value changed. Not live-applied.";
                break;
            default:
                _lastSettingsApplyStatus = $"{safeName}: saved.";
                break;
        }

        RefreshSettingsApplyStatusProperties();
        SetStatus(_lastSettingsApplyStatus);
        RefreshDiagnostics();
    }

    private void RecordSettingsApplyStatus(string changeText, string statusText)
    {
        if (_suppressSettingsApplyRecording)
        {
            return;
        }

        _lastSettingsChange = string.IsNullOrWhiteSpace(changeText) ? "Settings" : changeText.Trim();
        _lastSettingsApplyStatus = string.IsNullOrWhiteSpace(statusText) ? "Settings updated." : statusText.Trim();
        _lastSettingsApplyError = string.Empty;
        RefreshSettingsApplyStatusProperties();
        SetStatus(_lastSettingsApplyStatus);
        RefreshDiagnostics();
    }

    private void RecordSettingsValidationError(string message)
    {
        var safeMessage = string.IsNullOrWhiteSpace(message)
            ? "Invalid setting value."
            : message.Trim();

        Interlocked.Increment(ref _settingsValidationErrorCount);
        _lastSettingsValidationError = safeMessage;
        _lastSettingsApplyError = safeMessage;
        _lastSettingsApplyStatus = safeMessage;
        RefreshSettingsApplyStatusProperties();
        SetStatus(safeMessage);
        RefreshDiagnostics();
    }

    private void ClearPendingReconnectSettings()
    {
        if (_pendingReconnectSettings.Count == 0)
        {
            return;
        }

        _pendingReconnectSettings.Clear();
        RefreshSettingsApplyStatusProperties();
    }

    private void RefreshSettingsApplyStatusProperties()
    {
        OnPropertyChanged(nameof(LastSettingsChange));
        OnPropertyChanged(nameof(LastSettingsApplyStatus));
        OnPropertyChanged(nameof(SettingsApplyErrorCount));
        OnPropertyChanged(nameof(LastSettingsApplyError));
        OnPropertyChanged(nameof(SettingsValidationErrorCount));
        OnPropertyChanged(nameof(LastSettingsValidationError));
        OnPropertyChanged(nameof(LastNormalizedSetting));
        OnPropertyChanged(nameof(ProfileNormalizationCount));
        OnPropertyChanged(nameof(PendingReconnectRequiredSettingsCount));
        OnPropertyChanged(nameof(PendingRestartRequiredSettingsCount));
        OnPropertyChanged(nameof(SettingsPendingSummaryText));
    }

    private void RecordSettingsApplyError(string message)
    {
        Interlocked.Increment(ref _settingsApplyErrorCount);
        _lastSettingsApplyError = message;
        _lastBackgroundError = message;
        _lastSettingsApplyStatus = message;
        RefreshSettingsApplyStatusProperties();
        SetStatus(message);
        RefreshDiagnostics();
    }

    private void RecordStatusChangedThreadMarshalError(string message)
    {
        Interlocked.Increment(ref _statusChangedThreadMarshalErrorCount);
        _lastStatusChangedThreadMarshalError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(StatusChangedThreadMarshalErrorCount));
        OnPropertyChanged(nameof(LastStatusChangedThreadMarshalError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    private void RecordStatusChangedThreadMarshalErrorWithoutUi(string message)
    {
        Interlocked.Increment(ref _statusChangedThreadMarshalErrorCount);
        _lastStatusChangedThreadMarshalError = message;
        _lastBackgroundError = message;
    }

    private void FanOutLogLine(
        LogLine line,
        bool fileEligible,
        bool detectEvent,
        bool useBridgeVisualQueue = false)
    {
        if (detectEvent)
        {
            _eventDetector.TryEnqueue(line);
        }

        lock (_viewPauseGate)
        {
            var decision = _viewPause.ClassifyRecord(
                fileEligible,
                FileLoggingEnabled,
                FileLoggingWhileViewPaused);
            if (decision.EnqueueFile)
            {
                _fileLogWriter.TryEnqueue(line);
            }

            if (decision.OmitFromView)
            {
                return;
            }

            if (useBridgeVisualQueue)
            {
                TryEnqueueAcceptedBridgeVisualLog(line);
            }
            else
            {
                PostAcceptedVisualLog(line);
            }
        }
    }

    private void PostAcceptedVisualLog(LogLine line)
    {
        var dropped = _logBatchDispatcher.Post(line);
        RecordPendingUiDrops(dropped);
        UpdateMax(ref _maxVisualBacklogLineCount, PendingVisualLineCount);
    }

    private void TryEnqueueAcceptedBridgeVisualLog(LogLine line)
    {
        Interlocked.Increment(ref _bridgeVisualLogPendingCount);
        if (_bridgeVisualLogQueue.Writer.TryWrite(line))
        {
            return;
        }

        Interlocked.Decrement(ref _bridgeVisualLogPendingCount);
        Interlocked.Increment(ref _bridgeVisualLogDroppedCount);
        Volatile.Write(ref _backgroundStatusSnapshotDirty, 1);
    }

    private async Task ObserveBridgeVisualLogsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in _bridgeVisualLogQueue.Reader.ReadAllAsync(cancellationToken))
            {
                // Post before decrementing so a pause drain can never observe both queues as empty
                // while an accepted bridge record is between them.
                PostAcceptedVisualLog(line);
                Interlocked.Decrement(ref _bridgeVisualLogPendingCount);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("MainViewModel.ObserveBridgeVisualLogsAsync", ex);
            Interlocked.Increment(ref _bridgeVisualLogDroppedCount);
            Volatile.Write(ref _backgroundStatusSnapshotDirty, 1);
        }
    }

    private static Channel<LogLine> CreateBridgeVisualLogQueue()
    {
        return Channel.CreateBounded<LogLine>(new BoundedChannelOptions(BridgeVisualLogQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    private void ApplyLogRenderBatch(IReadOnlyList<LogLine> batch)
    {
        var pendingDrops = Interlocked.Exchange(ref _pendingLogDropCount, 0);
        Log.AddPendingDropCount((int)Math.Min(int.MaxValue, pendingDrops));
        UpdateMax(ref _maxVisualBacklogLineCount, PendingVisualLineCount);
        Volatile.Write(ref _lastVisualAppendLineCount, batch.Count);
        UpdateMax(ref _maxVisualAppendLineCount, batch.Count);

        if (batch.Count > 0)
        {
            Interlocked.Increment(ref _visualAppendBatchCount);
        }

        if (batch.Count == 0)
        {
            return;
        }

        Log.AddRange(batch);
        MarkSearchResultsStale();

    }

    private void ApplyEventRenderBatch(IReadOnlyList<DetectedEvent> events)
    {
        if (events.Count == 0)
        {
            SetFooter(CreateFooterStatus());
            return;
        }

        try
        {
            var previouslySelectedEvent = SelectedEvent;
            var previousSelectedEventId = previouslySelectedEvent?.Id;
            Events.AddRange(events);
            if (events.Count >= Events.Capacity)
            {
                Interlocked.Increment(ref _eventListResetCount);
                OnPropertyChanged(nameof(EventListResetCount));
            }
            else
            {
                Interlocked.Increment(ref _eventListIncrementalUpdateCount);
                OnPropertyChanged(nameof(EventListIncrementalUpdateCount));
            }

            PreserveSelectedEventAfterIncrementalUpdate(previouslySelectedEvent, previousSelectedEventId);
            _lastListUpdateError = string.Empty;
            OnPropertyChanged(nameof(LastListUpdateError));
            OnPropertyChanged(nameof(DetectedEventUiItemCount));
            OnPropertyChanged(nameof(DetectedEventUiCountText));
            SelectLatestEventCommand.NotifyCanExecuteChanged();
            SetFooter(CreateFooterStatus());
        }
        catch (Exception ex)
        {
            RecordListUpdateError($"Event list update failed: {ex.Message}");
        }
    }

    private void PreserveSelectedEventAfterIncrementalUpdate(DetectedEvent? previouslySelectedEvent, Guid? previousSelectedEventId)
    {
        if (previouslySelectedEvent is null || previousSelectedEventId is null)
        {
            return;
        }

        var retainedEvent = Events.Events.FirstOrDefault(detectedEvent => detectedEvent.Id == previousSelectedEventId.Value);
        if (retainedEvent is not null)
        {
            if (!ReferenceEquals(SelectedEvent, retainedEvent))
            {
                SelectedEvent = retainedEvent;
            }

            Interlocked.Increment(ref _eventSelectionPreservedCount);
            OnPropertyChanged(nameof(EventSelectionPreservedCount));
            return;
        }

        if (SelectedEvent?.Id == previousSelectedEventId.Value ||
            ReferenceEquals(SelectedEvent, previouslySelectedEvent))
        {
            SelectedEvent = null;
        }

        Interlocked.Increment(ref _eventSelectionLostCount);
        OnPropertyChanged(nameof(EventSelectionLostCount));
    }

    private void ApplyEventContextBatch(IReadOnlyList<DetectedEventContext> contexts)
    {
        if (contexts.Count == 0)
        {
            return;
        }

        var selectedEventId = SelectedEvent?.Id;
        var selectedContextUpdated = false;
        foreach (var context in contexts)
        {
            var isNewContext = !_eventContextsById.ContainsKey(context.EventId);
            _eventContextsById[context.EventId] = context;
            if (isNewContext)
            {
                _eventContextOrder.Enqueue(context.EventId);
            }

            selectedContextUpdated |= selectedEventId == context.EventId;
        }

        TrimRetainedEventContextsToLimit();

        if (selectedContextUpdated)
        {
            RefreshSelectedEventContextText();
        }

        OnPropertyChanged(nameof(SelectedEventContextAvailable));
        OnPropertyChanged(nameof(SelectedEventContextLineCount));
        OnPropertyChanged(nameof(SelectedEventContextHeaderText));
        CopyEventContextCommand.NotifyCanExecuteChanged();
    }

    private int TrimRetainedEventContextsToLimit()
    {
        var removedCount = 0;
        var limit = RetainedEventContextLimit;
        while (_eventContextsById.Count > limit &&
            _eventContextOrder.TryDequeue(out var expiredEventId))
        {
            if (_eventContextsById.Remove(expiredEventId))
            {
                removedCount++;
            }
        }

        return removedCount;
    }

    private void RefreshSelectedEventContextText()
    {
        try
        {
            if (SelectedEvent is null)
            {
                SelectedEventContextStatusText = "Select an event.";
                SelectedEventContextText = "Select an event.";
                OnPropertyChanged(nameof(SelectedEventContextHeaderText));
                return;
            }

            _eventContextsById.TryGetValue(SelectedEvent.Id, out var completedContext);
            SelectedEventContextStatusText = completedContext is null
                ? CreateMissingContextStatus()
                : $"before {completedContext.BeforeContextLines.Count:N0} / after {completedContext.AfterContextLines.Count:N0}";
            SelectedEventContextText = BuildEventContextText(SelectedEvent, completedContext);
            OnPropertyChanged(nameof(SelectedEventContextLineCount));
            OnPropertyChanged(nameof(SelectedEventContextHeaderText));
            CopyEventContextCommand.NotifyCanExecuteChanged();
            Interlocked.Increment(ref _contextRefreshCount);
            _lastContextRefreshError = string.Empty;
            OnPropertyChanged(nameof(ContextRefreshCount));
            OnPropertyChanged(nameof(LastContextRefreshError));
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _contextRefreshErrorCount);
            _lastContextRefreshError = $"Event context refresh failed: {ex.Message}";
            OnPropertyChanged(nameof(ContextRefreshErrorCount));
            OnPropertyChanged(nameof(LastContextRefreshError));
            RecordEventContextUiError($"Event context display failed: {ex.Message}");
        }
    }

    private string CreateMissingContextStatus()
    {
        return IsConnected || _eventDetector.ActivePendingContextCount > 0
            ? "Context pending..."
            : "Context unavailable.";
    }

    private string BuildEventContextText(DetectedEvent detectedEvent, DetectedEventContext? completedContext)
    {
        var beforeLines = completedContext?.BeforeContextLines ?? detectedEvent.BeforeContextLines;
        var afterLines = completedContext?.AfterContextLines;
        var beforeLimit = completedContext?.BeforeContextLineLimit ?? _currentEventContextSettings.BeforeContextLines;
        var afterLimit = completedContext?.AfterContextLineLimit ?? _currentEventContextSettings.AfterContextLines;
        var builder = new StringBuilder();

        builder.AppendLine($"BEFORE ({beforeLimit:N0})");
        AppendLogLines(builder, beforeLines);

        builder.AppendLine();
        builder.AppendLine("MATCHED");
        builder.AppendLine(detectedEvent.SourceLogLine is not null
            ? FormatContextLogLine(detectedEvent.SourceLogLine)
            : FormatContextEventLine(detectedEvent));

        builder.AppendLine();
        builder.AppendLine($"AFTER ({afterLimit:N0})");
        if (afterLines is null)
        {
            builder.AppendLine(CreateMissingContextStatus());
        }
        else
        {
            AppendLogLines(builder, afterLines);
        }
        return builder.ToString();
    }

    private void AppendLogLines(StringBuilder builder, IReadOnlyList<LogLine> lines)
    {
        if (lines.Count == 0)
        {
            builder.AppendLine("(none)");
            return;
        }

        foreach (var line in lines)
        {
            builder.AppendLine(FormatContextLogLine(line));
        }
    }

    private string FormatContextLogLine(LogLine line)
    {
        return ShowTimestampInLogView
            ? line.Format(_currentUiSettings.TimestampDisplayFormat)
            : $"{line.DirectionText} {line.Text}";
    }

    private string FormatContextEventLine(DetectedEvent detectedEvent)
    {
        return ShowTimestampInLogView
            ? detectedEvent.Formatted
            : $"{detectedEvent.DirectionText} {detectedEvent.Message}";
    }

    private static string FormatDirection(LogDirection direction)
    {
        return direction switch
        {
            LogDirection.Tx => "TX",
            LogDirection.Rx => "RX",
            LogDirection.Mark => "MARK",
            _ => "SYS"
        };
    }

    private void RecordEventContextUiError(string message)
    {
        Interlocked.Increment(ref _eventContextUiErrorCount);
        _lastEventContextUiError = message;
        _lastBackgroundError = message;
        OnPropertyChanged(nameof(EventContextUiErrorCount));
        OnPropertyChanged(nameof(LastEventContextUiError));
        SetStatus(message);
        RefreshDiagnostics();
    }

    private void UpdateLogRenderingPauseState(string statusMessage)
    {
        // During Pausing, accepted pre-boundary work must continue draining. The dispatcher is
        // suspended only after the xterm/UI boundary has completed.
        _logBatchDispatcher.IsPaused = IsViewFullyPaused || _isXtermAppendBackpressureActive;
        OnPropertyChanged(nameof(IsLogRenderingPaused));
        OnPropertyChanged(nameof(IsManualLogRenderingPaused));
        OnPropertyChanged(nameof(IsViewPauseTransitioning));
        OnPropertyChanged(nameof(IsViewFullyPaused));
        OnPropertyChanged(nameof(RenderingPauseReason));
        OnPropertyChanged(nameof(RenderingStateText));
        OnPropertyChanged(nameof(PauseRenderingButtonText));
        OnPropertyChanged(nameof(CompactPauseRenderingButtonText));
        OnPropertyChanged(nameof(CompactPauseRenderingButtonGlyph));
        OnPropertyChanged(nameof(PauseRenderingToolTip));
        OnPropertyChanged(nameof(PendingVisualLineCount));
        SetStatus(statusMessage);
        SetFooter(CreateFooterStatus());
        RefreshDiagnostics();
    }

    private SerialSettings CreateCurrentSettings()
    {
        var settings = _currentSerialSettings.Clone();
        settings.PortName = GetActualPortName(SelectedPort) ?? settings.PortName;
        settings.BaudRate = SelectedBaudRate;
        settings.TxLineEnding = SelectedTxLineEnding;
        settings.SaveDirectory = string.IsNullOrWhiteSpace(_currentLogSettings.SaveDirectory)
            ? settings.SaveDirectory
            : _currentLogSettings.SaveDirectory;
        return settings;
    }

    private AppProfile CreateProfileFromCurrentState()
    {
        var serialSettings = CreateCurrentSettings();
        var logSettings = _currentLogSettings.Clone();
        logSettings.SaveDirectory = serialSettings.SaveDirectory;
        logSettings.FileLoggingEnabled = false;
        var uiSettings = _currentUiSettings.Clone();
        uiSettings.AutoScrollEnabled = IsAutoScrollEnabled;
        uiSettings.EventAutoScrollEnabled = IsEventAutoScrollEnabled;
        uiSettings.SearchCaseSensitive = IsSearchCaseSensitive;
        uiSettings.LastSearchText = SearchText;
        uiSettings.MarkerText = MarkerText;
        uiSettings.RxDisplayMode = SelectedRxDisplayMode;
        uiSettings.TxSendMode = SelectedTxSendMode;
        uiSettings.ShowMockTestPort = ShowMockTestPort;
        uiSettings.CuteBackgroundMode = CuteBackgroundMode;
        uiSettings.CuteBackgroundImagePath = CuteBackgroundImagePath;
        uiSettings.CuteBackgroundOpacity = CuteBackgroundOpacity;
        uiSettings.MockStressLinesPerSecond = SelectedMockStressLinesPerSecond;
        uiSettings.MockStressBurstSize = SelectedMockStressBurstSize;
        uiSettings.MockStressEventInjectionEnabled = IsMockStressEventInjectionEnabled;
        uiSettings.MockStressInvalidByteInjectionEnabled = IsMockStressInvalidByteInjectionEnabled;
        uiSettings.MockGeneratorPattern = _selectedMockGeneratorPattern;

        return new AppProfile
        {
            ProfileSchemaVersion = ProfileSchemaVersion,
            Name = "Default",
            CurrentSessionName = LogFileName,
            SerialSettings = serialSettings,
            LastSuccessfulSerialSettings = _lastSuccessfulSerialSettings?.Clone(),
            LogSettings = logSettings,
            UiSettings = uiSettings,
            EventContextSettings = _currentEventContextSettings.Clone(),
            BridgeSettings = CreatePersistedBridgeSettings(),
            LogRules = LogRules.Select(CloneLogRule).ToList(),
            EventRules = EventRules.Select(CloneEventRule).ToList(),
            HighlightRules = HighlightRules.Select(CloneHighlightRule).ToList(),
            SavedCommands = Commands.SavedCommands.Select(CloneTxCommand).ToList(),
            CommandHistory = Commands.GetHistorySnapshot().ToList(),
            CommandSequences = CommandSequences.Select(CloneCommandSequence).ToList()
        };
    }

    private BridgeSettings CreatePersistedBridgeSettings()
    {
        var settings = _currentBridgeSettings.Clone();
        settings.Enabled = false;
        return settings;
    }

    private void ApplyProfile(AppProfile profile)
    {
        var previousSuppress = _suppressSettingsApplyRecording;
        _suppressSettingsApplyRecording = true;
        try
        {
            _currentSerialSettings = profile.SerialSettings.Clone();
            _lastSuccessfulSerialSettings = profile.LastSuccessfulSerialSettings?.Clone();
            _lastSuccessfulPort = string.IsNullOrWhiteSpace(_lastSuccessfulSerialSettings?.PortName)
                ? "(none)"
                : _lastSuccessfulSerialSettings.PortName;
            _lastSuccessfulBaudRate = _lastSuccessfulSerialSettings?.BaudRate ?? 0;
            _currentLogSettings = profile.LogSettings.Clone();
            _currentLogSettings.FileLoggingEnabled = false;
            _currentUiSettings = profile.UiSettings.Clone();
            _hexGroupTimeoutDraftText = _currentUiSettings.HexGroupTimeoutMs.ToString(CultureInfo.InvariantCulture);
            _currentUiSettings.RxDisplayMode = NormalizeRxDisplayMode(_currentUiSettings.RxDisplayMode);
            _currentUiSettings.TxSendMode = _currentUiSettings.RxDisplayMode == RxDisplayMode.Hex
                ? TxSendMode.Hex
                : TxSendMode.Terminal;
            _eventDetector.UpdateRuleMode(ToLogRuleMode(_currentUiSettings.RxDisplayMode));
            ClearPendingEventNotifications();
            _currentEventContextSettings = profile.EventContextSettings.Clone();
            _currentBridgeSettings = profile.BridgeSettings.Clone();
            _currentBridgeSettings.Enabled = false;
            _sessionName = profile.CurrentSessionName ?? string.Empty;
            _currentSessionName = string.Empty;
            _sessionStartedTime = null;

            if (string.IsNullOrWhiteSpace(_currentLogSettings.SaveDirectory))
            {
                _currentLogSettings.SaveDirectory = _currentSerialSettings.SaveDirectory;
            }

            _currentSerialSettings.SaveDirectory = _currentLogSettings.SaveDirectory;

            SelectedPort = _currentSerialSettings.PortName;
            SelectedBridgePort = _currentBridgeSettings.VirtualPortName;
            SelectedBaudRate = _currentSerialSettings.BaudRate;
            SelectedTxLineEnding = _currentSerialSettings.TxLineEnding;
            IsAutoScrollEnabled = _currentUiSettings.AutoScrollEnabled;
            Events.SetCapacity(_currentUiSettings.MaxVisibleEventCount);
            SetVisibleLogRebuildReason("profile visible log capacity restore");
            Log.SetCapacity(_currentUiSettings.MaxVisibleLogLines);
            _lastVisibleCapChangeTimeText = FormatDiagnosticTime(DateTimeOffset.Now);
            SetVisibleLogRebuildReason("profile timestamp display restore");
            Log.SetShowTimestampInLogView(_currentUiSettings.ShowTimestampInLogView);
            Log.SetTimestampDisplayFormat(_currentUiSettings.TimestampDisplayFormat);
            SetVisibleLogRebuildReason("profile RX view restore");
            Log.SetRxDisplayMode(_currentUiSettings.RxDisplayMode);
            _logPipeline.ConfigureRxDisplay(
                _currentUiSettings.RxDisplayMode,
                _currentUiSettings.HexGroupTimeoutMs);
            _appliedRxDisplayMode = _currentUiSettings.RxDisplayMode;
            _appliedHexGroupTimeoutMs = _currentUiSettings.HexGroupTimeoutMs;
            ApplyProfileUiRuntimeSettings(_currentUiSettings);

            LogRules.Clear();
            foreach (var rule in profile.LogRules.Select(CloneLogRule))
            {
                if (TryNormalizeLogRule(rule, out var normalizedRule, out _))
                {
                    LogRules.Add(normalizedRule);
                }
            }

            SelectedLogRule = LogRules.FirstOrDefault();
            RebuildProjectedRulesFromLogRules();
            _eventDetector.UpdateRules(EventRules.Select(CloneEventRule).ToArray());
            _eventDetector.UpdateContextSettings(_currentEventContextSettings);
            Log.SetHighlightRules(HighlightRules.Select(CloneHighlightRule).ToArray());
            SetVisibleLogRebuildReason("profile visible filter restore");
            RefreshVisibleLogFilterOptions(preserveSelection: false, applyFilter: true);
            RefreshBridgePortOptionsFromCurrentPorts();
            NotifyBridgePropertiesChanged();

            Commands.SavedCommands.Clear();
            foreach (var command in profile.SavedCommands.Select(CloneTxCommand))
            {
                Commands.SavedCommands.Add(command);
            }
            SelectedSavedCommand = Commands.SavedCommands.FirstOrDefault();
            Commands.LoadHistory(profile.CommandHistory);

            CommandSequences.Clear();
            foreach (var sequence in profile.CommandSequences.Select(CloneCommandSequence))
            {
                CommandSequences.Add(sequence);
            }
            SelectedCommandSequence = CommandSequences.FirstOrDefault();
            SelectedCommandSequenceStep = SelectedCommandSequence?.Steps.FirstOrDefault();
            ApplySizeRotationSettings();
            ApplySessionFileNaming(requestNewFile: false);
        }
        finally
        {
            _suppressSettingsApplyRecording = previousSuppress;
        }

        OnPropertyChanged(nameof(ActiveHighlightRuleCount));
        OnPropertyChanged(nameof(SessionName));
        OnPropertyChanged(nameof(LogFileName));
        OnPropertyChanged(nameof(CurrentSessionName));
        OnPropertyChanged(nameof(CurrentSessionDisplayText));
        OnPropertyChanged(nameof(SessionStartedTimeText));
        OnPropertyChanged(nameof(ActiveLogFileName));
        RefreshSettingsProperties();
        NotifyRuleEditorStateChanged();
        NotifyCommandEditorStateChanged();
        NotifyCommandSequenceStateChanged();
        OnPropertyChanged(nameof(IsAutoScrollEnabled));
        NotifyCommandStates();

        if (!IsConnected)
        {
            _ = RefreshPortsAsync();
        }
    }

    private void ApplyProfileUiRuntimeSettings(UiSettings settings)
    {
        _markerText = settings.MarkerText?.Trim() ?? string.Empty;
        _searchText = settings.LastSearchText?.Trim() ?? string.Empty;
        _isSearchCaseSensitive = settings.SearchCaseSensitive;
        _isEventAutoScrollEnabled = settings.EventAutoScrollEnabled;
        _selectedMockStressLinesPerSecond = settings.MockStressLinesPerSecond;
        _selectedMockStressBurstSize = settings.MockStressBurstSize;
        _selectedMockGeneratorPattern = settings.MockGeneratorPattern;
        _isMockStressEventInjectionEnabled = settings.MockStressEventInjectionEnabled;
        _isMockStressInvalidByteInjectionEnabled = settings.MockStressInvalidByteInjectionEnabled;

        ConfigureMockStressFromUi();
        InvalidateSearchResultsForCriteriaChange();
        NotifySearchCommandStates();
    }

    private void RefreshProfileProperties()
    {
        if (!string.IsNullOrWhiteSpace(ProfileLastError))
        {
            _lastNormalizedSetting = ProfileLastError;
        }

        OnPropertyChanged(nameof(CurrentProfilePath));
        OnPropertyChanged(nameof(ProfileStatusText));
        OnPropertyChanged(nameof(ProfileLastError));
        OnPropertyChanged(nameof(ProfileLoadErrorCount));
        OnPropertyChanged(nameof(ProfileSaveErrorCount));
        OnPropertyChanged(nameof(ProfileNormalizationCount));
        OnPropertyChanged(nameof(ProfileLoaded));
        OnPropertyChanged(nameof(ProfileLoadTimeText));
        OnPropertyChanged(nameof(ProfileSaveTimeText));
        OnPropertyChanged(nameof(ProfileLoadCount));
        OnPropertyChanged(nameof(ProfileSaveCount));
        OnPropertyChanged(nameof(ProfileSchemaVersion));
        OnPropertyChanged(nameof(ProfileCuteBackgroundCustomPathCleared));
        OnPropertyChanged(nameof(ProfileCuteBackgroundCustomPathClearReason));
        OnPropertyChanged(nameof(RuleMigrationResult));
        OnPropertyChanged(nameof(LastNormalizedSetting));
        OnPropertyChanged(nameof(LogRuleEditorCount));
        OnPropertyChanged(nameof(ActiveEventLogRuleCount));
        OnPropertyChanged(nameof(ActiveHighlightRuleCount));
        OnPropertyChanged(nameof(ActiveViewFilterRuleCount));
        OnPropertyChanged(nameof(EventRuleEditorCount));
        OnPropertyChanged(nameof(HighlightRuleEditorCount));
        OnPropertyChanged(nameof(SavedCommandEditorCount));
        OnPropertyChanged(nameof(CommandSequenceCount));
        RefreshSettingsProperties(includeBackgroundVisualSettings: false);
    }

    private void RefreshSettingsProperties(bool includeBackgroundVisualSettings = true)
    {
        OnPropertyChanged(nameof(CanEditConnectionSettings));
        OnPropertyChanged(nameof(CanEditSizeRotationMegabytes));
        OnPropertyChanged(nameof(CanEditLogFileName));
        OnPropertyChanged(nameof(SelectedDataBits));
        OnPropertyChanged(nameof(SelectedParity));
        OnPropertyChanged(nameof(SelectedStopBits));
        OnPropertyChanged(nameof(SelectedHandshake));
        OnPropertyChanged(nameof(DtrEnable));
        OnPropertyChanged(nameof(RtsEnable));
        OnPropertyChanged(nameof(SelectedRxLineEnding));
        OnPropertyChanged(nameof(SelectedRxEncoding));
        OnPropertyChanged(nameof(SelectedRxDisplayMode));
        OnPropertyChanged(nameof(IsHexRxViewSelected));
        OnPropertyChanged(nameof(HexGroupTimeoutMs));
        OnPropertyChanged(nameof(HexGroupTimeoutDraftText));
        OnPropertyChanged(nameof(HasPendingHexGroupTimeout));
        OnPropertyChanged(nameof(HexGroupTimeoutAppliedText));
        OnPropertyChanged(nameof(HexGroupTimeoutHeaderText));
        OnPropertyChanged(nameof(HexGroupTimeoutHeaderMinWidth));
        OnPropertyChanged(nameof(HexGroupTimeoutMsText));
        OnPropertyChanged(nameof(HexGroupTimeoutRecommendationText));
        OnPropertyChanged(nameof(SelectedTxSendMode));
        OnPropertyChanged(nameof(IsTxLineEndingEffective));
        OnPropertyChanged(nameof(TxLineEndingToolTip));
        OnPropertyChanged(nameof(FileLoggingEnabled));
        OnPropertyChanged(nameof(FileLoggingActive));
        OnPropertyChanged(nameof(FileLoggingToggleText));
        OnPropertyChanged(nameof(FileLoggingMainStatusText));
        OnPropertyChanged(nameof(FileLoggingToolTip));
        OnPropertyChanged(nameof(FileLoggingWhileViewPaused));
        OnPropertyChanged(nameof(LogSaveDirectory));
        OnPropertyChanged(nameof(SizeRotationEnabled));
        OnPropertyChanged(nameof(SizeRotationMegabytesText));
        OnPropertyChanged(nameof(SessionName));
        OnPropertyChanged(nameof(LogFileName));
        OnPropertyChanged(nameof(ConfiguredLogFileNameDisplayText));
        OnPropertyChanged(nameof(CurrentSessionDisplayText));
        OnPropertyChanged(nameof(SessionStartedTimeText));
        OnPropertyChanged(nameof(LastSessionAction));
        OnPropertyChanged(nameof(LastSessionError));
        OnPropertyChanged(nameof(SessionErrorCount));
        OnPropertyChanged(nameof(ActiveLogFileName));
        OnPropertyChanged(nameof(LastSessionFileAction));
        OnPropertyChanged(nameof(SessionFileNamingErrorCount));
        OnPropertyChanged(nameof(LastSessionFileNamingError));
        OnPropertyChanged(nameof(LastSaveDirectoryAction));
        OnPropertyChanged(nameof(SaveDirectoryBrowseErrorCount));
        OnPropertyChanged(nameof(LastSaveDirectoryError));
        OnPropertyChanged(nameof(MaxVisibleLogLines));
        OnPropertyChanged(nameof(MaxVisibleLogLinesText));
        OnPropertyChanged(nameof(LastVisibleCapChangeTimeText));
        OnPropertyChanged(nameof(MaxVisibleEventCount));
        OnPropertyChanged(nameof(XtermScrollbackSize));
        OnPropertyChanged(nameof(XtermScrollbackSizeText));
        OnPropertyChanged(nameof(EffectiveXtermScrollbackSize));
        OnPropertyChanged(nameof(ConfirmBeforeDisconnect));
        OnPropertyChanged(nameof(IsAutoScrollEnabled));
        OnPropertyChanged(nameof(ShowTimestampInLogView));
        OnPropertyChanged(nameof(SelectedTimestampDisplayFormatOption));
        if (includeBackgroundVisualSettings)
        {
            OnPropertyChanged(nameof(CuteBackgroundMode));
            OnPropertyChanged(nameof(CuteBackgroundImagePath));
            OnPropertyChanged(nameof(CuteBackgroundLayerOpacity));
            OnPropertyChanged(nameof(CuteBackgroundOpacity));
            OnPropertyChanged(nameof(CuteBackgroundOpacityText));
            OnPropertyChanged(nameof(CuteBackgroundOverlayOpacity));
            OnPropertyChanged(nameof(CuteBackgroundFileExists));
            OnPropertyChanged(nameof(CuteBackgroundLoaded));
            OnPropertyChanged(nameof(CuteBackgroundSource));
            OnPropertyChanged(nameof(CuteBackgroundBundledPath));
            OnPropertyChanged(nameof(CuteBackgroundLoadError));
            OnPropertyChanged(nameof(CuteBackgroundLastAppliedTimeText));
            OnPropertyChanged(nameof(CuteBackgroundApplyCount));
            OnPropertyChanged(nameof(CuteBackgroundImageReloadCount));
            OnPropertyChanged(nameof(CuteBackgroundSkippedUnchangedCount));
        }
        OnPropertyChanged(nameof(ShowMockTestPort));
        OnPropertyChanged(nameof(CurrentPortIsMock));
        OnPropertyChanged(nameof(LastPortRefreshIncludedMock));
        OnPropertyChanged(nameof(LastSuccessfulPort));
        OnPropertyChanged(nameof(LastSuccessfulBaudRate));
        OnPropertyChanged(nameof(LastPortSelectionChangeReason));
        OnPropertyChanged(nameof(LastDisconnectPreservedPort));
        OnPropertyChanged(nameof(LastPortRefreshResult));
        OnPropertyChanged(nameof(SelectedPortAvailable));
        OnPropertyChanged(nameof(MarkerText));
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(IsSearchCaseSensitive));
        OnPropertyChanged(nameof(IsEventAutoScrollEnabled));
        OnPropertyChanged(nameof(SelectedMockStressLinesPerSecond));
        OnPropertyChanged(nameof(SelectedMockStressBurstSize));
        OnPropertyChanged(nameof(SelectedMockGeneratorPatternOption));
        OnPropertyChanged(nameof(SelectedMockGeneratorPatternText));
        OnPropertyChanged(nameof(IsMockStressEventInjectionEnabled));
        OnPropertyChanged(nameof(IsMockStressInvalidByteInjectionEnabled));
        OnPropertyChanged(nameof(BeforeContextLines));
        OnPropertyChanged(nameof(AfterContextLines));
        RefreshSettingsApplyStatusProperties();
    }

    private void RefreshLogFileActionProperties()
    {
        OnPropertyChanged(nameof(FileLoggingEnabled));
        OnPropertyChanged(nameof(FileLoggingActive));
        OnPropertyChanged(nameof(FileLoggingToggleText));
        OnPropertyChanged(nameof(FileLoggingMainStatusText));
        OnPropertyChanged(nameof(FileLoggingToolTip));
        OnPropertyChanged(nameof(CurrentSerialLogPath));
        OnPropertyChanged(nameof(LastLogFileActionStatus));
        OnPropertyChanged(nameof(LogFileActionErrorCount));
        OnPropertyChanged(nameof(LastLogFileActionError));
        OnPropertyChanged(nameof(LastSaveDirectoryAction));
        OnPropertyChanged(nameof(SaveDirectoryBrowseErrorCount));
        OnPropertyChanged(nameof(LastSaveDirectoryError));
        OnPropertyChanged(nameof(LastSessionFileAction));
        OnPropertyChanged(nameof(SessionFileNamingErrorCount));
        OnPropertyChanged(nameof(LastSessionFileNamingError));
        NotifyLogFileActionCommandStates();
    }

    private void RefreshMockStressProperties()
    {
        OnPropertyChanged(nameof(ShowMockTestPort));
        OnPropertyChanged(nameof(CurrentPortIsMock));
        OnPropertyChanged(nameof(LastPortRefreshIncludedMock));
        OnPropertyChanged(nameof(IsMockStressRunning));
        OnPropertyChanged(nameof(CanStartMockStress));
        OnPropertyChanged(nameof(CanStopMockStress));
        OnPropertyChanged(nameof(MockStressStatusText));
        OnPropertyChanged(nameof(MockGeneratedLineCount));
        OnPropertyChanged(nameof(IsMockNoNewlineActive));
        OnPropertyChanged(nameof(MockNoNewlineEmittedBytes));
        OnPropertyChanged(nameof(SelectedMockGeneratorPatternText));
        OnPropertyChanged(nameof(MockExpectedSequence));
        OnPropertyChanged(nameof(MockLastGeneratedSequence));
        OnPropertyChanged(nameof(MockLastParsedSequence));
        OnPropertyChanged(nameof(MockMissingSequenceCount));
        OnPropertyChanged(nameof(MockDuplicateSequenceCount));
        OnPropertyChanged(nameof(MockOutOfOrderSequenceCount));
        OnPropertyChanged(nameof(MockMalformedSequenceCount));
        OnPropertyChanged(nameof(LastMockSequenceError));
        StartMockStressCommand.NotifyCanExecuteChanged();
        StopMockStressCommand.NotifyCanExecuteChanged();
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

    private static LogRule CloneLogRule(LogRule rule)
    {
        return new LogRule
        {
            Name = rule.Name,
            Keyword = rule.Keyword,
            Enabled = rule.Enabled,
            UseForEvent = rule.UseForEvent,
            UseForHighlight = rule.UseForHighlight,
            UseAsViewFilter = rule.UseAsViewFilter,
            CaseSensitive = rule.CaseSensitive,
            Mode = rule.Mode,
            MatchDirection = rule.MatchDirection,
            ForegroundColor = rule.ForegroundColor,
            BackgroundColor = rule.BackgroundColor,
            Priority = rule.Priority,
            TrayNotificationEnabled = rule.TrayNotificationEnabled,
            SoundNotificationEnabled = rule.SoundNotificationEnabled,
            PopupNotificationEnabled = rule.PopupNotificationEnabled,
            NotificationCooldownSeconds = rule.NotificationCooldownSeconds
        };
    }

    private static EventRule CreateEventRuleFromLogRule(LogRule rule)
    {
        return new EventRule
        {
            Name = rule.Name,
            Keyword = rule.Keyword,
            Enabled = rule.Enabled,
            CaseSensitive = rule.CaseSensitive,
            Mode = rule.Mode,
            MatchDirection = ConvertDirection(rule.MatchDirection),
            HighlightColor = rule.ForegroundColor,
            TrayNotificationEnabled = rule.TrayNotificationEnabled,
            SoundNotificationEnabled = rule.SoundNotificationEnabled,
            PopupNotificationEnabled = rule.PopupNotificationEnabled,
            NotificationCooldownSeconds = rule.NotificationCooldownSeconds
        };
    }

    private static HighlightRule CreateHighlightRuleFromLogRule(LogRule rule)
    {
        return new HighlightRule
        {
            Name = rule.Name,
            Keyword = rule.Keyword,
            Enabled = rule.Enabled,
            CaseSensitive = rule.CaseSensitive,
            Mode = rule.Mode,
            UseAsViewFilter = rule.UseAsViewFilter,
            ForegroundColor = rule.ForegroundColor,
            BackgroundColor = rule.BackgroundColor,
            Priority = rule.Priority,
            MatchDirection = rule.MatchDirection
        };
    }

    private static HighlightRule CloneHighlightRule(HighlightRule rule)
    {
        return new HighlightRule
        {
            Name = rule.Name,
            Keyword = rule.Keyword,
            Enabled = rule.Enabled,
            CaseSensitive = rule.CaseSensitive,
            Mode = rule.Mode,
            UseAsViewFilter = rule.UseAsViewFilter,
            ForegroundColor = rule.ForegroundColor,
            BackgroundColor = rule.BackgroundColor,
            Priority = rule.Priority,
            MatchDirection = rule.MatchDirection
        };
    }

    private static EventMatchDirection ConvertDirection(HighlightMatchDirection direction)
    {
        return direction switch
        {
            HighlightMatchDirection.TxOnly => EventMatchDirection.TxOnly,
            HighlightMatchDirection.Both => EventMatchDirection.Both,
            _ => EventMatchDirection.RxOnly
        };
    }

    private static TxCommand CloneTxCommand(TxCommand command)
    {
        return new TxCommand(command.Name, command.CommandText)
        {
            LineEndingMode = command.LineEndingMode,
            OptionalShortcut = command.OptionalShortcut
        };
    }

    private static CommandSequence CloneCommandSequence(CommandSequence sequence)
    {
        return new CommandSequence
        {
            Name = sequence.Name,
            Steps = new ObservableCollection<CommandSequenceStep>(
                sequence.Steps.Select(CloneCommandSequenceStep))
        };
    }

    private static CommandSequenceStep CloneCommandSequenceStep(CommandSequenceStep step)
    {
        return new CommandSequenceStep
        {
            Name = step.Name,
            CommandText = step.CommandText,
            LineEndingMode = step.LineEndingMode,
            DelayAfterMs = step.DelayAfterMs,
            Comment = step.Comment
        };
    }

    private void OnBackgroundError(object? sender, string message)
    {
        RunOnUiThread(() =>
        {
            _lastBackgroundError = message;
            StatusText = message;
            IsConnected = _serialService.IsConnected;
            OnPropertyChanged(nameof(ConnectionStateText));
            OnPropertyChanged(nameof(CompactConnectionStatusText));
            FooterStatusText = CreateFooterStatus();
            RefreshDiagnostics();
        });
    }

    private void OnSerialStatusChanged(object? sender, EventArgs args)
    {
        if (!_serialService.IsConnected &&
            _bridgeService.IsRunning &&
            Interlocked.CompareExchange(ref _bridgeStopForSerialDisconnectRunning, 1, 0) == 0)
        {
            _ = StopBridgeAfterSerialDisconnectAsync();
        }

        Volatile.Write(ref _backgroundStatusSnapshotDirty, 1);
    }

    private async Task StopBridgeAfterSerialDisconnectAsync()
    {
        try
        {
            _serialService.SetRawBridgePriorityEnabled(false);
            await _bridgeService.StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("MainViewModel.StopBridgeAfterSerialDisconnectAsync", ex);
            _lastBackgroundError = $"Bridge stop after serial disconnect failed: {ex.Message}";
        }
        finally
        {
            Volatile.Write(ref _bridgeStopForSerialDisconnectRunning, 0);
            Volatile.Write(ref _backgroundStatusSnapshotDirty, 1);
        }
    }

    private void RecordPendingUiDrops(int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _pendingLogDropCount, count);
        }
    }

    private bool PrepareLogFileNameForNewRun()
    {
        if (!LogFileNamePolicy.TryValidate(LogFileName, out var logFileName, out var validationError))
        {
            RecordLogToggleError($"LOG ON failed: {validationError}");
            SetStatus("LOG ON failed. Check the log file name.");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(logFileName))
        {
            try
            {
                var path = Path.Combine(LogSaveDirectory, logFileName);
                if (File.Exists(path))
                {
                    RecordLogToggleError($"LOG ON failed: log file already exists: {path}");
                    SetStatus("LOG ON failed. Choose a new log file name.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                RecordLogToggleError($"LOG ON failed: invalid log file path: {ex.Message}");
                SetStatus("LOG ON failed. Check the log folder and file name.");
                return false;
            }
        }

        CurrentSessionName = logFileName;
        _sessionStartedTime = DateTimeOffset.Now;
        OnPropertyChanged(nameof(SessionStartedTimeText));
        ApplySessionFileNaming(requestNewFile: false);
        return true;
    }

    private void OnPipelineStatusChanged(object? sender, EventArgs args)
    {
        Volatile.Write(ref _backgroundStatusSnapshotDirty, 1);
    }

    private void OnFileLogStatusChanged(object? sender, EventArgs args)
    {
        Volatile.Write(ref _backgroundStatusSnapshotDirty, 1);
    }

    private void OnEventDetectorStatusChanged(object? sender, EventArgs args)
    {
        Volatile.Write(ref _backgroundStatusSnapshotDirty, 1);
    }

    private void OnBridgeStatusChanged(object? sender, EventArgs args)
    {
        if (!_bridgeService.IsRunning)
        {
            _serialService.SetRawBridgePriorityEnabled(false);
            RunOnUiThread(() =>
            {
                _currentBridgeSettings.Enabled = false;
                NotifyBridgePropertiesChanged();
            });
        }

        Volatile.Write(ref _backgroundStatusSnapshotDirty, 1);
    }

    private void OnManualTxStateChanged(object? sender, ManualTxStateChangedEventArgs args)
    {
        RunOnUiThread(NotifyManualTxUiStateChanged);
    }

    private void NotifyManualTxUiStateChanged()
    {
        OnPropertyChanged(nameof(ManualTxState));
        OnPropertyChanged(nameof(IsManualTxBusy));
        OnPropertyChanged(nameof(CanSendManualTx));
        OnPropertyChanged(nameof(ManualTxStateText));
        SendCommand.NotifyCanExecuteChanged();
        SendSavedCommandCommand.NotifyCanExecuteChanged();
    }

    private void OnRawBytesReceived(BridgeRxChunk chunk)
    {
        _bridgeService.TryEnqueueDeviceChunk(chunk);
    }

    private async Task ObserveBridgeProcessedLogsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in _bridgeLogProcessor.Logs.ReadAllAsync(cancellationToken))
            {
                FanOutLogLine(
                    line,
                    fileEligible: true,
                    detectEvent: true,
                    useBridgeVisualQueue: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("MainViewModel.ObserveBridgeProcessedLogsAsync", ex);
            _lastBackgroundError = $"Bridge log observer failed: {ex.Message}";
            Volatile.Write(ref _backgroundStatusSnapshotDirty, 1);
        }
    }

    private void OnDiagnosticsTimerTick(DispatcherQueueTimer sender, object args)
    {
        StartResourceSnapshotRefreshIfDue();
        var backgroundStatusChanged = Interlocked.Exchange(ref _backgroundStatusSnapshotDirty, 0) != 0;
        if (backgroundStatusChanged)
        {
            IsConnected = _serialService.IsConnected;
            OnPropertyChanged(nameof(ConnectionStateText));
            OnPropertyChanged(nameof(CompactConnectionStatusText));
        }

        OnPropertyChanged(nameof(PendingVisualLineCount));
        OnPropertyChanged(nameof(CurrentViewPauseOmittedLineCount));
        OnPropertyChanged(nameof(TotalViewPauseOmittedLineCount));
        OnPropertyChanged(nameof(ViewPauseCount));
        OnPropertyChanged(nameof(LastViewPauseSummary));
        OnPropertyChanged(nameof(VisualDispatcherFlushCount));
        OnPropertyChanged(nameof(MaxVisualDispatcherBatchSize));
        OnPropertyChanged(nameof(LastVisualAppendLineCount));
        OnPropertyChanged(nameof(MaxVisualAppendLineCount));
        OnPropertyChanged(nameof(VisualAppendBatchCount));
        OnPropertyChanged(nameof(MaxVisualBacklogLineCount));
        OnPropertyChanged(nameof(PendingEventUiCount));
        OnPropertyChanged(nameof(PendingEventContextUiCount));
        OnPropertyChanged(nameof(TotalPendingUiCount));
        OnPropertyChanged(nameof(RecordedRxDropCount));
        OnPropertyChanged(nameof(RecordedUiDropCount));
        NotifyBridgePropertiesChanged();
        OnPropertyChanged(nameof(EventContextUiDroppedCount));
        OnPropertyChanged(nameof(EventUiFlushCount));
        OnPropertyChanged(nameof(MaxEventUiBatchSize));
        OnPropertyChanged(nameof(XtermAppendedLineCount));
        OnPropertyChanged(nameof(XtermAppendBatchCount));
        OnPropertyChanged(nameof(LastXtermAppendLineCount));
        OnPropertyChanged(nameof(LastXtermAppendCharacterCount));
        OnPropertyChanged(nameof(MaxXtermAppendLineCount));
        OnPropertyChanged(nameof(MaxXtermAppendCharacterCount));
        OnPropertyChanged(nameof(XtermPendingCharacterCount));
        OnPropertyChanged(nameof(MaxXtermPendingCharacterCount));
        OnPropertyChanged(nameof(LastRenderedSequenceId));
        OnPropertyChanged(nameof(PendingVisualDeltaLineCount));
        OnPropertyChanged(nameof(MinimizedVisualCoalescedLineCount));
        OnPropertyChanged(nameof(MinimizedVisualCoalescedCharacterCount));
        OnPropertyChanged(nameof(MaxMinimizedVisualCoalescedLineCount));
        OnPropertyChanged(nameof(MaxMinimizedVisualCoalescedCharacterCount));
        OnPropertyChanged(nameof(ActiveHighlightRuleCount));
        RefreshMockStressProperties();
        RefreshProfileProperties();
        RefreshLogFileActionProperties();
        RefreshDiagnostics();
    }

    private void SetStatus(string message)
    {
        RunOnUiThread(() => StatusText = message);
    }

    private void SetFooter(string message)
    {
        RunOnUiThread(() => FooterStatusText = message);
    }

    private string CreateFooterStatus()
    {
        var missingSequenceText = IsMockStressRunning &&
            _serialService.MockGeneratorPattern == MockGeneratorPattern.NormalLines
            ? $" | Missing {MockMissingSequenceCount:N0}"
            : string.Empty;
        var viewBacklogText = PendingVisualLineCount >= PendingVisualWarningThreshold
            ? $" | View backlog {PendingVisualLineCount:N0} l"
            : string.Empty;
        var lastErrorText = CreateLastErrorSummaryText();
        var lastErrorSummary = string.IsNullOrWhiteSpace(lastErrorText)
            ? string.Empty
            : $" | Last {lastErrorText}";
        var bridgeText = IsBridgeActive
            ? $" | BRIDGE ON {_bridgeService.VirtualPortName}"
            : string.Empty;

        return
            $"{HealthStateText} | " +
            CreateResourceStatusText() + " | " +
            $"RX {_logPipeline.ParsedLineCount:N0} l / {_serialService.ReceivedByteCount:N0} B | " +
            $"DRV F/P/O/RX {_serialService.SerialFrameErrorCount:N0}/{_serialService.SerialParityErrorCount:N0}/{_serialService.SerialOverrunErrorCount:N0}/{_serialService.SerialRxOverErrorCount:N0} | " +
            $"Events {_eventDetector.DetectedEventCount:N0} | " +
            CreateLogFileStatusText() +
            bridgeText +
            missingSequenceText +
            viewBacklogText +
            lastErrorSummary;
    }

    private string CreateResourceStatusText()
    {
        var diskText = _hasResourceSnapshot && DiskTotalBytes > 0
            ? $"Disk {FormatByteSize(DiskFreeBytes)} {DiskFreePercent:0.#}%"
            : "Disk n/a";
        var sessionText = _hasResourceSnapshot
            ? FormatByteSize(CurrentSessionLogSizeBytes)
            : "n/a";
        var memoryText = _hasResourceSnapshot
            ? FormatByteSize(ProcessWorkingSetBytes)
            : "n/a";

        return $"{diskText} | Session {sessionText} | Mem {memoryText} | " +
               $"UI {Log.TotalRetainedLineCount:N0}/{Log.Capacity:N0} | " +
               $"Q F/E/U {_fileLogWriter.PendingRequestCount:N0}/{_eventDetector.PendingInputLineCount:N0}/{TotalPendingUiCount:N0} | " +
               $"Drop RX/F/UI {RecordedRxDropCount:N0}/{_fileLogWriter.DroppedLineCount:N0}/{RecordedUiDropCount:N0} | " +
               $"PS {TotalViewPauseOmittedLineCount:N0}";
    }

    private string CreateLogFileStatusText()
    {
        if (!FileLoggingEnabled)
        {
            return "File OFF";
        }

        if (!string.IsNullOrWhiteSpace(_fileLogWriter.CurrentLogFilePath))
        {
            return $"File ON {Path.GetFileName(_fileLogWriter.CurrentLogFilePath)}";
        }

        return "File ON waiting";
    }

    private void RefreshHealthSummary(string lastRuntimeError)
    {
        var warnings = new List<string>();
        var errors = new List<string>();

        AddErrorIf(errors, MockMissingSequenceCount > 0, $"Missing mock sequences: {MockMissingSequenceCount:N0}");
        var fileWriterDroppedSinceBaseline = Math.Max(0, _fileLogWriter.DroppedLineCount - _fileWriterDroppedHealthBaseline);
        var fileWriterErrorsSinceBaseline = Math.Max(0, _fileLogWriter.FileErrorCount - _fileWriterErrorHealthBaseline);

        AddErrorIf(errors, FileLoggingEnabled && fileWriterDroppedSinceBaseline > 0, $"File writer dropped lines: {fileWriterDroppedSinceBaseline:N0}");
        AddErrorIf(errors, _eventDetector.DroppedInputLineCount > 0, $"Event detector dropped input lines: {_eventDetector.DroppedInputLineCount:N0}");
        AddErrorIf(errors, _eventDetector.DroppedOutputEventCount > 0, $"Event detector dropped output events: {_eventDetector.DroppedOutputEventCount:N0}");
        AddErrorIf(errors, XtermAppendErrorCount > 0, $"xterm append errors: {XtermAppendErrorCount:N0}");
        AddErrorIf(errors, FileLoggingEnabled && fileWriterErrorsSinceBaseline > 0, $"File writer errors: {fileWriterErrorsSinceBaseline:N0}");
        AddErrorIf(errors, _serialService.ConnectionErrorCount > 0, $"Serial connection errors: {_serialService.ConnectionErrorCount:N0}");
        AddErrorIf(errors, !string.IsNullOrWhiteSpace(lastRuntimeError), "Unhandled runtime error captured");
        AddErrorIf(errors, BridgeDroppedChunkCount > 0, $"Bridge dropped device-to-virtual chunks: {BridgeDroppedChunkCount:N0}");
        AddErrorIf(
            errors,
            _hasResourceSnapshot && DiskTotalBytes > 0 &&
            (DiskFreeBytes < DiskErrorFreeBytes || DiskFreePercent < 2),
            $"Log disk space critically low: {FormatByteSize(DiskFreeBytes)} free ({DiskFreePercent:0.#}%)");

        AddWarningIf(warnings, PendingVisualLineCount >= PendingVisualWarningThreshold, $"Pending visual lines high: {PendingVisualLineCount:N0}/{PendingVisualWarningThreshold:N0}");
        AddWarningIf(
            warnings,
            IsVisualAppendSuspendedForMinimize && MinimizedVisualCoalescedLineCount >= PendingVisualWarningThreshold,
            $"Minimized visual redraw backlog high: {MinimizedVisualCoalescedLineCount:N0}/{PendingVisualWarningThreshold:N0}");
        AddWarningIf(warnings, Log.DroppedPendingLineCount > 0, $"UI pending lines dropped: {Log.DroppedPendingLineCount:N0}");
        AddWarningIf(warnings, _eventDetector.ContextCaptureDroppedCount > 0, $"Event context captures dropped: {_eventDetector.ContextCaptureDroppedCount:N0}");
        AddWarningIf(
            warnings,
            _eventDetector.MaxContextCaptureScanCount >= 500,
            $"High event context scan fan-out: {_eventDetector.MaxContextCaptureScanCount:N0} captures on one line");
        AddWarningIf(
            warnings,
            _eventDetector.IsContextCaptureOverloadActive,
            $"Event context overload active: {_eventDetector.ActivePendingContextCount:N0} pending; new contexts are temporarily skipped");
        AddWarningIf(
            warnings,
            _eventDetector.ContextCaptureOverloadSkippedCount > 0,
            $"Event contexts skipped during overload: {_eventDetector.ContextCaptureOverloadSkippedCount:N0}");
        AddWarningIf(warnings, EventContextUiDroppedCount > 0, $"UI-only event context updates dropped: {EventContextUiDroppedCount:N0}");
        AddWarningIf(warnings, _eventDetector.ContextCaptureFailedCount > 0, $"Event context captures failed: {_eventDetector.ContextCaptureFailedCount:N0}");
        AddWarningIf(warnings, Log.XtermFormattingErrorCount > 0, $"xterm formatting errors: {Log.XtermFormattingErrorCount:N0}");
        AddWarningIf(warnings, XtermLayoutErrorCount > 0, $"xterm layout errors: {XtermLayoutErrorCount:N0}");
        AddWarningIf(
            warnings,
            _hasResourceSnapshot && DiskTotalBytes > 0 &&
            DiskFreeBytes >= DiskErrorFreeBytes &&
            DiskFreePercent >= 2 &&
            (DiskFreeBytes < DiskWarningFreeBytes || DiskFreePercent < 10),
            $"Log disk space low: {FormatByteSize(DiskFreeBytes)} free ({DiskFreePercent:0.#}%)");
        AddWarningIf(
            warnings,
            FileLoggingEnabled && _hasResourceSnapshot && !string.IsNullOrWhiteSpace(_resourceSnapshotError),
            $"Resource status unavailable: {_resourceSnapshotError}");
        AddWarningIf(warnings, BridgeErrorCount > 0, $"Bridge errors: {BridgeErrorCount:N0}");
        AddWarningIf(
            warnings,
            BridgeVisualLogDroppedCount > 0,
            $"Bridge UI-only log entries dropped: {BridgeVisualLogDroppedCount:N0} (bridge transport unaffected)");
        AddWarningIf(
            warnings,
            _bridgeLogProcessor.DroppedInputChunkCount > 0 || _bridgeLogProcessor.DroppedOutputLineCount > 0,
            $"Bridge log processing drops: input {_bridgeLogProcessor.DroppedInputChunkCount:N0} chunks / {_bridgeLogProcessor.DroppedInputByteCount:N0} bytes, output {_bridgeLogProcessor.DroppedOutputLineCount:N0} lines (bridge transport unaffected)");
        AddWarningIf(
            warnings,
            _bridgeLogProcessor.DecodeErrorCount > 0,
            $"Bridge Terminal decode errors: {_bridgeLogProcessor.DecodeErrorCount:N0}");
        AddWarningIf(
            warnings,
            BridgePriorityDroppedPipelineChunkCount > 0,
            $"Bridge-priority parser/log chunks dropped: {BridgePriorityDroppedPipelineChunkCount:N0} ({BridgePriorityDroppedPipelineByteCount:N0} bytes; raw bridge prioritized)");

        var decodeErrorCount = _logPipeline.DecodeErrorCount;
        if (!_hasHealthDecodeBaseline || decodeErrorCount > _lastHealthObservedDecodeErrorCount)
        {
            AddWarningIf(warnings, decodeErrorCount > 0, $"Decode errors increased to: {decodeErrorCount:N0}");
        }

        _lastHealthObservedDecodeErrorCount = decodeErrorCount;
        _hasHealthDecodeBaseline = true;

        HealthErrorCount = errors.Count;
        HealthWarningCount = warnings.Count;
        HealthStateText = errors.Count > 0
            ? "ERROR"
            : warnings.Count > 0
                ? "WARNING"
                : "HEALTH OK";

        var reasonLines = errors.Concat(warnings).ToArray();
        HealthReasonsText = reasonLines.Length == 0
            ? "No health issues."
            : string.Join(Environment.NewLine, reasonLines);
        HealthReasonSummary = reasonLines.Length == 0
            ? "No health issues."
            : string.Join("; ", reasonLines.Take(3)) + (reasonLines.Length > 3 ? $"; +{reasonLines.Length - 3:N0} more" : string.Empty);
        LastHealthUpdateTimeText = DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    private static void AddErrorIf(ICollection<string> errors, bool condition, string reason)
    {
        if (condition)
        {
            errors.Add(reason);
        }
    }

    private static void AddWarningIf(ICollection<string> warnings, bool condition, string reason)
    {
        if (condition)
        {
            warnings.Add(reason);
        }
    }

    private void StartResourceSnapshotRefreshIfDue()
    {
        var lastRefreshTicks = Interlocked.Read(ref _lastResourceSnapshotUtcTicks);
        var now = DateTimeOffset.UtcNow;
        if (lastRefreshTicks > 0 &&
            now - new DateTimeOffset(lastRefreshTicks, TimeSpan.Zero) < ResourceSnapshotRefreshInterval)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _resourceSnapshotRefreshInProgress, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _lastResourceSnapshotUtcTicks, now.UtcTicks);
        var saveDirectory = LogSaveDirectory;
        var serialLogPath = CurrentSerialLogPath;
        _ = RefreshResourceSnapshotAsync(saveDirectory, serialLogPath);
    }

    private async Task RefreshResourceSnapshotAsync(
        string saveDirectory,
        string serialLogPath)
    {
        ResourceSnapshot snapshot;
        try
        {
            snapshot = await Task.Run(() => CaptureResourceSnapshot(saveDirectory, serialLogPath));
        }
        catch (Exception ex)
        {
            snapshot = new ResourceSnapshot(0, 0, 0, 0, ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _resourceSnapshotRefreshInProgress, 0);
        }

        RunOnUiThread(() => ApplyResourceSnapshot(snapshot));
    }

    private static ResourceSnapshot CaptureResourceSnapshot(
        string saveDirectory,
        string serialLogPath)
    {
        long diskFreeBytes = 0;
        long diskTotalBytes = 0;
        var errors = new List<string>();

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(saveDirectory));
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new InvalidOperationException("Log drive could not be resolved.");
            }

            var drive = new DriveInfo(root);
            diskFreeBytes = drive.AvailableFreeSpace;
            diskTotalBytes = drive.TotalSize;
        }
        catch (Exception ex)
        {
            errors.Add($"disk: {ex.Message}");
        }

        long sessionLogSizeBytes = 0;
        if (!string.IsNullOrWhiteSpace(serialLogPath))
        {
            try
            {
                var file = new FileInfo(serialLogPath);
                if (file.Exists)
                {
                    sessionLogSizeBytes = file.Length;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"log size: {ex.Message}");
            }
        }

        long processWorkingSetBytes = 0;
        try
        {
            using var process = Process.GetCurrentProcess();
            processWorkingSetBytes = process.WorkingSet64;
        }
        catch (Exception ex)
        {
            errors.Add($"memory: {ex.Message}");
        }

        return new ResourceSnapshot(
            diskFreeBytes,
            diskTotalBytes,
            sessionLogSizeBytes,
            processWorkingSetBytes,
            string.Join("; ", errors));
    }

    private void ApplyResourceSnapshot(ResourceSnapshot snapshot)
    {
        Interlocked.Exchange(ref _diskFreeBytes, Math.Max(0, snapshot.DiskFreeBytes));
        Interlocked.Exchange(ref _diskTotalBytes, Math.Max(0, snapshot.DiskTotalBytes));
        Interlocked.Exchange(ref _currentSessionLogSizeBytes, Math.Max(0, snapshot.CurrentSessionLogSizeBytes));
        Interlocked.Exchange(ref _processWorkingSetBytes, Math.Max(0, snapshot.ProcessWorkingSetBytes));
        _resourceSnapshotError = snapshot.Error;
        _hasResourceSnapshot = true;

        OnPropertyChanged(nameof(DiskFreeBytes));
        OnPropertyChanged(nameof(DiskTotalBytes));
        OnPropertyChanged(nameof(DiskFreePercent));
        OnPropertyChanged(nameof(CurrentSessionLogSizeBytes));
        OnPropertyChanged(nameof(ProcessWorkingSetBytes));
        RefreshDiagnostics();
    }

    private static string FormatByteSize(long bytes)
    {
        var value = Math.Max(0, bytes);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unitIndex = 0;
        var displayValue = (double)value;
        while (displayValue >= 1024 && unitIndex < units.Length - 1)
        {
            displayValue /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:N0} {units[unitIndex]}"
            : $"{displayValue:0.#} {units[unitIndex]}";
    }

    private string CreateLastErrorSummaryText()
    {
        var lastError = CreateLastErrorText();
        if (string.Equals(lastError, "(none)", StringComparison.Ordinal))
        {
            return HealthErrorCount > 0 ? TruncateStatusText(HealthReasonSummary, 120) : string.Empty;
        }

        return TruncateStatusText(lastError, 120);
    }

    private static string TruncateStatusText(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var compact = text.ReplaceLineEndings(" ").Trim();
        return compact.Length <= maxLength
            ? compact
            : compact[..Math.Max(0, maxLength - 3)] + "...";
    }

    private static string NormalizeSelectedSearchText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var compact = text.ReplaceLineEndings(" ").Trim();
        while (compact.Contains("  ", StringComparison.Ordinal))
        {
            compact = compact.Replace("  ", " ", StringComparison.Ordinal);
        }

        return compact.Length <= 200 ? compact : compact[..200];
    }

    private bool IsDiagnosticsTabActive =>
        string.Equals(ActiveInspectorTabText, "Diag", StringComparison.OrdinalIgnoreCase);

    private void RefreshDiagnostics(bool forceDetailed = false)
    {
        var lastRuntimeError = RuntimeDiagnostics.ReadLastError();
        RefreshHealthSummary(lastRuntimeError);
        FooterStatusText = CreateFooterStatus();
        DiagnosticsSummaryText = BuildDiagnosticsSummaryText();
        if (forceDetailed || IsDiagnosticsTabActive)
        {
            DiagnosticsText = BuildDiagnosticsText(lastRuntimeError);
        }
    }

    private string BuildDiagnosticsSummaryText()
    {
        var lastErrorSummary = CreateLastErrorSummaryText();
        if (string.IsNullOrWhiteSpace(lastErrorSummary))
        {
            lastErrorSummary = "(none)";
        }

        return
            $"Health: {HealthStateText} | Last error: {lastErrorSummary}" + Environment.NewLine +
            $"Serial log: {(string.IsNullOrWhiteSpace(CurrentSerialLogPath) ? "(not open)" : CurrentSerialLogPath)}" + Environment.NewLine +
            $"Log Save: {(FileLoggingEnabled ? "ON" : "OFF")} | " +
            $"FileWriter: {(_fileLogWriter.IsRunning ? "running" : "stopped")} | " +
            $"EventDetector: {(_eventDetector.IsRunning ? "running" : "stopped")} | " +
            $"Bridge: {(IsBridgeActive ? $"ON {_bridgeService.VirtualPortName}" : BridgeStateText)} | " +
            $"Mock missing seq: {MockMissingSequenceCount:N0}";
    }

    private string BuildBoundary64KWarningText()
    {
        var warnings = new List<string>();
        AddBoundary64KWarning(warnings, "RX bytes", _serialService.ReceivedByteCount);
        AddBoundary64KWarning(warnings, "RX chunks", _serialService.ReceivedChunkCount);
        AddBoundary64KWarning(warnings, "parsed lines", _logPipeline.ParsedLineCount);
        AddBoundary64KWarning(warnings, "displayed lines", Log.DisplayedLineCount);
        AddBoundary64KWarning(warnings, "file written lines", _fileLogWriter.WrittenLineCount);
        AddBoundary64KWarning(warnings, "detected events", _eventDetector.DetectedEventCount);
        AddBoundary64KWarning(warnings, "visible events", Events.CurrentVisibleEventCount);
        AddBoundary64KWarning(warnings, "xterm appended lines", XtermAppendedLineCount);
        return warnings.Count == 0
            ? "(none)"
            : string.Join("; ", warnings);
    }

    private static void AddBoundary64KWarning(ICollection<string> warnings, string name, long value)
    {
        if (value >= Boundary64KWarningThreshold)
        {
            warnings.Add($"{name} {value:N0}");
        }
    }

    private string BuildDiagnosticsText(string lastRuntimeError)
    {
        var lastShutdown = RuntimeDiagnostics.ReadLastShutdown();
        var builder = new StringBuilder();
        builder.AppendLine($"Updated: {DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"Connection state: {_serialService.ConnectionState}");
        builder.AppendLine($"Is connected: {_serialService.IsConnected}");
        builder.AppendLine($"Status: {StatusText}");
        builder.AppendLine($"Last error: {CreateLastErrorText()}");
        builder.AppendLine($"Diagnostics file: {RuntimeDiagnostics.LastErrorPath}");
        builder.AppendLine($"Last app runtime error: {(string.IsNullOrWhiteSpace(lastRuntimeError) ? "(none)" : "see below")}");
        if (!string.IsNullOrWhiteSpace(lastRuntimeError))
        {
            builder.AppendLine(lastRuntimeError.TrimEnd());
        }

        builder.AppendLine();
        builder.AppendLine("Manual Disconnect Confirmation");
        builder.AppendLine($"  Confirm before disconnect: {ConfirmBeforeDisconnect}");
        builder.AppendLine($"  Last disconnect confirmation result: {LastDisconnectConfirmationResult}");
        builder.AppendLine($"  Disconnect confirmation errors: {DisconnectConfirmationErrorCount:N0}");
        builder.AppendLine($"  Last disconnect confirmation error: {(string.IsNullOrWhiteSpace(LastDisconnectConfirmationError) ? "(none)" : LastDisconnectConfirmationError)}");

        builder.AppendLine();
        builder.AppendLine("Connect Attempts");
        builder.AppendLine($"  Last connect requested port: {LastConnectRequestedPort}");
        builder.AppendLine($"  Last connect requested baud: {LastConnectRequestedBaud:N0}");
        builder.AppendLine($"  Last connect result: {LastConnectResult}");
        builder.AppendLine($"  Last connect failure reason: {(string.IsNullOrWhiteSpace(LastConnectFailureReason) ? "(none)" : LastConnectFailureReason)}");
        builder.AppendLine($"  Last connect exception type: {(string.IsNullOrWhiteSpace(LastConnectExceptionType) ? "(none)" : LastConnectExceptionType)}");
        builder.AppendLine($"  Last connect failure time: {LastConnectFailureTimeText}");
        builder.AppendLine($"  Selected port after failure: {SelectedPortAfterConnectFailure}");

        builder.AppendLine();
        builder.AppendLine("Shutdown");
        builder.AppendLine($"  Last shutdown start time: {LastShutdownStartTimeText}");
        builder.AppendLine($"  Last shutdown completed time: {LastShutdownCompletedTimeText}");
        builder.AppendLine($"  Shutdown cleanup result: {ShutdownCleanupResult}");
        builder.AppendLine($"  Shutdown disconnect error: {(string.IsNullOrWhiteSpace(ShutdownDisconnectError) ? "(none)" : ShutdownDisconnectError)}");
        builder.AppendLine($"  Shutdown file flush error: {(string.IsNullOrWhiteSpace(ShutdownFileFlushError) ? "(none)" : ShutdownFileFlushError)}");
        builder.AppendLine($"  Shutdown profile save error: {(string.IsNullOrWhiteSpace(ShutdownProfileSaveError) ? "(none)" : ShutdownProfileSaveError)}");
        builder.AppendLine($"  Sequence running during shutdown: {WasSequenceRunningDuringShutdown}");
        builder.AppendLine($"  Serial connected during shutdown: {WasSerialConnectedDuringShutdown}");
        builder.AppendLine($"  Last persisted shutdown diagnostics: {(string.IsNullOrWhiteSpace(lastShutdown) ? "(none)" : "see below")}");
        if (!string.IsNullOrWhiteSpace(lastShutdown))
        {
            foreach (var line in lastShutdown.TrimEnd().Split(Environment.NewLine, StringSplitOptions.None))
            {
                builder.AppendLine($"    {line}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Health");
        builder.AppendLine($"  Current health state: {HealthStateText}");
        builder.AppendLine($"  Health warning count: {HealthWarningCount:N0}");
        builder.AppendLine($"  Health error count: {HealthErrorCount:N0}");
        builder.AppendLine($"  Last health update time: {LastHealthUpdateTimeText}");
        builder.AppendLine($"  Log disk free: {(_hasResourceSnapshot && DiskTotalBytes > 0 ? $"{FormatByteSize(DiskFreeBytes)} / {FormatByteSize(DiskTotalBytes)} ({DiskFreePercent:0.#}%)" : "(unavailable)")}");
        builder.AppendLine($"  Current session log size: {(_hasResourceSnapshot ? FormatByteSize(CurrentSessionLogSizeBytes) : "(unavailable)")}");
        builder.AppendLine($"  Process working set: {(_hasResourceSnapshot ? FormatByteSize(ProcessWorkingSetBytes) : "(unavailable)")}");
        builder.AppendLine($"  UI retained logs: {Log.TotalRetainedLineCount:N0}/{Log.Capacity:N0}");
        builder.AppendLine($"  Pending queues (file/event/UI): {_fileLogWriter.PendingRequestCount:N0}/{_eventDetector.PendingInputLineCount:N0}/{TotalPendingUiCount:N0}");
        builder.AppendLine($"  Recorded drops (RX/file/UI): {RecordedRxDropCount:N0}/{_fileLogWriter.DroppedLineCount:N0}/{RecordedUiDropCount:N0}");
        builder.AppendLine($"  Resource snapshot error: {(string.IsNullOrWhiteSpace(_resourceSnapshotError) ? "(none)" : _resourceSnapshotError)}");
        builder.AppendLine("  Health reasons:");
        foreach (var reason in HealthReasonsText.Split(Environment.NewLine, StringSplitOptions.None))
        {
            builder.AppendLine($"    {reason}");
        }

        builder.AppendLine();
        builder.AppendLine("Serial Bridge");
        builder.AppendLine($"  Explicit user-start active: {BridgeRequestedEnabled}");
        builder.AppendLine($"  Active: {IsBridgeActive}");
        builder.AppendLine($"  Route: {BridgeRouteText}");
        builder.AppendLine($"  Device to virtual bytes/chunks: {BridgeDeviceToVirtualByteCount:N0}/{BridgeDeviceToVirtualChunkCount:N0}");
        builder.AppendLine($"  Virtual to device bytes/chunks: {BridgeVirtualToDeviceByteCount:N0}/{BridgeVirtualToDeviceChunkCount:N0}");
        builder.AppendLine($"  Pending device-to-virtual chunks: {BridgePendingDeviceToVirtualChunkCount:N0}");
        builder.AppendLine($"  Pending virtual-to-device chunks: {BridgePendingVirtualToDeviceChunkCount:N0}");
        builder.AppendLine($"  Pending device-to-virtual bytes: {BridgePendingDeviceToVirtualByteCount:N0}");
        builder.AppendLine($"  Pending virtual-to-device bytes: {BridgePendingVirtualToDeviceByteCount:N0}");
        builder.AppendLine($"  Oldest pending bridge chunk age: {BridgeOldestPendingChunkAgeMs:0.###} ms");
        builder.AppendLine($"  Last/max device-to-virtual delay: {_bridgeService.LastDeviceToVirtualDelayMs:0.###}/{_bridgeService.MaxDeviceToVirtualDelayMs:0.###} ms");
        builder.AppendLine($"  Replay late count/max lateness: {_bridgeService.ReplayLateCount:N0}/{_bridgeService.MaxReplayLatenessMs:0.###} ms");
        builder.AppendLine($"  Queue overflow count: {_bridgeService.QueueOverflowCount:N0}");
        builder.AppendLine($"  Last bridge fault: {(_bridgeService.LastFaultReason ?? "(none)")}");
        builder.AppendLine($"  Last bridge activity: {(_bridgeService.LastBridgeActivityAt?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "(none)")}");
        builder.AppendLine($"  Manual TX state/wait/idle guard remaining: {ManualTxState}/{_bridgeService.ManualTxWaitMs:0.###}/{_bridgeService.ManualTxIdleGuardRemainingMs:0.###} ms");
        builder.AppendLine($"  Dropped device-to-virtual bytes/chunks: {BridgeDroppedDeviceToVirtualByteCount:N0}/{BridgeDroppedDeviceToVirtualChunkCount:N0}");
        builder.AppendLine($"  Dropped virtual-to-device bytes/chunks: {BridgeDroppedVirtualToDeviceByteCount:N0}/{BridgeDroppedVirtualToDeviceChunkCount:N0}");
        builder.AppendLine($"  Errors: {BridgeErrorCount:N0}");
        builder.AppendLine($"  UI-only bridge log pending/dropped: {BridgeVisualLogPendingCount:N0}/{BridgeVisualLogDroppedCount:N0}");
        builder.AppendLine("  UI-only bridge log drops do not affect raw bridge transport.");
        builder.AppendLine($"  Bridge log input pending/dropped chunks/bytes: {_bridgeLogProcessor.PendingInputChunkCount:N0}/{_bridgeLogProcessor.DroppedInputChunkCount:N0}/{_bridgeLogProcessor.DroppedInputByteCount:N0}");
        builder.AppendLine($"  Bridge log output dropped lines: {_bridgeLogProcessor.DroppedOutputLineCount:N0}");
        builder.AppendLine($"  Bridge Terminal decode errors: {_bridgeLogProcessor.DecodeErrorCount:N0}");
        builder.AppendLine($"  Bridge log processor errors: {_bridgeLogProcessor.ErrorCount:N0}");
        builder.AppendLine("  Bridge log processing drops do not affect raw bridge transport.");
        builder.AppendLine($"  Raw bridge priority enabled: {IsRawBridgePriorityEnabled}");
        builder.AppendLine($"  Bridge-priority parser/log dropped bytes/chunks: {BridgePriorityDroppedPipelineByteCount:N0}/{BridgePriorityDroppedPipelineChunkCount:N0}");
        builder.AppendLine("  Bridge-priority parser/log drops do not affect raw bridge transport.");
        builder.AppendLine($"  Last error: {(string.IsNullOrWhiteSpace(BridgeLastError) ? "(none)" : BridgeLastError)}");

        builder.AppendLine();
        builder.AppendLine("Log File Actions");
        builder.AppendLine($"  Current serial log path: {(string.IsNullOrWhiteSpace(CurrentSerialLogPath) ? "(not open)" : CurrentSerialLogPath)}");
        builder.AppendLine($"  Last log file action status: {LastLogFileActionStatus}");
        builder.AppendLine($"  Log file action error count: {LogFileActionErrorCount:N0}");
        builder.AppendLine($"  Last log file action error: {(string.IsNullOrWhiteSpace(LastLogFileActionError) ? "(none)" : LastLogFileActionError)}");
        builder.AppendLine($"  Requested log file name: {(string.IsNullOrWhiteSpace(LogFileName) ? "(none)" : LogFileName)}");
        builder.AppendLine($"  Active log file name: {CurrentSessionDisplayText}");
        builder.AppendLine($"  Active exact log file name: {(string.IsNullOrWhiteSpace(ActiveLogFileName) ? "(none)" : ActiveLogFileName)}");
        builder.AppendLine($"  Last log file naming action: {LastSessionFileAction}");
        builder.AppendLine($"  Log file naming errors: {SessionFileNamingErrorCount:N0}");
        builder.AppendLine($"  Last log file naming error: {(string.IsNullOrWhiteSpace(LastSessionFileNamingError) ? "(none)" : LastSessionFileNamingError)}");
        builder.AppendLine($"  Last save directory action: {LastSaveDirectoryAction}");
        builder.AppendLine($"  Save directory browse errors: {SaveDirectoryBrowseErrorCount:N0}");
        builder.AppendLine($"  Last save directory error: {(string.IsNullOrWhiteSpace(LastSaveDirectoryError) ? "(none)" : LastSaveDirectoryError)}");

        builder.AppendLine();
        builder.AppendLine("Log Save");
        builder.AppendLine($"  File logging enabled: {FileLoggingEnabled}");
        builder.AppendLine($"  File logging active: {FileLoggingActive}");
        builder.AppendLine($"  Current serial log path: {(string.IsNullOrWhiteSpace(CurrentSerialLogPath) ? "(not open)" : CurrentSerialLogPath)}");
        builder.AppendLine($"  Last log toggle action: {LastLogToggleAction}");
        builder.AppendLine($"  Last log toggle time: {LastLogToggleTimeText}");
        builder.AppendLine($"  Log toggle errors: {LogToggleErrorCount:N0}");
        builder.AppendLine($"  Last log toggle error: {(string.IsNullOrWhiteSpace(LastLogToggleError) ? "(none)" : LastLogToggleError)}");

        builder.AppendLine();
        builder.AppendLine("Profile");
        builder.AppendLine($"  Current profile path: {CurrentProfilePath}");
        builder.AppendLine($"  Profile schema version: {ProfileSchemaVersion:N0}");
        builder.AppendLine($"  Profile loaded: {ProfileLoaded}");
        builder.AppendLine($"  Profile load time: {ProfileLoadTimeText}");
        builder.AppendLine($"  Profile save time: {ProfileSaveTimeText}");
        builder.AppendLine($"  Profile load count: {ProfileLoadCount:N0}");
        builder.AppendLine($"  Profile save count: {ProfileSaveCount:N0}");
        builder.AppendLine($"  Profile status: {ProfileStatusText}");
        builder.AppendLine($"  Profile last error: {(string.IsNullOrWhiteSpace(ProfileLastError) ? "(none)" : ProfileLastError)}");
        builder.AppendLine($"  Profile load error count: {ProfileLoadErrorCount:N0}");
        builder.AppendLine($"  Profile save error count: {ProfileSaveErrorCount:N0}");
        builder.AppendLine($"  Profile normalization count: {ProfileNormalizationCount:N0}");
        builder.AppendLine($"  Profile file logging enabled: {_currentLogSettings.FileLoggingEnabled}");
        builder.AppendLine($"  Profile log save directory: {_currentLogSettings.SaveDirectory}");
        builder.AppendLine("  Date-based serial log rotation: disabled");
        builder.AppendLine($"  Profile size rotation enabled: {_currentLogSettings.SizeRotationEnabled}");
        builder.AppendLine($"  Profile last successful port: {LastSuccessfulPort}");
        builder.AppendLine($"  Profile last successful baud: {LastSuccessfulBaudRate:N0}");
        builder.AppendLine($"  Profile max visible log lines: {_currentUiSettings.MaxVisibleLogLines:N0}");
        builder.AppendLine($"  Profile max visible event count: {_currentUiSettings.MaxVisibleEventCount:N0}");
        builder.AppendLine($"  Profile xterm scrollback size: {_currentUiSettings.XtermScrollbackSize:N0}");
        builder.AppendLine($"  Profile auto-scroll enabled: {_currentUiSettings.AutoScrollEnabled}");
        builder.AppendLine($"  Profile file logging while view paused: {_currentUiSettings.FileLoggingWhileViewPaused}");
        builder.AppendLine($"  Profile confirm before disconnect: {_currentUiSettings.ConfirmBeforeDisconnect}");
        builder.AppendLine($"  Profile show timestamp in log view: {_currentUiSettings.ShowTimestampInLogView}");
        builder.AppendLine($"  Profile timestamp display format: {LogLine.GetTimestampFormatPattern(_currentUiSettings.TimestampDisplayFormat)}");
        builder.AppendLine("  Rule changes apply to new logs only: True (fixed)");
        builder.AppendLine($"  Profile RX display mode: {FormatRxDisplayModeName(_currentUiSettings.RxDisplayMode)}");
        builder.AppendLine($"  Profile HEX group timeout: {_currentUiSettings.HexGroupTimeoutMs:N0} ms");
        builder.AppendLine($"  Profile TX send mode: {FormatTxSendModeName(_currentUiSettings.TxSendMode)}");
        builder.AppendLine($"  Profile cute background mode: {_currentUiSettings.CuteBackgroundMode}");
        builder.AppendLine($"  Profile custom cute background image path: {(string.IsNullOrWhiteSpace(_currentUiSettings.CuteBackgroundImagePath) ? "(none)" : _currentUiSettings.CuteBackgroundImagePath)}");
        builder.AppendLine($"  Profile cute background opacity: {_currentUiSettings.CuteBackgroundOpacity:0.00}");
        builder.AppendLine($"  Profile custom cute background path cleared by migration: {ProfileCuteBackgroundCustomPathCleared}");
        builder.AppendLine($"  Profile custom cute background path clear reason: {ProfileCuteBackgroundCustomPathClearReason}");
        builder.AppendLine($"  Profile show MOCK test port: {_currentUiSettings.ShowMockTestPort}");
        builder.AppendLine($"  Profile event auto-scroll enabled: {_currentUiSettings.EventAutoScrollEnabled}");
        builder.AppendLine($"  Profile search case sensitive: {_currentUiSettings.SearchCaseSensitive}");
        builder.AppendLine("  Search result refresh mode: Manual only");
        builder.AppendLine($"  Profile last search text: {(string.IsNullOrWhiteSpace(_currentUiSettings.LastSearchText) ? "(none)" : _currentUiSettings.LastSearchText)}");
        builder.AppendLine($"  Profile marker text: {(string.IsNullOrWhiteSpace(_currentUiSettings.MarkerText) ? "(none)" : _currentUiSettings.MarkerText)}");
        builder.AppendLine($"  Profile mock lines/sec: {_currentUiSettings.MockStressLinesPerSecond:N0}");
        builder.AppendLine($"  Profile mock burst size: {_currentUiSettings.MockStressBurstSize:N0}");
        builder.AppendLine($"  Profile mock pattern: {FormatMockGeneratorPatternName(_currentUiSettings.MockGeneratorPattern)}");
        builder.AppendLine($"  Profile mock event injection enabled: {_currentUiSettings.MockStressEventInjectionEnabled}");
        builder.AppendLine($"  Profile mock invalid byte injection enabled: {_currentUiSettings.MockStressInvalidByteInjectionEnabled}");
        builder.AppendLine($"  Event before context lines: {_currentEventContextSettings.BeforeContextLines:N0}");
        builder.AppendLine($"  Event after context lines: {_currentEventContextSettings.AfterContextLines:N0}");
        builder.AppendLine($"  Saved commands in profile: {Commands.SavedCommands.Count:N0}");
        builder.AppendLine($"  Command history in profile: {Commands.CommandHistoryCount:N0}");
        builder.AppendLine($"  Command sequences in profile: {CommandSequences.Count:N0}");
        builder.AppendLine($"  Unified log rules in profile: {LogRules.Count:N0}");
        builder.AppendLine($"  Terminal log rules in profile: {TerminalLogRuleCount:N0}");
        builder.AppendLine($"  HEX log rules in profile: {HexLogRuleCount:N0}");
        builder.AppendLine($"  Invalid HEX log rules in profile: {InvalidHexLogRuleCount:N0}");
        builder.AppendLine($"  Event rule projections in profile: {EventRules.Count:N0}");
        builder.AppendLine($"  Highlight rule projections in profile: {HighlightRules.Count:N0}");
        builder.AppendLine($"  Rule migration result: {RuleMigrationResult}");
        builder.AppendLine($"  Last settings change: {LastSettingsChange}");
        builder.AppendLine($"  Last settings apply status: {LastSettingsApplyStatus}");
        builder.AppendLine($"  Pending reconnect-required settings: {PendingReconnectRequiredSettingsCount:N0}");
        builder.AppendLine($"  Pending restart-required settings: {PendingRestartRequiredSettingsCount:N0}");
        builder.AppendLine($"  Settings apply errors: {SettingsApplyErrorCount:N0}");
        builder.AppendLine($"  Last settings apply error: {(string.IsNullOrWhiteSpace(LastSettingsApplyError) ? "(none)" : LastSettingsApplyError)}");
        builder.AppendLine($"  Settings validation errors: {SettingsValidationErrorCount:N0}");
        builder.AppendLine($"  Last settings validation error: {(string.IsNullOrWhiteSpace(LastSettingsValidationError) ? "(none)" : LastSettingsValidationError)}");
        builder.AppendLine($"  Last normalized setting: {LastNormalizedSetting}");
        builder.AppendLine();
        builder.AppendLine("Rule / Command Editors");
        builder.AppendLine($"  Unified log rule count: {LogRuleEditorCount:N0}");
        builder.AppendLine($"  Rules used for events: {ActiveEventLogRuleCount:N0}");
        builder.AppendLine($"  Rules used for highlights: {ActiveHighlightRuleCount:N0}");
        builder.AppendLine($"  Rules used for filters: {ActiveViewFilterRuleCount:N0}");
        builder.AppendLine($"  Terminal mode rule count: {TerminalLogRuleCount:N0}");
        builder.AppendLine($"  HEX mode rule count: {HexLogRuleCount:N0}");
        builder.AppendLine($"  Invalid HEX rule count: {InvalidHexLogRuleCount:N0}");
        builder.AppendLine($"  Last invalid HEX rule: {(string.IsNullOrWhiteSpace(LastInvalidHexLogRuleName) ? "(none)" : LastInvalidHexLogRuleName)}");
        builder.AppendLine($"  Last invalid HEX rule error: {(string.IsNullOrWhiteSpace(LastInvalidHexLogRuleError) ? "(none)" : LastInvalidHexLogRuleError)}");
        builder.AppendLine($"  Rule evaluation errors: {_eventDetector.RuleEvaluationErrorCount:N0}");
        builder.AppendLine($"  Last rule evaluation error: {(string.IsNullOrWhiteSpace(_eventDetector.LastRuleEvaluationError) ? "(none)" : _eventDetector.LastRuleEvaluationError)}");
        builder.AppendLine($"  Last HEX rule match: {(string.IsNullOrWhiteSpace(_eventDetector.LastHexRuleMatchName) ? "(none)" : _eventDetector.LastHexRuleMatchName)}");
        builder.AppendLine($"  Last HEX rule match bytes: {(string.IsNullOrWhiteSpace(_eventDetector.LastHexRuleMatchBytesPreview) ? "(none)" : _eventDetector.LastHexRuleMatchBytesPreview)}");
        builder.AppendLine("  Apply rules to new logs only: True (fixed)");
        builder.AppendLine($"  Automatic rule re-render suppressed count: {AutomaticRuleRerenderSuppressedCount:N0}");
        builder.AppendLine($"  Last live-only rule change time: {LastRuleChangeLiveOnlyTimeText}");
        builder.AppendLine($"  Rule changes since clear: {RuleChangesSinceClearCount:N0}");
        builder.AppendLine("  New rule Event default: false");
        builder.AppendLine($"  Unified log rule color count: {UnifiedLogRuleColorCount:N0}");
        builder.AppendLine($"  Invalid rule color fallbacks: {InvalidRuleColorFallbackCount:N0}");
        builder.AppendLine($"  Event rule projection count: {EventRules.Count:N0}");
        builder.AppendLine($"  Highlight rule projection count: {HighlightRules.Count:N0}");
        builder.AppendLine($"  Saved command count: {SavedCommandEditorCount:N0}");
        builder.AppendLine($"  Command sequence count: {CommandSequenceCount:N0}");
        builder.AppendLine($"  Selected log rule: {SelectedLogRule?.Name ?? "(none)"}");
        builder.AppendLine($"  Selected saved command: {SelectedSavedCommand?.Name ?? "(none)"}");
        builder.AppendLine($"  Selected saved command shortcut: {SelectedSavedCommand?.OptionalShortcut ?? "(none)"}");
        builder.AppendLine($"  Selected sequence: {SelectedCommandSequenceName}");
        builder.AppendLine($"  Selected sequence steps: {SelectedCommandSequenceStepCount:N0}");
        builder.AppendLine($"  Last rule edit status: {LastRuleEditStatus}");
        builder.AppendLine($"  Last rule edit error: {(string.IsNullOrWhiteSpace(LastRuleEditError) ? "(none)" : LastRuleEditError)}");
        builder.AppendLine($"  Rule edit errors: {RuleEditErrorCount:N0}");
        builder.AppendLine($"  Last rule color change: {LastRuleColorChange}");
        builder.AppendLine($"  Rule color change errors: {RuleColorChangeErrorCount:N0}");
        builder.AppendLine($"  Last rule color change error: {(string.IsNullOrWhiteSpace(LastRuleColorChangeError) ? "(none)" : LastRuleColorChangeError)}");
        builder.AppendLine($"  Last command edit status: {LastCommandEditStatus}");
        builder.AppendLine($"  Last command edit error: {(string.IsNullOrWhiteSpace(LastCommandEditError) ? "(none)" : LastCommandEditError)}");
        builder.AppendLine($"  Command edit errors: {CommandEditErrorCount:N0}");
        builder.AppendLine();
        builder.AppendLine("Serial");
        builder.AppendLine($"  Serial connection state: {_serialService.ConnectionState}");
        builder.AppendLine($"  ViewModel is connected: {IsConnected}");
        builder.AppendLine($"  ViewModel is busy: {IsBusy}");
        builder.AppendLine($"  Selected port: {SelectedPort ?? "(none)"}");
        builder.AppendLine($"  Selected port available: {SelectedPortAvailable}");
        builder.AppendLine($"  Last successful port: {LastSuccessfulPort}");
        builder.AppendLine($"  Last successful baud: {LastSuccessfulBaudRate:N0}");
        builder.AppendLine($"  Last port selection change reason: {LastPortSelectionChangeReason}");
        builder.AppendLine($"  Last disconnect preserved port: {LastDisconnectPreservedPort}");
        builder.AppendLine($"  Last port refresh result: {LastPortRefreshResult}");
        builder.AppendLine($"  Show MOCK test port: {ShowMockTestPort}");
        builder.AppendLine($"  Current port is MOCK: {CurrentPortIsMock}");
        builder.AppendLine($"  Last port refresh included MOCK: {LastPortRefreshIncludedMock}");
        builder.AppendLine($"  Received bytes: {_serialService.ReceivedByteCount:N0}");
        builder.AppendLine($"  Received chunks: {_serialService.ReceivedChunkCount:N0}");
        builder.AppendLine($"  Active log observer task count: {ActiveLogObserverTaskCount:N0}");
        builder.AppendLine($"  Active event observer task count: {ActiveEventObserverTaskCount:N0}");
        builder.AppendLine($"  Active event context observer task count: {ActiveEventContextObserverTaskCount:N0}");
        builder.AppendLine($"  Connection errors: {_serialService.ConnectionErrorCount:N0}");
        builder.AppendLine($"  Serial last error: {_serialService.LastError ?? "(none)"}");
        builder.AppendLine($"  Duplicate MOCK port entries removed: {DuplicateMockPortEntryCount:N0}");
        builder.AppendLine();
        builder.AppendLine("Mock Stress / Sequence Verification");
        builder.AppendLine($"  Stress running: {IsMockStressRunning}");
        builder.AppendLine($"  Stress status: {_serialService.MockStressStatus}");
        builder.AppendLine($"  Mock pattern: {SelectedMockGeneratorPatternText}");
        builder.AppendLine($"  No-newline mock active: {IsMockNoNewlineActive}");
        builder.AppendLine($"  No-newline mock emitted bytes: {MockNoNewlineEmittedBytes:N0}");
        builder.AppendLine($"  Selected lines/sec: {SelectedMockStressLinesPerSecond:N0}");
        builder.AppendLine($"  Selected burst size: {SelectedMockStressBurstSize:N0}");
        builder.AppendLine($"  ERROR/WARN/FAULT injection enabled: {IsMockStressEventInjectionEnabled}");
        builder.AppendLine($"  Invalid byte injection enabled: {IsMockStressInvalidByteInjectionEnabled}");
        builder.AppendLine($"  Mock generated lines/packets: {MockGeneratedLineCount:N0}");
        builder.AppendLine($"  Mock expected sequence: {MockExpectedSequence:N0}");
        builder.AppendLine($"  Mock last generated sequence: {MockLastGeneratedSequence:N0}");
        builder.AppendLine($"  Mock last parsed sequence: {MockLastParsedSequence:N0}");
        builder.AppendLine($"  Missing sequence count: {MockMissingSequenceCount:N0}");
        builder.AppendLine($"  Duplicate sequence count: {MockDuplicateSequenceCount:N0}");
        builder.AppendLine($"  Out-of-order sequence count: {MockOutOfOrderSequenceCount:N0}");
        builder.AppendLine($"  Malformed sequence line count: {MockMalformedSequenceCount:N0}");
        builder.AppendLine($"  Last sequence error: {(string.IsNullOrWhiteSpace(LastMockSequenceError) ? "(none)" : LastMockSequenceError)}");
        builder.AppendLine($"  RX bytes: {_serialService.ReceivedByteCount:N0}");
        builder.AppendLine($"  RX chunks: {_serialService.ReceivedChunkCount:N0}");
        builder.AppendLine($"  Pipeline processed RX bytes: {_logPipeline.ProcessedRxByteCount:N0}");
        builder.AppendLine($"  Parsed lines: {_logPipeline.ParsedLineCount:N0}");
        builder.AppendLine($"  Displayed lines: {Log.DisplayedLineCount:N0}");
        builder.AppendLine($"  File written lines: {_fileLogWriter.WrittenLineCount:N0}");
        builder.AppendLine($"  Events detected: {_eventDetector.DetectedEventCount:N0}");
        builder.AppendLine($"  64K boundary warning: {BuildBoundary64KWarningText()}");
        builder.AppendLine("  64K counter audit: no app log/event counters use 16-bit integer types; GetKeyState interop uses Win32 short only.");
        builder.AppendLine($"  Dropped UI visible lines: {Log.DroppedVisibleLineCount:N0}");
        builder.AppendLine($"  Dropped UI pending lines: {Log.DroppedPendingLineCount:N0}");
        builder.AppendLine($"  Pending visual line count: {PendingVisualLineCount:N0}");
        builder.AppendLine($"  View pause active: {IsLogRenderingPaused}");
        builder.AppendLine($"  Current pause skip (PS) lines: {CurrentViewPauseOmittedLineCount:N0}");
        builder.AppendLine($"  Total pause skip (PS) lines: {TotalViewPauseOmittedLineCount:N0}");
        builder.AppendLine($"  View pause count: {ViewPauseCount:N0}");
        builder.AppendLine($"  File logging while view paused: {FileLoggingWhileViewPaused}");
        builder.AppendLine($"  Last view-pause summary: {LastViewPauseSummary}");
        builder.AppendLine($"  File writer dropped lines: {_fileLogWriter.DroppedLineCount:N0}");
        builder.AppendLine($"  Event detector dropped input lines: {_eventDetector.DroppedInputLineCount:N0}");
        builder.AppendLine($"  Event detector dropped output events: {_eventDetector.DroppedOutputEventCount:N0}");
        builder.AppendLine($"  Event detector failed contexts: {_eventDetector.ContextCaptureFailedCount:N0}");
        builder.AppendLine();
        builder.AppendLine("TX");
        builder.AppendLine($"  Selected TX line ending: {SelectedTxLineEnding}");
        builder.AppendLine($"  TX send mode: {FormatTxSendModeName(SelectedTxSendMode)}");
        builder.AppendLine($"  Last TX mode: {FormatTxSendModeName(LastTxMode)}");
        builder.AppendLine($"  Last TX raw input: {(string.IsNullOrWhiteSpace(LastTxRawInput) ? "(none)" : LastTxRawInput)}");
        builder.AppendLine($"  Last TX byte count: {LastTxByteCount:N0}");
        builder.AppendLine($"  Last TX HEX parse error: {(string.IsNullOrWhiteSpace(LastTxHexParseError) ? "(none)" : LastTxHexParseError)}");
        builder.AppendLine($"  Sent command count: {SentCommandCount:N0}");
        builder.AppendLine($"  Last sent command text: {(string.IsNullOrWhiteSpace(LastSentCommandText) ? "(none)" : LastSentCommandText)}");
        builder.AppendLine($"  Last sent command time: {LastSentCommandTimeText}");
        builder.AppendLine($"  TX written bytes: {_serialService.WrittenByteCount:N0}");
        builder.AppendLine($"  TX error count: {TxErrorCount:N0}");
        builder.AppendLine($"  Last TX error: {(string.IsNullOrWhiteSpace(LastTxError) ? "(none)" : LastTxError)}");
        builder.AppendLine($"  Command history count: {Commands.CommandHistoryCount:N0}");
        builder.AppendLine($"  Last history command: {(string.IsNullOrWhiteSpace(Commands.LastHistoryCommand) ? "(none)" : Commands.LastHistoryCommand)}");
        builder.AppendLine($"  Last history update time: {Commands.LastHistoryUpdateTimeText}");
        builder.AppendLine($"  History max count: {Commands.HistoryMaxCount:N0}");
        builder.AppendLine($"  History errors: {Commands.HistoryErrorCount:N0}");
        builder.AppendLine($"  Last history error: {(string.IsNullOrWhiteSpace(Commands.LastHistoryError) ? "(none)" : Commands.LastHistoryError)}");
        builder.AppendLine();
        builder.AppendLine("Command Sequences");
        builder.AppendLine($"  Sequence count: {CommandSequenceCount:N0}");
        builder.AppendLine($"  Selected sequence: {SelectedCommandSequenceName}");
        builder.AppendLine($"  Running: {IsSequenceRunning}");
        builder.AppendLine($"  Running sequence: {RunningSequenceName}");
        builder.AppendLine($"  Current step: {CurrentSequenceStepText}");
        builder.AppendLine($"  Completed steps: {CompletedSequenceSteps:N0}");
        builder.AppendLine($"  Last sequence action status: {LastSequenceActionStatus}");
        builder.AppendLine($"  Sequence run count: {SequenceRunCount:N0}");
        builder.AppendLine($"  Sequence stop count: {SequenceStopCount:N0}");
        builder.AppendLine($"  Sequence errors: {SequenceErrorCount:N0}");
        builder.AppendLine($"  Last sequence error: {(string.IsNullOrWhiteSpace(LastSequenceError) ? "(none)" : LastSequenceError)}");
        builder.AppendLine();
        builder.AppendLine("Markers");
        builder.AppendLine($"  Marker count: {MarkerCount:N0}");
        builder.AppendLine($"  Last marker action: {LastMarkerAction}");
        builder.AppendLine($"  Last marker text: {(string.IsNullOrWhiteSpace(LastMarkerText) ? "(none)" : LastMarkerText)}");
        builder.AppendLine($"  Last marker time: {LastMarkerTimeText}");
        builder.AppendLine($"  Marker insert errors: {MarkerInsertErrorCount:N0}");
        builder.AppendLine($"  Last marker error: {(string.IsNullOrWhiteSpace(LastMarkerError) ? "(none)" : LastMarkerError)}");
        builder.AppendLine();
        builder.AppendLine("Pipeline");
        builder.AppendLine($"  Parsed lines: {_logPipeline.ParsedLineCount:N0}");
        builder.AppendLine($"  Decode errors: {_logPipeline.DecodeErrorCount:N0}");
        builder.AppendLine($"  Partial line buffer length: {_logPipeline.PartialLineBufferLength:N0}");
        builder.AppendLine($"  Max partial line buffer length: {_logPipeline.MaxPartialLineBufferLength:N0}");
        builder.AppendLine($"  Partial RX flush count: {_logPipeline.PartialRxFlushCount:N0}");
        builder.AppendLine($"  Last partial RX flush time: {_logPipeline.LastPartialRxFlushTimeText}");
        builder.AppendLine($"  No-newline RX detected: {_logPipeline.NoNewlineRxDetected}");
        builder.AppendLine($"  Last partial finalized by newline: {_logPipeline.LastPartialFinalizedByNewline}");
        builder.AppendLine($"  Partial duplicate suppression count: {_logPipeline.PartialDuplicateSuppressionCount:N0}");
        builder.AppendLine($"  Last RX chunk bytes: {_logPipeline.LastRxChunkBytes:N0}");
        var lastRxChunkGapTicks = _logPipeline.LastRxChunkGapTicks;
        builder.AppendLine($"  Last RX chunk gap: {(lastRxChunkGapTicks < 0 ? "(none)" : $"{TimeSpan.FromTicks(lastRxChunkGapTicks).TotalMilliseconds:0.###} ms")}");
        builder.AppendLine($"  Last RX raw bytes hex preview: {(string.IsNullOrWhiteSpace(_logPipeline.LastRxRawBytesHexPreview) ? "(none)" : _logPipeline.LastRxRawBytesHexPreview)}");
        builder.AppendLine($"  Last RX chunk had newline: {_logPipeline.LastRxChunkHadNewline}");
        builder.AppendLine($"  Last RX contained TAB byte: {_logPipeline.LastRxContainedTabByte}");
        builder.AppendLine($"  Last RX contained literal backslash-t: {_logPipeline.LastRxContainedLiteralBackslashT}");
        builder.AppendLine($"  Last RX chunk parsed lines: {_logPipeline.LastRxChunkParsedLines:N0}");
        builder.AppendLine($"  Max RX chunk parsed lines: {_logPipeline.MaxRxChunkParsedLines:N0}");
        builder.AppendLine($"  Driver-reported F/P/overrun/RX-buffer-warning signals: {_serialService.SerialFrameErrorCount:N0}/{_serialService.SerialParityErrorCount:N0}/{_serialService.SerialOverrunErrorCount:N0}/{_serialService.SerialRxOverErrorCount:N0}");
        builder.AppendLine($"  Last driver-reported line-status signal: {_serialService.LastSerialErrorSummary}");
        builder.AppendLine($"  Native idle boundaries suppressed by line-status errors: {_serialService.SerialLineErrorBoundarySuppressionCount:N0}");
        builder.AppendLine($"  Native RX idle timeout enabled: {_serialService.UsesNativeReceiveIdleTimeout}");
        builder.AppendLine($"  Native RX idle timeout applied: {(_serialService.UsesNativeReceiveIdleTimeout ? $"{_serialService.AppliedReceiveIdleTimeoutMs:N0} ms" : "immediate drain")}");
        builder.AppendLine();
        builder.AppendLine("UI Log");
        builder.AppendLine($"  Active log view mode: {ActiveLogViewModeText}");
        builder.AppendLine($"  Smooth rendering enabled: {SmoothLogRenderingEnabled}");
        builder.AppendLine($"  Visual render interval ms: {VisualRenderIntervalMs:N0}");
        builder.AppendLine($"  Visual drain batch size: {VisualDrainBatchSize:N0}");
        builder.AppendLine($"  Visual drain max chars: {VisualDrainMaxChars:N0}");
        builder.AppendLine($"  Show timestamp in log view: {ShowTimestampInLogView}");
        builder.AppendLine($"  RX display mode: {FormatRxDisplayModeName(SelectedRxDisplayMode)}");
        builder.AppendLine($"  HEX group timeout profile/app/native: {HexGroupTimeoutMs:N0}/{_logPipeline.HexGroupTimeoutMs:N0}/{(_serialService.UsesNativeReceiveIdleTimeout ? _serialService.AppliedReceiveIdleTimeoutMs : 0):N0} ms");
        builder.AppendLine($"  HEX timeout recommendation: {HexGroupTimeoutRecommendationText}");
        builder.AppendLine($"  HEX pending bytes: {_logPipeline.HexPendingByteCount:N0}");
        builder.AppendLine($"  HEX accepted/emitted bytes: {_logPipeline.HexAcceptedByteCount:N0}/{_logPipeline.HexEmittedByteCount:N0}");
        builder.AppendLine($"  HEX byte conservation delta: {_logPipeline.HexAcceptedByteCount - _logPipeline.HexEmittedByteCount - _logPipeline.HexPendingByteCount:N0}");
        builder.AppendLine($"  Last HEX group bytes: {_logPipeline.LastHexGroupByteCount:N0}");
        builder.AppendLine($"  HEX group flush count: {_logPipeline.HexGroupFlushCount:N0}");
        builder.AppendLine($"  Last HEX group flush: {_logPipeline.LastHexGroupFlushTimeText}");
        builder.AppendLine($"  Partial RX visual line active: {Log.PartialRxVisualLineActive}");
        builder.AppendLine($"  Partial RX visual length: {Log.PartialRxVisualLength:N0}");
        builder.AppendLine($"  Partial RX append-in-place count: {Log.PartialRxAppendInPlaceCount:N0}");
        builder.AppendLine("  RX visual formatting mode: terminal tabs; CR/LF/ESC/other controls are shown as safe text");
        builder.AppendLine($"  RX render mode errors: {Log.XtermFormattingErrorCount:N0}");
        builder.AppendLine($"  RX control character format errors: {Log.XtermFormattingErrorCount:N0}");
        builder.AppendLine($"  Cute background enabled: {CuteBackgroundMode}");
        builder.AppendLine($"  Cute background source: {CuteBackgroundSource}");
        builder.AppendLine($"  Custom background image path: {(string.IsNullOrWhiteSpace(CuteBackgroundImagePath) ? "(none)" : CuteBackgroundImagePath)}");
        builder.AppendLine($"  Bundled background path: {(string.IsNullOrWhiteSpace(CuteBackgroundBundledPath) ? "(none)" : CuteBackgroundBundledPath)}");
        builder.AppendLine($"  Cute background file exists: {CuteBackgroundFileExists}");
        builder.AppendLine($"  Cute background loaded: {CuteBackgroundLoaded}");
        builder.AppendLine($"  Cute background opacity: {CuteBackgroundOpacity:0.00}");
        builder.AppendLine($"  Cute background dark overlay opacity: {CuteBackgroundOverlayOpacity:0.00}");
        builder.AppendLine($"  Last cute background visual update: {CuteBackgroundLastAppliedTimeText}");
        builder.AppendLine($"  Cute background apply count: {CuteBackgroundApplyCount:N0}");
        builder.AppendLine($"  Cute background image reload count: {CuteBackgroundImageReloadCount:N0}");
        builder.AppendLine($"  Cute background skipped unchanged count: {CuteBackgroundSkippedUnchangedCount:N0}");
        builder.AppendLine("  Cute background flicker prevention active: True");
        builder.AppendLine($"  Cute background load error: {(string.IsNullOrWhiteSpace(CuteBackgroundLoadError) ? "(none)" : CuteBackgroundLoadError)}");
        builder.AppendLine($"  Last timestamp display mode change time: {LastTimestampDisplayModeChangeTimeText}");
        builder.AppendLine($"  Timestamp display mode errors: {TimestampDisplayModeErrorCount:N0}");
        builder.AppendLine($"  Last timestamp display mode error: {(string.IsNullOrWhiteSpace(LastTimestampDisplayModeError) ? "(none)" : LastTimestampDisplayModeError)}");
        builder.AppendLine($"  xterm ready: {IsXtermReady}");
        builder.AppendLine($"  Rendering paused: {IsLogRenderingPaused}");
        builder.AppendLine($"  Rendering pause reason: {RenderingPauseReason}");
        builder.AppendLine($"  Manual pause active: {IsManualLogRenderingPaused}");
        builder.AppendLine($"  xterm append backpressure active: {IsXtermAppendBackpressureActive}");
        builder.AppendLine($"  xterm backlog UI relief active: {IsXtermAppendBackpressureActive}");
        builder.AppendLine($"  Effective xterm auto-scroll: {IsEffectiveXtermAutoScrollEnabled}");
        builder.AppendLine("  Search auto-refresh: disabled (manual only)");
        builder.AppendLine($"  Event auto-scroll suppressed by xterm backlog: {IsEventAutoScrollSuppressedByXtermBackpressure}");
        builder.AppendLine($"  Backlog event auto-scroll suppressions: {XtermBackpressureEventAutoScrollSuppressedCount:N0}");
        builder.AppendLine($"  Backlog xterm auto-scroll suppressions: {XtermBackpressureAutoScrollSuppressedCount:N0}");
        builder.AppendLine($"  Backlog full re-renders deferred: {XtermBackpressureFullRerenderDeferredCount:N0}");
        builder.AppendLine($"  Window minimized: {IsWindowMinimized}");
        builder.AppendLine($"  Visual append suspended by minimize: {IsVisualAppendSuspendedForMinimize}");
        builder.AppendLine($"  xterm needs full re-render after restore: {XtermNeedsFullRerenderAfterRestore}");
        builder.AppendLine($"  Last minimize time: {LastWindowMinimizeTimeText}");
        builder.AppendLine($"  Last restore time: {LastWindowRestoreTimeText}");
        builder.AppendLine($"  Restore render started time: {RestoreRenderStartedTimeText}");
        builder.AppendLine($"  Restore render completed time: {RestoreRenderCompletedTimeText}");
        builder.AppendLine($"  Restore mode: {LastRestoreRenderMode}");
        builder.AppendLine($"  Restore rendered line count: {RestoreRenderedLineCount:N0}");
        builder.AppendLine($"  Restore render duration: {RestoreRenderDurationText}");
        builder.AppendLine($"  Restore full re-render suppressed count: {RestoreFullRerenderSuppressedCount:N0}");
        builder.AppendLine($"  Window activation re-render suppressed count: {WindowActivationRerenderSuppressedCount:N0}");
        builder.AppendLine($"  Last rendered sequence id: {LastRenderedSequenceId:N0}");
        builder.AppendLine($"  Retained first sequence id: {(Log.TotalRetainedLineCount > 0 ? Math.Max(1, Log.DisplayedLineCount - Log.TotalRetainedLineCount + 1) : 0):N0}");
        builder.AppendLine($"  Retained last sequence id: {Log.DisplayedLineCount:N0}");
        builder.AppendLine($"  Pending visual delta lines: {PendingVisualDeltaLineCount:N0}");
        builder.AppendLine($"  Full re-render in progress: {IsFullXtermRerenderInProgress}");
        builder.AppendLine($"  Last full re-render reason: {LastFullXtermRerenderReason}");
        builder.AppendLine($"  Last full re-render source tag: {LastVisibleLogRebuildReason}");
        builder.AppendLine($"  Last full re-render generation: {LastFullXtermRerenderGeneration:N0}");
        builder.AppendLine($"  Full re-render requested count: {FullXtermRerenderRequestCount:N0}");
        builder.AppendLine($"  Full re-render coalesced count: {FullXtermRerenderCoalescedCount:N0}");
        builder.AppendLine($"  Full re-render canceled count: {FullXtermRerenderCanceledCount:N0}");
        builder.AppendLine($"  Last full re-render line count: {LastFullXtermRerenderLineCount:N0}");
        builder.AppendLine($"  Last full re-render duration: {LastFullXtermRerenderDurationText}");
        builder.AppendLine($"  Full re-render scroll restore attempted: {LastFullXtermScrollRestoreAttempted}");
        builder.AppendLine($"  Full re-render final scroll action: {LastFullXtermFinalScrollAction}");
        builder.AppendLine($"  Last full re-render xterm clear count: {LastFullXtermClearCount:N0}");
        builder.AppendLine($"  Last full re-render visibility toggle count: {LastFullXtermVisibilityToggleCount:N0}");
        builder.AppendLine($"  Suppressed intermediate auto-scroll count: {SuppressedIntermediateAutoScrollCount:N0}");
        builder.AppendLine($"  Last full re-render error: {(string.IsNullOrWhiteSpace(LastFullXtermRerenderError) ? "(none)" : LastFullXtermRerenderError)}");
        builder.AppendLine($"  Visible max lines setting: {MaxVisibleLogLines:N0}");
        builder.AppendLine($"  Last visible cap change time: {LastVisibleCapChangeTimeText}");
        builder.AppendLine($"  xterm scrollback setting: {XtermScrollbackSize:N0}");
        builder.AppendLine($"  Effective xterm scrollback size: {EffectiveXtermScrollbackSize:N0}");
        builder.AppendLine($"  Last applied xterm scrollback size: {LastAppliedXtermScrollbackSize:N0}");
        builder.AppendLine($"  Pending visual line count: {PendingVisualLineCount:N0}");
        builder.AppendLine($"  Visual pending char count: {XtermPendingCharacterCount:N0}");
        builder.AppendLine($"  Max visual pending char count: {MaxXtermPendingCharacterCount:N0}");
        builder.AppendLine($"  Minimized coalesced visual lines: {MinimizedVisualCoalescedLineCount:N0}");
        builder.AppendLine($"  Minimized coalesced visual chars: {MinimizedVisualCoalescedCharacterCount:N0}");
        builder.AppendLine($"  Max minimized coalesced visual lines: {MaxMinimizedVisualCoalescedLineCount:N0}");
        builder.AppendLine($"  Max minimized coalesced visual chars: {MaxMinimizedVisualCoalescedCharacterCount:N0}");
        builder.AppendLine($"  Suspended xterm pending lines: {SuspendedXtermPendingLineCount:N0}");
        builder.AppendLine($"  Suspended xterm pending chars: {SuspendedXtermPendingCharacterCount:N0}");
        builder.AppendLine($"  Suspended xterm queue collapse count: {SuspendedXtermQueueCollapseCount:N0}");
        builder.AppendLine($"  Last suspended xterm queue collapse: {LastSuspendedXtermQueueCollapseReason}");
        builder.AppendLine($"  Last visual append line count: {LastVisualAppendLineCount:N0}");
        builder.AppendLine($"  Max visual append line count: {MaxVisualAppendLineCount:N0}");
        builder.AppendLine($"  Max visual backlog line count: {MaxVisualBacklogLineCount:N0}");
        builder.AppendLine($"  Visual append batch count: {VisualAppendBatchCount:N0}");
        builder.AppendLine($"  Active highlight rule count: {ActiveHighlightRuleCount:N0}");
        builder.AppendLine($"  Visual dispatcher flush count: {VisualDispatcherFlushCount:N0}");
        builder.AppendLine($"  Max visual dispatcher batch size: {MaxVisualDispatcherBatchSize:N0}");
        builder.AppendLine($"  Compiled highlight Terminal rule count: {Log.CompiledTerminalRuleCount:N0}");
        builder.AppendLine($"  Compiled highlight HEX rule count: {Log.CompiledHexRuleCount:N0}");
        builder.AppendLine($"  Invalid compiled highlight rule count: {Log.InvalidCompiledRuleCount:N0}");
        builder.AppendLine($"  Current visible filter: {CurrentVisibleFilterText}");
        builder.AppendLine($"  Available view filters: {AvailableViewFilterCount:N0}");
        builder.AppendLine($"  Filtered visible line count: {Log.FilteredVisibleLineCount:N0}");
        builder.AppendLine($"  Total visible buffer line count: {Log.TotalRetainedLineCount:N0}");
        builder.AppendLine($"  Retained visible lines count: {Log.TotalRetainedLineCount:N0}");
        builder.AppendLine($"  Visible trim count due to cap: {Log.DroppedVisibleLineCount:N0}");
        builder.AppendLine($"  Retained visible character count approx: {Log.VisibleCharacterCount:N0}");
        builder.AppendLine($"  Approx retained visible memory estimate: {Log.VisibleCharacterCount * 2:N0} bytes of UTF-16 text");
        builder.AppendLine($"  Max retained visible line count seen: {Log.MaxRetainedLineCountSeen:N0}");
        builder.AppendLine($"  Last filter change time: {LastVisibleFilterChangeTimeText}");
        builder.AppendLine($"  Filter match errors: {Log.ViewFilterMatchErrorCount:N0}");
        builder.AppendLine($"  Rule filter regex errors: 0 (regex matching is not enabled)");
        builder.AppendLine($"  Visible filter errors: {VisibleFilterErrorCount:N0}");
        builder.AppendLine($"  Last visible filter error: {(string.IsNullOrWhiteSpace(LastVisibleFilterError) ? "(none)" : LastVisibleFilterError)}");
        builder.AppendLine($"  xterm appended lines: {XtermAppendedLineCount:N0}");
        builder.AppendLine($"  xterm append batch count: {XtermAppendBatchCount:N0}");
        builder.AppendLine($"  Last xterm append line count: {LastXtermAppendLineCount:N0}");
        builder.AppendLine($"  Last xterm append char count: {LastXtermAppendCharacterCount:N0}");
        builder.AppendLine($"  Max xterm append line count: {MaxXtermAppendLineCount:N0}");
        builder.AppendLine($"  Max xterm append char count: {MaxXtermAppendCharacterCount:N0}");
        builder.AppendLine($"  Last xterm append duration: {LastXtermAppendDurationText}");
        builder.AppendLine($"  Max xterm append duration: {MaxXtermAppendDurationText}");
        builder.AppendLine($"  xterm append errors: {XtermAppendErrorCount:N0}");
        builder.AppendLine($"  xterm last append error: {(string.IsNullOrWhiteSpace(LastXtermAppendError) ? "(none)" : LastXtermAppendError)}");
        builder.AppendLine($"  WebView2 append error count: {XtermAppendErrorCount:N0}");
        builder.AppendLine($"  Last WebView2 append error: {(string.IsNullOrWhiteSpace(LastXtermAppendError) ? "(none)" : LastXtermAppendError)}");
        builder.AppendLine($"  xterm fit/resize count: {XtermFitResizeCount:N0}");
        builder.AppendLine($"  xterm visual/layout errors: {XtermLayoutErrorCount:N0}");
        builder.AppendLine($"  xterm last visual/layout error: {(string.IsNullOrWhiteSpace(LastXtermLayoutError) ? "(none)" : LastXtermLayoutError)}");
        builder.AppendLine($"  Highlighted line count: {Log.HighlightedLineCount:N0}");
        builder.AppendLine($"  xterm formatting errors: {Log.XtermFormattingErrorCount:N0}");
        builder.AppendLine($"  xterm copy requests: {XtermCopyRequestCount:N0}");
        builder.AppendLine($"  xterm copied characters: {XtermCopiedCharacterCount:N0}");
        builder.AppendLine($"  xterm copy errors: {XtermCopyErrorCount:N0}");
        builder.AppendLine($"  xterm last copy error: {(string.IsNullOrWhiteSpace(LastXtermCopyError) ? "(none)" : LastXtermCopyError)}");
        builder.AppendLine($"  Last xterm context menu action: {LastXtermContextMenuAction}");
        builder.AppendLine($"  xterm context menu errors: {XtermContextMenuErrorCount:N0}");
        builder.AppendLine($"  Last xterm context menu error: {(string.IsNullOrWhiteSpace(LastXtermContextMenuError) ? "(none)" : LastXtermContextMenuError)}");
        builder.AppendLine($"  Last copy visible line count: {LastCopyVisibleLineCount:N0}");
        builder.AppendLine($"  Last copy since TX action time: {LastCopySinceTxActionTimeText}");
        builder.AppendLine($"  Last copy since TX line count: {LastCopySinceTxLineCount:N0}");
        builder.AppendLine($"  Last copy since TX character count: {LastCopySinceTxCharacterCount:N0}");
        builder.AppendLine($"  Last copy since TX result: {LastCopySinceTxResult}");
        builder.AppendLine($"  Copy since TX errors: {CopySinceTxErrorCount:N0}");
        builder.AppendLine($"  Last copy since TX error: {(string.IsNullOrWhiteSpace(LastCopySinceTxError) ? "(none)" : LastCopySinceTxError)}");
        builder.AppendLine($"  Last copy since MARK action time: {LastCopySinceMarkActionTimeText}");
        builder.AppendLine($"  Last copy since MARK line count: {LastCopySinceMarkLineCount:N0}");
        builder.AppendLine($"  Last copy since MARK character count: {LastCopySinceMarkCharacterCount:N0}");
        builder.AppendLine($"  Last copy since MARK result: {LastCopySinceMarkResult}");
        builder.AppendLine($"  Copy since MARK errors: {CopySinceMarkErrorCount:N0}");
        builder.AppendLine($"  Last copy since MARK error: {(string.IsNullOrWhiteSpace(LastCopySinceMarkError) ? "(none)" : LastCopySinceMarkError)}");
        builder.AppendLine($"  Last search selected text length: {LastSearchSelectedTextLength:N0}");
        builder.AppendLine($"  Auto-scroll enabled: {IsAutoScrollEnabled}");
        builder.AppendLine($"  xterm at bottom: {XtermAtBottomText}");
        builder.AppendLine($"  Last auto-scroll action time: {LastAutoScrollActionTimeText}");
        builder.AppendLine($"  Last auto-scroll error: {(string.IsNullOrWhiteSpace(LastAutoScrollError) ? "(none)" : LastAutoScrollError)}");
        builder.AppendLine($"  Displayed lines: {Log.DisplayedLineCount:N0}");
        builder.AppendLine($"  Dropped visible lines: {Log.DroppedVisibleLineCount:N0}");
        builder.AppendLine($"  Dropped pending UI lines: {Log.DroppedPendingLineCount:N0}");
        builder.AppendLine($"  Current visible line count: {Log.CurrentVisibleLineCount:N0}");
        builder.AppendLine();
        builder.AppendLine("Visible Log Search");
        builder.AppendLine($"  Last search text: {(string.IsNullOrWhiteSpace(SearchText) ? "(none)" : SearchText)}");
        builder.AppendLine($"  Case sensitive: {IsSearchCaseSensitive}");
        builder.AppendLine($"  Search match count: {SearchMatchCount:N0}");
        builder.AppendLine($"  Current search match index: {CurrentSearchMatchIndex:N0}");
        builder.AppendLine($"  Current matched line: {(string.IsNullOrWhiteSpace(CurrentSearchMatchedLine) ? "(none)" : CurrentSearchMatchedLine)}");
        builder.AppendLine($"  Search errors: {SearchErrorCount:N0}");
        builder.AppendLine($"  Last search error: {(string.IsNullOrWhiteSpace(LastSearchError) ? "(none)" : LastSearchError)}");
        builder.AppendLine($"  Search result count: {SearchMatchCount:N0}");
        builder.AppendLine($"  Search result visible count: {SearchResultVisibleCount:N0}");
        builder.AppendLine($"  Selected search result index: {SelectedSearchResultIndex:N0}");
        builder.AppendLine($"  Search result status: {SearchResultStatusText}");
        builder.AppendLine($"  Search results rebuild count: {SearchResultsRebuildCount:N0}");
        builder.AppendLine("  Search results refresh mode: Manual only");
        builder.AppendLine($"  Search results stale: {AreSearchResultsStale}");
        builder.AppendLine($"  Last search shortcut action: {LastSearchShortcutAction}");
        builder.AppendLine($"  Last search shortcut source: {LastSearchShortcutSource}");
        builder.AppendLine($"  Last search shortcut time: {LastSearchShortcutTimeText}");
        builder.AppendLine($"  Search shortcut errors: {SearchShortcutErrorCount:N0}");
        builder.AppendLine($"  Last search shortcut error: {(string.IsNullOrWhiteSpace(LastSearchShortcutError) ? "(none)" : LastSearchShortcutError)}");
        builder.AppendLine($"  Search result selection lost count: {SearchResultSelectionLostCount:N0}");
        builder.AppendLine($"  Search result build errors: {SearchResultBuildErrorCount:N0}");
        builder.AppendLine($"  Last search result build error: {(string.IsNullOrWhiteSpace(LastSearchResultBuildError) ? "(none)" : LastSearchResultBuildError)}");
        builder.AppendLine($"  Search result jump errors: {SearchResultJumpErrorCount:N0}");
        builder.AppendLine($"  Last search result jump error: {(string.IsNullOrWhiteSpace(LastSearchResultJumpError) ? "(none)" : LastSearchResultJumpError)}");
        builder.AppendLine($"  List update errors: {ListUpdateErrorCount:N0}");
        builder.AppendLine($"  Last list update error: {(string.IsNullOrWhiteSpace(LastListUpdateError) ? "(none)" : LastListUpdateError)}");
        builder.AppendLine($"  Active inspector tab: {ActiveInspectorTabText}");
        builder.AppendLine($"  Inspector tab layout errors: {InspectorTabLayoutErrorCount:N0}");
        builder.AppendLine($"  Last inspector tab layout error: {(string.IsNullOrWhiteSpace(LastInspectorTabLayoutError) ? "(none)" : LastInspectorTabLayoutError)}");
        builder.AppendLine($"  Search tab layout errors: {SearchTabLayoutErrorCount:N0}");
        builder.AppendLine($"  Last search tab layout error: {(string.IsNullOrWhiteSpace(LastSearchTabLayoutError) ? "(none)" : LastSearchTabLayoutError)}");
        builder.AppendLine($"  StatusChanged thread marshal errors: {StatusChangedThreadMarshalErrorCount:N0}");
        builder.AppendLine($"  Last StatusChanged thread marshal error: {(string.IsNullOrWhiteSpace(LastStatusChangedThreadMarshalError) ? "(none)" : LastStatusChangedThreadMarshalError)}");
        builder.AppendLine($"  xterm search requests: {XtermSearchRequestCount:N0}");
        builder.AppendLine($"  xterm search hits: {XtermSearchHitCount:N0}");
        builder.AppendLine($"  xterm search errors: {XtermSearchErrorCount:N0}");
        builder.AppendLine($"  xterm last search error: {(string.IsNullOrWhiteSpace(LastXtermSearchError) ? "(none)" : LastXtermSearchError)}");
        builder.AppendLine();
        builder.AppendLine("File Writer");
        builder.AppendLine($"  File logging enabled: {FileLoggingEnabled}");
        builder.AppendLine($"  File logging active: {FileLoggingActive}");
        builder.AppendLine($"  Running: {_fileLogWriter.IsRunning}");
        builder.AppendLine($"  Pending requests: {_fileLogWriter.PendingRequestCount:N0}");
        builder.AppendLine($"  Start count: {_fileLogWriter.StartCount:N0}");
        builder.AppendLine($"  Stop count: {_fileLogWriter.StopCount:N0}");
        builder.AppendLine($"  Last lifecycle action: {_fileLogWriter.LastLifecycleAction}");
        builder.AppendLine($"  Lifecycle errors: {_fileLogWriter.LifecycleErrorCount:N0}");
        builder.AppendLine($"  Written log lines: {_fileLogWriter.WrittenLineCount:N0}");
        builder.AppendLine($"  Written log bytes: {_fileLogWriter.WrittenByteCount:N0}");
        builder.AppendLine($"  Dropped lines: {_fileLogWriter.DroppedLineCount:N0}");
        builder.AppendLine($"  Error count: {_fileLogWriter.FileErrorCount:N0}");
        builder.AppendLine($"  Log directory: {_fileLogWriter.LogDirectory}");
        builder.AppendLine($"  Current log file path: {_fileLogWriter.CurrentLogFilePath ?? "(not open)"}");
        builder.AppendLine($"  File writer last error: {_fileLogWriter.LastFileError ?? "(none)"}");
        builder.AppendLine();
        builder.AppendLine("Event Detection");
        builder.AppendLine($"  Running: {_eventDetector.IsRunning}");
        builder.AppendLine($"  Event rule count: {_eventDetector.EventRuleCount:N0}");
        builder.AppendLine($"  Active event rule mode: {_eventDetector.ActiveRuleMode}");
        builder.AppendLine($"  Compiled event Terminal rule count: {_eventDetector.CompiledTerminalRuleCount:N0}");
        builder.AppendLine($"  Compiled event HEX rule count: {_eventDetector.CompiledHexRuleCount:N0}");
        builder.AppendLine($"  Invalid compiled event rule count: {_eventDetector.InvalidCompiledRuleCount:N0}");
        builder.AppendLine($"  Enabled rule count: {EventRules.Count(rule => rule.Enabled):N0}");
        builder.AppendLine($"  Tray notification rules: {EventRules.Count(rule => rule.Enabled && rule.TrayNotificationEnabled):N0}");
        builder.AppendLine($"  Sound notification rules: {EventRules.Count(rule => rule.Enabled && rule.SoundNotificationEnabled):N0}");
        builder.AppendLine($"  Popup notification rules: {EventRules.Count(rule => rule.Enabled && rule.PopupNotificationEnabled):N0}");
        builder.AppendLine($"  Notification batches delivered: {Interlocked.Read(ref _eventNotificationBatchCount):N0}");
        builder.AppendLine($"  Events included in notifications: {Interlocked.Read(ref _eventNotificationEventCount):N0}");
        builder.AppendLine($"  Detected event count: {_eventDetector.DetectedEventCount:N0}");
        builder.AppendLine($"  Detected event UI item count: {DetectedEventUiItemCount:N0}");
        builder.AppendLine($"  Visible event cap: {Events.Capacity:N0}");
        builder.AppendLine($"  Event UI batch interval ms: {EventRenderIntervalMs:N0}");
        builder.AppendLine($"  Event UI pending count: {PendingEventUiCount:N0}");
        builder.AppendLine($"  Event UI flush count: {EventUiFlushCount:N0}");
        builder.AppendLine($"  Max event UI batch size: {MaxEventUiBatchSize:N0}");
        builder.AppendLine($"  Displayed event count: {Events.DisplayedEventCount:N0}");
        builder.AppendLine($"  Dropped visible event count: {Events.DroppedVisibleEventCount:N0}");
        builder.AppendLine($"  Current visible event count: {Events.CurrentVisibleEventCount:N0}");
        builder.AppendLine($"  Event auto-scroll enabled: {IsEventAutoScrollEnabled}");
        builder.AppendLine($"  Latest event select count: {LatestEventSelectCount:N0}");
        builder.AppendLine($"  Event list incremental updates: {EventListIncrementalUpdateCount:N0}");
        builder.AppendLine($"  Event list reset count: {EventListResetCount:N0}");
        builder.AppendLine($"  Event selection preserved count: {EventSelectionPreservedCount:N0}");
        builder.AppendLine($"  Event selection lost count: {EventSelectionLostCount:N0}");
        builder.AppendLine($"  Event list scroll errors: {EventListScrollErrorCount:N0}");
        builder.AppendLine($"  Last event list scroll error: {(string.IsNullOrWhiteSpace(LastEventListScrollError) ? "(none)" : LastEventListScrollError)}");
        builder.AppendLine($"  Event detector error count: {_eventDetector.ErrorCount:N0}");
        builder.AppendLine($"  Event context captures started: {_eventDetector.ContextCapturesStartedCount:N0}");
        builder.AppendLine($"  Event context captures completed: {_eventDetector.ContextCapturesCompletedCount:N0}");
        builder.AppendLine($"  Active pending event contexts: {_eventDetector.ActivePendingContextCount:N0}");
        builder.AppendLine($"  Event context dropped count: {_eventDetector.ContextCaptureDroppedCount:N0}");
        builder.AppendLine($"  Event context scan lines: {_eventDetector.ContextCaptureScanLineCount:N0}");
        builder.AppendLine($"  Event context capture entries visited: {_eventDetector.ContextCaptureEntriesVisited:N0}");
        builder.AppendLine($"  Event context max captures scanned per line: {_eventDetector.MaxContextCaptureScanCount:N0}");
        builder.AppendLine($"  Event context overload active: {_eventDetector.IsContextCaptureOverloadActive}");
        builder.AppendLine($"  Event context overload high/low: {_eventDetector.ContextCaptureOverloadHighWatermark:N0}/{_eventDetector.ContextCaptureOverloadLowWatermark:N0}");
        builder.AppendLine($"  Event contexts skipped by overload: {_eventDetector.ContextCaptureOverloadSkippedCount:N0}");
        builder.AppendLine($"  Event context overload transitions: {_eventDetector.ContextCaptureOverloadTransitionCount:N0}");
        builder.AppendLine($"  Last event context overload transition: {(_eventDetector.LastContextCaptureOverloadTransitionTime?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "(none)")}");
        builder.AppendLine($"  Event context UI pending count: {PendingEventContextUiCount:N0}");
        builder.AppendLine($"  UI-only event context updates dropped: {EventContextUiDroppedCount:N0}");
        builder.AppendLine($"  Event context failed count: {_eventDetector.ContextCaptureFailedCount:N0}");
        builder.AppendLine($"  Retained UI event context cap: {RetainedEventContextLimit:N0}");
        builder.AppendLine($"  Retained UI event contexts: {_eventContextsById.Count:N0}");
        builder.AppendLine($"  Selected event rule/name: {SelectedEventRuleName}");
        builder.AppendLine($"  Selected event has context: {SelectedEventContextAvailable}");
        builder.AppendLine($"  Selected event context line count: {SelectedEventContextLineCount:N0}");
        builder.AppendLine($"  Selected event context available: {SelectedEventContextAvailable}");
        builder.AppendLine($"  Selected event context status: {SelectedEventContextStatusText}");
        builder.AppendLine($"  Context refresh count: {ContextRefreshCount:N0}");
        builder.AppendLine($"  Context render refresh errors: {ContextRefreshErrorCount:N0}");
        builder.AppendLine($"  Last context refresh error: {(string.IsNullOrWhiteSpace(LastContextRefreshError) ? "(none)" : LastContextRefreshError)}");
        builder.AppendLine($"  Context tab activated count: {ContextTabActivatedCount:N0}");
        builder.AppendLine($"  Context visual refresh count: {ContextVisualRefreshCount:N0}");
        builder.AppendLine($"  Last context visual refresh time: {LastContextVisualRefreshTimeText}");
        builder.AppendLine($"  Last context visual refresh event id: {LastContextVisualRefreshEventId}");
        builder.AppendLine($"  Last context visual refresh event summary: {LastContextVisualRefreshEventSummary}");
        builder.AppendLine($"  Last context text length: {LastContextVisualRefreshTextLength:N0}");
        builder.AppendLine($"  Context render errors: {ContextRenderErrorCount:N0}");
        builder.AppendLine($"  Last context render error: {(string.IsNullOrWhiteSpace(LastContextRenderError) ? "(none)" : LastContextRenderError)}");
        builder.AppendLine($"  Context WebView ready: {IsContextWebViewReady}");
        builder.AppendLine($"  Context WebView update count: {ContextWebViewUpdateCount:N0}");
        builder.AppendLine($"  Last context WebView update time: {LastContextWebViewUpdateTimeText}");
        builder.AppendLine($"  Selected event summary at context update: {LastContextWebViewUpdateEventSummary}");
        builder.AppendLine($"  Context text length at update: {LastContextWebViewTextLength:N0}");
        builder.AppendLine($"  Context WebView update errors: {ContextWebViewUpdateErrorCount:N0}");
        builder.AppendLine($"  Last context WebView update error: {(string.IsNullOrWhiteSpace(LastContextWebViewUpdateError) ? "(none)" : LastContextWebViewUpdateError)}");
        builder.AppendLine($"  Copied event context count: {CopiedEventContextCount:N0}");
        builder.AppendLine($"  Event selection errors: {EventSelectionErrorCount:N0}");
        builder.AppendLine($"  Last event selection error: {(string.IsNullOrWhiteSpace(LastEventSelectionError) ? "(none)" : LastEventSelectionError)}");
        builder.AppendLine($"  Event context UI errors: {EventContextUiErrorCount:N0}");
        builder.AppendLine($"  Last event context UI error: {(string.IsNullOrWhiteSpace(LastEventContextUiError) ? "(none)" : LastEventContextUiError)}");
        builder.AppendLine($"  Dropped event input lines: {_eventDetector.DroppedInputLineCount:N0}");
        builder.AppendLine($"  Dropped output events: {_eventDetector.DroppedOutputEventCount:N0}");
        builder.AppendLine($"  Last detected event: {_eventDetector.LastDetectedEventText ?? "(none)"}");
        builder.AppendLine($"  Last event detector error: {_eventDetector.LastError ?? "(none)"}");
        return builder.ToString();
    }

    private string CreateLastErrorText()
    {
        if (!string.IsNullOrWhiteSpace(_lastConnectFailureReason))
        {
            return _lastConnectFailureReason;
        }

        if (!string.IsNullOrWhiteSpace(_serialService.LastError))
        {
            return _serialService.LastError;
        }

        if (!string.IsNullOrWhiteSpace(_fileLogWriter.LastFileError))
        {
            return _fileLogWriter.LastFileError;
        }

        if (!string.IsNullOrWhiteSpace(_eventDetector.LastError))
        {
            return _eventDetector.LastError;
        }

        if (!string.IsNullOrWhiteSpace(_bridgeService.LastError))
        {
            return _bridgeService.LastError;
        }

        if (!string.IsNullOrWhiteSpace(_profileService.LastError))
        {
            return _profileService.LastError;
        }

        if (!string.IsNullOrWhiteSpace(_lastMarkerError))
        {
            return _lastMarkerError;
        }

        if (!string.IsNullOrWhiteSpace(_lastSessionError))
        {
            return _lastSessionError;
        }

        if (!string.IsNullOrWhiteSpace(_lastSessionFileNamingError))
        {
            return _lastSessionFileNamingError;
        }

        if (!string.IsNullOrWhiteSpace(_lastSaveDirectoryError))
        {
            return _lastSaveDirectoryError;
        }

        if (!string.IsNullOrWhiteSpace(_lastXtermAppendError))
        {
            return _lastXtermAppendError;
        }

        if (!string.IsNullOrWhiteSpace(_lastXtermCopyError))
        {
            return _lastXtermCopyError;
        }

        if (!string.IsNullOrWhiteSpace(_lastXtermSearchError))
        {
            return _lastXtermSearchError;
        }

        if (!string.IsNullOrWhiteSpace(_lastXtermContextMenuError))
        {
            return _lastXtermContextMenuError;
        }

        if (!string.IsNullOrWhiteSpace(_lastTimestampDisplayModeError))
        {
            return _lastTimestampDisplayModeError;
        }

        if (!string.IsNullOrWhiteSpace(_lastDisconnectConfirmationError))
        {
            return _lastDisconnectConfirmationError;
        }

        if (!string.IsNullOrWhiteSpace(_lastSearchResultBuildError))
        {
            return _lastSearchResultBuildError;
        }

        if (!string.IsNullOrWhiteSpace(_lastSearchResultJumpError))
        {
            return _lastSearchResultJumpError;
        }

        if (!string.IsNullOrWhiteSpace(_lastListUpdateError))
        {
            return _lastListUpdateError;
        }

        if (!string.IsNullOrWhiteSpace(_lastInspectorTabLayoutError))
        {
            return _lastInspectorTabLayoutError;
        }

        if (!string.IsNullOrWhiteSpace(_lastSearchTabLayoutError))
        {
            return _lastSearchTabLayoutError;
        }

        if (!string.IsNullOrWhiteSpace(_lastLogFileActionError))
        {
            return _lastLogFileActionError;
        }

        if (!string.IsNullOrWhiteSpace(_lastSettingsApplyError))
        {
            return _lastSettingsApplyError;
        }

        if (!string.IsNullOrWhiteSpace(_lastStatusChangedThreadMarshalError))
        {
            return _lastStatusChangedThreadMarshalError;
        }

        if (!string.IsNullOrWhiteSpace(_lastXtermLayoutError))
        {
            return _lastXtermLayoutError;
        }

        if (!string.IsNullOrWhiteSpace(_lastEventContextUiError))
        {
            return _lastEventContextUiError;
        }

        if (!string.IsNullOrWhiteSpace(_lastEventSelectionError))
        {
            return _lastEventSelectionError;
        }

        if (!string.IsNullOrWhiteSpace(_lastEventListScrollError))
        {
            return _lastEventListScrollError;
        }

        if (!string.IsNullOrWhiteSpace(_lastContextRefreshError))
        {
            return _lastContextRefreshError;
        }

        if (!string.IsNullOrWhiteSpace(_lastContextRenderError))
        {
            return _lastContextRenderError;
        }

        if (!string.IsNullOrWhiteSpace(_lastContextWebViewUpdateError))
        {
            return _lastContextWebViewUpdateError;
        }

        if (!string.IsNullOrWhiteSpace(_lastRuleEditError))
        {
            return _lastRuleEditError;
        }

        if (!string.IsNullOrWhiteSpace(_lastCommandEditError))
        {
            return _lastCommandEditError;
        }

        if (!string.IsNullOrWhiteSpace(_lastSequenceError))
        {
            return _lastSequenceError;
        }

        if (!string.IsNullOrWhiteSpace(_lastMockSequenceError))
        {
            return _lastMockSequenceError;
        }

        return string.IsNullOrWhiteSpace(_lastBackgroundError)
            ? "(none)"
            : _lastBackgroundError;
    }

    private string GetLogFolderPath()
    {
        return LogSaveDirectory;
    }

    private void RunOnUiThread(Action action)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                RecordStatusChangedThreadMarshalError($"UI update failed: {ex.Message}");
            }

            return;
        }

        var queued = _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                RecordStatusChangedThreadMarshalError($"Dispatched UI update failed: {ex.Message}");
            }
        });

        if (!queued)
        {
            RecordStatusChangedThreadMarshalErrorWithoutUi("DispatcherQueue rejected UI update.");
        }
    }

    private void NotifyCommandStates()
    {
        OnPropertyChanged(nameof(CanManualDisconnect));
        OnPropertyChanged(nameof(CanToggleConnection));
        ConnectCommand.NotifyCanExecuteChanged();
        ToggleConnectionCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        SendSavedCommandCommand.NotifyCanExecuteChanged();
        AddMarkerCommand.NotifyCanExecuteChanged();
        AddDefaultMarkerCommand.NotifyCanExecuteChanged();
        SetSessionCommand.NotifyCanExecuteChanged();
        EndSessionCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
        LoadProfileCommand.NotifyCanExecuteChanged();
        ResetProfileCommand.NotifyCanExecuteChanged();
        SelectLatestEventCommand.NotifyCanExecuteChanged();
        StartMockStressCommand.NotifyCanExecuteChanged();
        StopMockStressCommand.NotifyCanExecuteChanged();
        ResetMockStressCountersCommand.NotifyCanExecuteChanged();
        SendMockCrlfCommand.NotifyCanExecuteChanged();
        RunCommandSequenceCommand.NotifyCanExecuteChanged();
        StopCommandSequenceCommand.NotifyCanExecuteChanged();
        NotifyLogFileActionCommandStates();
    }

    private void NotifyLogFileActionCommandStates()
    {
        OpenCurrentSerialLogCommand.NotifyCanExecuteChanged();
        CopySerialLogPathCommand.NotifyCanExecuteChanged();
        ToggleFileLoggingCommand.NotifyCanExecuteChanged();
    }

    private void NotifyFileLoggingStateChanged()
    {
        OnPropertyChanged(nameof(FileLoggingEnabled));
        OnPropertyChanged(nameof(FileLoggingActive));
        OnPropertyChanged(nameof(FileLoggingToggleText));
        OnPropertyChanged(nameof(FileLoggingMainStatusText));
        OnPropertyChanged(nameof(FileLoggingToolTip));
        OnPropertyChanged(nameof(FileLoggingWhileViewPaused));
        OnPropertyChanged(nameof(CanEditLogFileName));
        OnPropertyChanged(nameof(CurrentSerialLogPath));
        SetFooter(CreateFooterStatus());
        RefreshLogFileActionProperties();
        NotifyLogFileActionCommandStates();
        RefreshDiagnostics();
    }

    private void NotifySearchCommandStates()
    {
        FindNextCommand.NotifyCanExecuteChanged();
        FindPreviousCommand.NotifyCanExecuteChanged();
        RefreshSearchResultsCommand.NotifyCanExecuteChanged();
    }

    private enum SearchMove
    {
        None,
        Next,
        Previous
    }
}
