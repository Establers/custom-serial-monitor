using System.Globalization;
using System.Text;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed class LogTextBatch
{
    public LogTextBatch(string appendedText, int trimCharacterCount, int lineCount, long endDisplayedLineCount)
    {
        AppendedText = appendedText;
        TrimCharacterCount = trimCharacterCount;
        LineCount = lineCount;
        EndDisplayedLineCount = endDisplayedLineCount;
    }

    public string AppendedText { get; }

    public int TrimCharacterCount { get; }

    public int LineCount { get; }

    public long EndDisplayedLineCount { get; }
}

public readonly record struct VisibleLogSearchLine(
    long LineId,
    string FullText,
    int PayloadStart,
    LogDirection Direction = LogDirection.System,
    long? TimestampUnixMilliseconds = null)
{
    public string PayloadText => FullText[PayloadStart..];
}

public sealed partial class LogViewModel : ViewModelBase
{
    // Rule snapshots are shared by all lines received under the same rule version.
    private readonly record struct RetainedLogLine(
        long LineId,
        LogLine Line,
        LogRuleMatcher.CompiledHighlightRule[] HighlightRules,
        string HighlightPrefix = "", string FilterPrefix = "", long PreviousRxPosition = 0,
        LogRuleMatcher.CompiledHighlightRule? TerminalLineHighlight = null, long TerminalLineStart = 0)
    {
        // Immutable receipt-time suffixes survive screen eviction/reformatting.
        // Their size is bounded by keyword lengths and the retained byte budget.
        public long EstimatedBytes => LogViewMemoryPolicy.EstimateRetainedBytes(Line) +
            2L * HighlightPrefix.Length +
            (ReferenceEquals(HighlightPrefix, FilterPrefix) ? 0 : 2L * FilterPrefix.Length);
    }

    private readonly record struct PreparedRuleLine(RetainedLogLine Source, LogLine Line,
        LogRuleMatcher.CompiledHighlightRule? Highlight, bool Visible, bool BreakBefore, string MatchPrefix);

    private const string AnsiReset = "\u001b[0m";
    private const int SnapshotPreallocationMaxChars = 64 * 1024 * 1024;
    private const int LiveBatchPreallocationMaxChars = 1024 * 1024;
    private const int EstimatedFormattedLineOverheadChars = 64;
    private const string AnsiBracketBlue = "\u001b[38;2;207;232;255m";
    private const string AnsiCyan = "\u001b[36m";
    private const string AnsiGreen = "\u001b[32m";
    private const string AnsiGray = "\u001b[90m";
    private const string AnsiBackgroundContrastForegroundCode = "38;5;16";
    private const string XtermLineIdentityOscPrefix = "\u001b]777;";
    private const string XtermOscTerminator = "\u0007";
    // One queue entry per visible line instead of six separately allocated linked-list nodes.
    private sealed class VisibleLine(string displayText, string searchText, int payloadStart, long id, long timestamp, LogDirection direction, bool hasLineEnding)
    {
        public string DisplayText = displayText;
        public string SearchText = searchText;
        public readonly int PayloadStart = payloadStart;
        public int Length = displayText.Length + (hasLineEnding ? Environment.NewLine.Length : 0);
        public bool HasLineEnding = hasLineEnding;
        public readonly long Id = id;
        public readonly long Timestamp = timestamp;
        public readonly LogDirection Direction = direction;
    }

    private int _capacity;
    private readonly Queue<RetainedLogLine> _retainedLines = new();
    // 1 starts a line; 2 starts one with a restored hidden prefix. -1 appends
    // a visible RX fragment; 0 is hidden. Positive entries each count as ONE line.
    private Queue<int> _retainedVisibleLineContributions = new();
    private Queue<VisibleLine> _visibleLines = new();
    private VisibleLine? _lastVisibleLine;
    private LogRuleMatcher.CompiledHighlightRule[] _highlightRules = Array.Empty<LogRuleMatcher.CompiledHighlightRule>();
    private LogRuleMatcher.CompiledHighlightRule[] _viewFilters = Array.Empty<LogRuleMatcher.CompiledHighlightRule>();
    private StreamingHighlightRuleMatcher _highlightMatcher = new();
    private StreamingHighlightRuleMatcher _filterMatcher = new();
    private bool _showTimestampInLogView = true;
    private bool _showRxTxDirectionPrefixInLogView = true;
    private TimestampDisplayFormat _timestampDisplayFormat = TimestampDisplayFormat.DateTimeMilliseconds;
    private RxDisplayMode _rxDisplayMode = RxDisplayMode.Terminal;
    private bool _partialRxVisualLineActive;
    private int _partialRxVisualLength;
    private StringBuilder? _partialRxDisplayBuilder;
    private StringBuilder? _partialRxSearchableBuilder;
    private VisibleLine? _partialRxLine;
    private bool _partialRxLineDirty;
    private long _displayedLineCount;
    private long _droppedVisibleLineCount;
    private long _droppedPendingLineCount;
    private long _highlightedLineCount;
    private long _xtermFormattingErrorCount;
    private long _viewFilterMatchErrorCount;
    private long _visibleCharacterCount;
    private long _maxRetainedLineCountSeen;
    private long _partialRxAppendInPlaceCount;
    private int _compiledTerminalRuleCount;
    private int _compiledHexRuleCount;
    private int _invalidCompiledRuleCount;
    private long _nextLineId;

    public LogViewModel(int capacity) : this(capacity, LogViewMemoryPolicy.Default) { }

    public event EventHandler<LogTextBatch>? TextBatchAppended;

    public event EventHandler? TextCleared;

    public event EventHandler? TextRebuilt;

    public long DisplayedLineCount
    {
        get => _displayedLineCount;
        private set => SetProperty(ref _displayedLineCount, value);
    }

    public long DroppedVisibleLineCount
    {
        get => _droppedVisibleLineCount;
        private set => SetProperty(ref _droppedVisibleLineCount, value);
    }

    public long DroppedPendingLineCount
    {
        get => _droppedPendingLineCount;
        private set => SetProperty(ref _droppedPendingLineCount, value);
    }

    public long HighlightedLineCount
    {
        get => _highlightedLineCount;
        private set => SetProperty(ref _highlightedLineCount, value);
    }

    public long XtermFormattingErrorCount
    {
        get => _xtermFormattingErrorCount;
        private set => SetProperty(ref _xtermFormattingErrorCount, value);
    }

    public long ViewFilterMatchErrorCount
    {
        get => _viewFilterMatchErrorCount;
        private set => SetProperty(ref _viewFilterMatchErrorCount, value);
    }

    public int CurrentVisibleLineCount => _visibleLines.Count;

    public int FilteredVisibleLineCount => _visibleLines.Count;

    public int TotalRetainedLineCount => _retainedLines.Count;

    public int Capacity => _capacity;

    public long VisibleCharacterCount
    {
        get => _visibleCharacterCount;
        private set => SetProperty(ref _visibleCharacterCount, value);
    }

    public long MaxRetainedLineCountSeen
    {
        get => _maxRetainedLineCountSeen;
        private set => SetProperty(ref _maxRetainedLineCountSeen, value);
    }

    public bool PartialRxVisualLineActive => _partialRxVisualLineActive;

    public int PartialRxVisualLength => _partialRxVisualLength;

    public long PartialRxAppendInPlaceCount => Interlocked.Read(ref _partialRxAppendInPlaceCount);

    public int CompiledTerminalRuleCount => _compiledTerminalRuleCount;

    public int CompiledHexRuleCount => _compiledHexRuleCount;

    public int InvalidCompiledRuleCount => _invalidCompiledRuleCount;

    public void SetCapacity(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        }

        if (_capacity == capacity)
        {
            return;
        }

        var shrinking = capacity < _capacity;
        _visibleRebuildVersion++;
        _capacity = capacity;
        var trimmed = TrimRetainedLinesToCapacity(out _);
        if (shrinking)
        {
            _retainedLines.TrimExcess();
            _retainedVisibleLineContributions.TrimExcess();
            _visibleLines.TrimExcess();
        }
        OnPropertyChanged(nameof(Capacity));
        RaiseVisibleCountProperties();
        if (trimmed > 0)
        {
            TextRebuilt?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetHighlightRules(IEnumerable<HighlightRule> highlightRules)
    {
        _highlightRules = highlightRules.Select(LogRuleMatcher.Compile).ToArray();
        _compiledTerminalRuleCount = _highlightRules.Count(rule => rule.IsTerminalRule);
        _compiledHexRuleCount = _highlightRules.Count(rule => rule.IsHexRule);
        _invalidCompiledRuleCount = _highlightRules.Count(rule => rule.IsInvalid);
        OnPropertyChanged(nameof(CompiledTerminalRuleCount));
        OnPropertyChanged(nameof(CompiledHexRuleCount));
        OnPropertyChanged(nameof(InvalidCompiledRuleCount));
    }

    public void SetShowTimestampInLogView(bool showTimestamp)
    {
        if (_showTimestampInLogView == showTimestamp)
        {
            return;
        }

        _showTimestampInLogView = showTimestamp;
        RebuildVisibleLinesFromRetained();
        TextRebuilt?.Invoke(this, EventArgs.Empty);
    }

    public void SetShowRxTxDirectionPrefixInLogView(bool showDirectionPrefix)
    {
        if (_showRxTxDirectionPrefixInLogView == showDirectionPrefix)
        {
            return;
        }

        _showRxTxDirectionPrefixInLogView = showDirectionPrefix;
        RebuildVisibleLinesFromRetained();
        TextRebuilt?.Invoke(this, EventArgs.Empty);
    }

    public void SetTimestampDisplayFormat(TimestampDisplayFormat timestampDisplayFormat)
    {
        timestampDisplayFormat = NormalizeTimestampDisplayFormat(timestampDisplayFormat);
        if (_timestampDisplayFormat == timestampDisplayFormat)
        {
            return;
        }

        _timestampDisplayFormat = timestampDisplayFormat;
        RebuildVisibleLinesFromRetained();
        TextRebuilt?.Invoke(this, EventArgs.Empty);
    }

    public void SetRxDisplayMode(RxDisplayMode mode, bool clearExisting = false)
    {
        mode = NormalizeRxDisplayMode(mode);

        if (_rxDisplayMode == mode)
        {
            return;
        }

        _rxDisplayMode = mode;
        if (clearExisting)
        {
            Clear();
            return;
        }
        RebuildVisibleLinesFromRetained();
        TextRebuilt?.Invoke(this, EventArgs.Empty);
    }

    public void SetViewFilter(HighlightRule? viewFilter)
    {
        SetViewFilters(viewFilter is null ? [] : [viewFilter]);
    }

    public void SetViewFilters(IEnumerable<HighlightRule> viewFilters)
    {
        CancelPendingViewFilter();
        _viewFilters = viewFilters.Select(LogRuleMatcher.Compile).ToArray();
        RebuildVisibleLinesFromRetained();
        TextRebuilt?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshVisibleFormatting()
    {
        RebuildVisibleLinesFromRetained();
        TextRebuilt?.Invoke(this, EventArgs.Empty);
    }

    public void AddRange(IReadOnlyList<LogLine> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        // This builder contains only the incoming batch. Preallocating it from
        // VisibleCharacterCount made every append reserve the entire retained
        // log size once the visible buffer reached its cap, causing large-object
        // allocations and periodic GC pauses at 100k+ retained lines.
        var builder = new StringBuilder(EstimateLiveBatchCharacterCount(lines));
        var highlightedLines = 0;
        var formattingErrors = 0;
        var appendedVisibleLineCount = 0;
        var memoryDropped = 0;
        foreach (var line in lines)
        {
            var retainedLine = new RetainedLogLine(++_nextLineId, line, _highlightRules,
                PreviousRxPosition: _highlightMatcher.LastRxPosition);
            var prepared = PrepareRuleLine(retainedLine, captureContext: true, appendedText: builder);
            var terminalHighlight = CaptureTerminalHighlightContext();
            retainedLine = prepared.Source;
            var retainedBytes = retainedLine.EstimatedBytes;
            if (retainedBytes > _memoryPolicy.RetainedBytes)
            {
                if (line.Direction == LogDirection.Rx && (prepared.BreakBefore || !line.IsPartialRxSegment))
                    CompleteActivePartialRxVisualLine(builder);
                memoryDropped++;
                continue;
            }
            var evicted = ReserveScreenMemory(retainedLine);
            if (evicted > 0)
            {
                memoryDropped += evicted;
                builder.Clear();
                prepared = RestoreEvictedMatchPrefix(prepared);
                RestoreTerminalHighlightContext(terminalHighlight);
            }
            _retainedLines.Enqueue(retainedLine);
            _retainedMemoryBytes += retainedBytes;
            TrackPendingFilterLine(retainedLine);
            var visibleContribution = 0;
            if (prepared.BreakBefore && CompleteActivePartialRxVisualLine(builder))
                appendedVisibleLineCount++;
            if (line.IsPartialRxTerminator)
            {
                if (CompleteActivePartialRxVisualLine(builder))
                {
                    appendedVisibleLineCount++;
                }

                _retainedVisibleLineContributions.Enqueue(visibleContribution);
                continue;
            }

            if (!prepared.Visible)
            {
                if (line.Direction == LogDirection.Rx && !line.IsPartialRxSegment &&
                    CompleteActivePartialRxVisualLine(builder)) appendedVisibleLineCount++;
                _retainedVisibleLineContributions.Enqueue(visibleContribution);
                continue;
            }

            if (ShouldMergePartialRxVisually(line) ||
                (line.Direction == LogDirection.Rx && !prepared.BreakBefore && _partialRxVisualLineActive))
            {
                var formattedPartial = FormatPartialRxVisibleSegment(prepared.Line, prepared.Highlight);
                if (BreakLongScreenLine(formattedPartial.DisplayLine.Length, formattedPartial.SearchableLine.Length, builder))
                {
                    appendedVisibleLineCount++;
                    formattedPartial = FormatPartialRxVisibleSegment(prepared.Line, prepared.Highlight);
                }
                if (formattedPartial.IsHighlighted) highlightedLines++;
                if (formattedPartial.HasFormattingError)
                {
                    formattingErrors++;
                }

                if (_partialRxVisualLineActive && _lastVisibleLine is not null)
                {
                    var appendedPartialDisplayText = AppendPartialRxVisualSegment(
                        formattedPartial.DisplayLine,
                        formattedPartial.SearchableLine,
                        retainedLine.LineId,
                        line.Timestamp,
                        line.Direction);
                    builder.Append(appendedPartialDisplayText);
                    Interlocked.Increment(ref _partialRxAppendInPlaceCount);
                    visibleContribution = -1;
                }
                else
                {
                    var taggedPartialDisplayLine = AddVisibleLine(
                        formattedPartial.DisplayLine,
                        formattedPartial.SearchableLine,
                        formattedPartial.PayloadStart,
                        retainedLine.LineId,
                        line.Timestamp,
                        line.Direction);
                    BeginActivePartialRxVisualLine(formattedPartial.DisplayLine, formattedPartial.SearchableLine);
                    builder.Append(taggedPartialDisplayLine);
                    _partialRxVisualLength = formattedPartial.RawTextLength;
                    visibleContribution = ReferenceEquals(prepared.Line, line) ? 1 : 2;
                }

                appendedVisibleLineCount++;
                if (!line.IsPartialRxSegment && CompleteActivePartialRxVisualLine(builder))
                    appendedVisibleLineCount++;
                _retainedVisibleLineContributions.Enqueue(visibleContribution);
                continue;
            }

            if (CompleteActivePartialRxVisualLine(builder))
            {
                appendedVisibleLineCount++;
            }

            var formatted = FormatVisibleLine(prepared.Line, prepared.Highlight);
            if (formatted.HasFormattingError)
            {
                formattingErrors++;
            }

            var taggedDisplayLine = AddVisibleLine(
                formatted.DisplayLine,
                formatted.SearchableLine,
                formatted.PayloadStart,
                retainedLine.LineId,
                line.Timestamp,
                line.Direction,
                hasLineEnding: true);
            builder.Append(taggedDisplayLine);
            appendedVisibleLineCount++;
            visibleContribution = ReferenceEquals(prepared.Line, line) ? 1 : 2;

            if (formatted.IsHighlighted)
            {
                highlightedLines++;
            }

            _retainedVisibleLineContributions.Enqueue(visibleContribution);
        }

        var retainedCount = _retainedLines.Count;
        if (retainedCount > MaxRetainedLineCountSeen)
        {
            MaxRetainedLineCountSeen = retainedCount;
        }

        var versionBeforeTrim = _visibleRebuildVersion;
        var droppedCount = TrimRetainedLinesToCapacity(out var trimCharacterCount);

        DisplayedLineCount += lines.Count;
        DroppedVisibleLineCount += droppedCount + memoryDropped;
        HighlightedLineCount += highlightedLines;
        XtermFormattingErrorCount += formattingErrors;
        RaiseVisibleCountProperties();

        if (memoryDropped > 0 || versionBeforeTrim != _visibleRebuildVersion)
        {
            // A trim may include records appended earlier in this same batch.
            // Resynchronize from bounded history instead of trimming text that
            // has not reached the live terminal yet. Expiring a restored filter
            // prefix can also change payload offsets within a retained line.
            TextRebuilt?.Invoke(this, EventArgs.Empty);
        }
        else if (appendedVisibleLineCount > 0 || builder.Length > 0)
        {
            TextBatchAppended?.Invoke(this, new LogTextBatch(builder.ToString(), trimCharacterCount, Math.Max(1, appendedVisibleLineCount), DisplayedLineCount));
        }
    }

    // Call on the UI thread, like AddRange/Clear. Copy only immutable record references;
    // formatting and file I/O can then run independently while live history changes.
    public IReadOnlyList<LogLine> GetRetainedLinesSnapshot()
    {
        var snapshot = new LogLine[_retainedLines.Count];
        var index = 0;
        foreach (var retained in _retainedLines)
        {
            snapshot[index++] = retained.Line;
        }

        return snapshot;
    }

    public string GetVisibleTextSnapshot()
    {
        if (_visibleLines.Count == 0)
        {
            return string.Empty;
        }

        FlushActivePartialRxLineToNodes();

        var estimatedCapacity = (int)Math.Min(
            SnapshotPreallocationMaxChars,
            Math.Max(0, VisibleCharacterCount));
        var builder = new StringBuilder(estimatedCapacity);
        foreach (var line in _visibleLines)
        {
            builder.Append(line.DisplayText);
            if (line.HasLineEnding) builder.Append(Environment.NewLine);
        }

        return builder.ToString();
    }

    public string GetXtermTextSnapshot()
    {
        if (_visibleLines.Count == 0)
        {
            return string.Empty;
        }

        FlushActivePartialRxLineToNodes();
        var estimatedCapacity = (int)Math.Min(
            SnapshotPreallocationMaxChars,
            Math.Max(0, VisibleCharacterCount + ((long)_visibleLines.Count * 40)));
        var builder = new StringBuilder(estimatedCapacity);
        foreach (var line in _visibleLines)
        {
            builder.Append(FormatXtermLineIdentity(line.Id, line.Timestamp));
            builder.Append(line.DisplayText);
            if (line.HasLineEnding) builder.Append(Environment.NewLine);
        }
        return builder.ToString();
    }

    public IReadOnlyList<string> GetVisibleSearchLinesSnapshot()
    {
        FlushActivePartialRxLineToNodes();
        return _visibleLines.Select(line => line.SearchText).ToArray();
    }

    public IReadOnlyList<VisibleLogSearchLine> GetVisibleSearchContentSnapshot()
    {
        FlushActivePartialRxLineToNodes();
        var results = new VisibleLogSearchLine[_visibleLines.Count];
        var index = 0;
        foreach (var line in _visibleLines)
        {
            results[index++] = new VisibleLogSearchLine(
                line.Id, line.SearchText, line.PayloadStart, line.Direction, line.Timestamp);
        }

        return results;
    }

    public bool TryGetSearchLine(LogLine source, out VisibleLogSearchLine searchLine)
    {
        ArgumentNullException.ThrowIfNull(source);
        FlushActivePartialRxLineToNodes();
        long targetLineId = 0;
        long activePartialLineId = 0;
        using var contributions = _retainedVisibleLineContributions.GetEnumerator();
        foreach (var retained in _retainedLines)
        {
            if (!contributions.MoveNext())
            {
                break;
            }

            var contribution = contributions.Current;
            if (contribution > 0)
            {
                activePartialLineId = ShouldMergePartialRxVisually(retained.Line)
                    ? retained.LineId
                    : 0;
            }
            else if (retained.Line.IsPartialRxTerminator)
            {
                activePartialLineId = 0;
            }

            // Streaming rule matches can carry a new LogLine with the retained
            // tail prepended. Its sequence and exact metadata still identify
            // the source fragment without guessing from message text.
            var hasSameIdentity = source.SequenceNumber is { } sequence &&
                retained.Line.SequenceNumber == sequence &&
                retained.Line.Direction == source.Direction &&
                retained.Line.ContentMode == source.ContentMode &&
                retained.Line.Timestamp == source.Timestamp;
            if (!ReferenceEquals(retained.Line, source) && !hasSameIdentity)
            {
                continue;
            }

            if (contribution > 0)
            {
                targetLineId = retained.LineId;
            }
            else if (activePartialLineId > 0 && contribution < 0)
            {
                targetLineId = activePartialLineId;
            }
            break;
        }

        if (targetLineId > 0)
        {
            foreach (var visible in _visibleLines)
            {
                if (visible.Id == targetLineId)
                {
                    searchLine = new VisibleLogSearchLine(
                        visible.Id, visible.SearchText, visible.PayloadStart, visible.Direction, visible.Timestamp);
                    return true;
                }
            }
        }

        searchLine = default;
        return false;
    }

    public void AddPendingDropCount(int droppedCount)
    {
        if (droppedCount <= 0)
        {
            return;
        }

        DroppedPendingLineCount += droppedCount;
    }

    public void Clear()
    {
        _visibleRebuildVersion++;
        _highlightMatcher.Reset();
        _filterMatcher.Reset();
        ResetTerminalLineHighlight();
        if (_retainedLines.Count == 0 &&
            _visibleLines.Count == 0)
        {
            return;
        }

        _retainedLines.Clear();
        _retainedMemoryBytes = 0;
        _retainedVisibleLineContributions.Clear();
        _visibleLines.Clear();
        _retainedLines.TrimExcess();
        _retainedVisibleLineContributions.TrimExcess();
        _visibleLines.TrimExcess();
        _lastVisibleLine = null;
        _visibleCharacterCount = 0;
        _partialRxVisualLineActive = false;
        _partialRxVisualLength = 0;
        ClearActivePartialRxBuilders();
        RaiseVisibleCountProperties();
        TextCleared?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildVisibleLinesFromRetained(CancellationToken cancellationToken = default)
    {
        // Re-admit immutable references while formatting so a hidden history
        // cannot allocate its entire expanded projection before being trimmed.
        var retained = _retainedLines.ToArray();
        var liveHighlight = _highlightMatcher;
        var liveFilter = _filterMatcher;
        var liveTerminalHighlight = CaptureTerminalHighlightContext();
        _retainedLines.Clear();
        _retainedMemoryBytes = 0;
        _visibleRebuildVersion++;
        _highlightMatcher = new();
        _filterMatcher = new();
        ResetTerminalLineHighlight();
        _visibleLines.Clear();
        _lastVisibleLine = null;
        _retainedVisibleLineContributions.Clear();
        _visibleCharacterCount = 0;
        _partialRxVisualLineActive = false;
        _partialRxVisualLength = 0;
        ClearActivePartialRxBuilders();

        AppendRebuiltLines(retained, cancellationToken);
        DroppedVisibleLineCount += TrimScreenMemoryAfterRebuild();
        if (_highlightMatcher.LastRxPosition < liveHighlight.LastRxPosition) _highlightMatcher = liveHighlight;
        RestoreTerminalHighlightContext(liveTerminalHighlight);
        _filterMatcher = _filterMatcher.WithLatestContext(liveFilter, liveHighlight, _viewFilters, ActiveRuleMode);
        RaiseVisibleCountProperties();
    }

    private void AppendRebuiltLines(IEnumerable<RetainedLogLine> lines, CancellationToken cancellationToken)
    {
        var formattingErrors = 0;
        foreach (var retainedLine in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = retainedLine.Line;
            var evicted = ReserveScreenMemory(retainedLine);
            DroppedVisibleLineCount += evicted;
            _retainedLines.Enqueue(retainedLine);
            _retainedMemoryBytes += retainedLine.EstimatedBytes;
            var visibleContribution = 0;
            var prepared = PrepareRuleLine(retainedLine);
            if (evicted > 0) prepared = RestoreEvictedMatchPrefix(prepared);
            if (prepared.BreakBefore) CompleteActivePartialRxVisualLine();
            if (line.IsPartialRxTerminator)
            {
                CompleteActivePartialRxVisualLine();
                _retainedVisibleLineContributions.Enqueue(visibleContribution);
                continue;
            }

            if (!prepared.Visible)
            {
                if (line.Direction == LogDirection.Rx && !line.IsPartialRxSegment)
                    CompleteActivePartialRxVisualLine();
                _retainedVisibleLineContributions.Enqueue(visibleContribution);
                continue;
            }

            if (ShouldMergePartialRxVisually(line) ||
                (line.Direction == LogDirection.Rx && !prepared.BreakBefore && _partialRxVisualLineActive))
            {
                var formattedPartial = FormatPartialRxVisibleSegment(prepared.Line, prepared.Highlight);
                if (BreakLongScreenLine(formattedPartial.DisplayLine.Length, formattedPartial.SearchableLine.Length))
                    formattedPartial = FormatPartialRxVisibleSegment(prepared.Line, prepared.Highlight);
                if (formattedPartial.HasFormattingError)
                {
                    formattingErrors++;
                }

                if (_partialRxVisualLineActive && _lastVisibleLine is not null)
                {
                    AppendPartialRxVisualSegment(
                        formattedPartial.DisplayLine,
                        formattedPartial.SearchableLine,
                        retainedLine.LineId,
                        line.Timestamp,
                        line.Direction);
                    visibleContribution = -1;
                }
                else
                {
                    AddVisibleLine(
                        formattedPartial.DisplayLine,
                        formattedPartial.SearchableLine,
                        formattedPartial.PayloadStart,
                        retainedLine.LineId,
                        line.Timestamp,
                        line.Direction);
                    BeginActivePartialRxVisualLine(formattedPartial.DisplayLine, formattedPartial.SearchableLine);
                    _partialRxVisualLineActive = true;
                    _partialRxVisualLength = formattedPartial.RawTextLength;
                    visibleContribution = ReferenceEquals(prepared.Line, line) ? 1 : 2;
                }

                if (!line.IsPartialRxSegment) CompleteActivePartialRxVisualLine();
                _retainedVisibleLineContributions.Enqueue(visibleContribution);
                continue;
            }

            CompleteActivePartialRxVisualLine();
            var formatted = FormatVisibleLine(prepared.Line, prepared.Highlight);
            if (formatted.HasFormattingError)
            {
                formattingErrors++;
            }

            AddVisibleLine(
                formatted.DisplayLine,
                formatted.SearchableLine,
                formatted.PayloadStart,
                retainedLine.LineId,
                line.Timestamp,
                line.Direction,
                hasLineEnding: true);
            _retainedVisibleLineContributions.Enqueue(ReferenceEquals(prepared.Line, line) ? 1 : 2);
        }

        XtermFormattingErrorCount += formattingErrors;
        if (_retainedLines.Count > MaxRetainedLineCountSeen)
        {
            MaxRetainedLineCountSeen = _retainedLines.Count;
        }
    }

    private int TrimRetainedLinesToCapacity(out int trimCharacterCount, bool preserveRuleState = false)
    {
        var droppedCount = 0;
        var removedVisibleLineCount = 0;
        var previousVisibleCharacterCount = VisibleCharacterCount;
        var projectedVisibleCharacterCount = previousVisibleCharacterCount;
        using var visibleLines = _visibleLines.GetEnumerator();
        var needsRebuild = _retainedVisibleLineContributions.Count != _retainedLines.Count;
        var trimMaySplitPartialLine = false;
        // Keep the retained prefix on a complete visual-line boundary. If the
        // capacity falls inside a partial RX group, discard the rest of that
        // oldest group instead of rebuilding every retained line.
        while (_retainedLines.Count > _capacity || NeedsMemoryTrim(projectedVisibleCharacterCount) ||
               (trimMaySplitPartialLine && _retainedLines.Count > 0))
        {
            if (_retainedLines.Count <= _capacity && !NeedsMemoryTrim(projectedVisibleCharacterCount) &&
                trimMaySplitPartialLine &&
                _retainedVisibleLineContributions.Count > 0 &&
                _retainedVisibleLineContributions.Peek() > 0)
            {
                needsRebuild |= _retainedVisibleLineContributions.Peek() == 2;
                trimMaySplitPartialLine = false;
                break;
            }

            var removed = _retainedLines.Dequeue();
            var removedLine = removed.Line;
            _retainedMemoryBytes -= removed.EstimatedBytes;
            var visibleContribution = _retainedVisibleLineContributions.Count > 0
                ? _retainedVisibleLineContributions.Dequeue()
                : 0;

            droppedCount++;
            removedVisibleLineCount += visibleContribution > 0 ? 1 : 0;
            if (visibleContribution > 0 && visibleLines.MoveNext())
                projectedVisibleCharacterCount -= visibleLines.Current.Length;
            if (removedLine.IsPartialRxSegment)
            {
                trimMaySplitPartialLine = true;
            }
            else if (removedLine.IsPartialRxTerminator || visibleContribution > 0)
            {
                trimMaySplitPartialLine = false;
            }
        }

        if (droppedCount > 0)
        {
            // Hidden normal lines do not end the visual partial line. Only a
            // terminator or another visible line proves a safe trim boundary.
            needsRebuild |= trimMaySplitPartialLine;

            if (!needsRebuild)
            {
                for (var index = 0; index < removedVisibleLineCount; index++)
                {
                    if (!RemoveFirstVisibleLine())
                    {
                        needsRebuild = true;
                        break;
                    }
                }
            }

            if (needsRebuild)
            {
                var highlightMatcher = _highlightMatcher;
                var filterMatcher = _filterMatcher;
                if (preserveRuleState)
                {
                    _highlightMatcher = new();
                    _filterMatcher = new();
                }
                RebuildVisibleLinesFromRetained();
                if (preserveRuleState)
                {
                    _highlightMatcher = highlightMatcher;
                    _filterMatcher = filterMatcher;
                }
            }
        }

        trimCharacterCount = (int)Math.Min(
            int.MaxValue,
            Math.Max(0, previousVisibleCharacterCount - VisibleCharacterCount));
        return droppedCount;
    }

    private bool RemoveFirstVisibleLine()
    {
        if (!_visibleLines.TryDequeue(out var removedLine))
        {
            return false;
        }

        _visibleCharacterCount = Math.Max(0, _visibleCharacterCount - removedLine.Length);
        ForgetTerminalHighlightLine(removedLine.Id);
        if (ReferenceEquals(removedLine, _partialRxLine))
        {
            _partialRxVisualLineActive = false;
            _partialRxVisualLength = 0;
            ClearActivePartialRxBuilders();
        }

        if (_visibleLines.Count == 0)
        {
            _lastVisibleLine = null;
        }

        return true;
    }

    private void RaiseVisibleCountProperties()
    {
        OnPropertyChanged(nameof(CurrentVisibleLineCount));
        OnPropertyChanged(nameof(FilteredVisibleLineCount));
        OnPropertyChanged(nameof(TotalRetainedLineCount));
        OnPropertyChanged(nameof(VisibleCharacterCount));
        OnPropertyChanged(nameof(PartialRxVisualLineActive));
        OnPropertyChanged(nameof(PartialRxVisualLength));
        OnPropertyChanged(nameof(PartialRxAppendInPlaceCount));
    }

    private string AddVisibleLine(
        string displayLine,
        string searchableLine,
        int payloadStart,
        long lineId,
        DateTimeOffset timestamp,
        LogDirection direction,
        bool hasLineEnding = false)
    {
        var timestampUnixMilliseconds = timestamp.ToUnixTimeMilliseconds();
        var taggedDisplayLine = FormatXtermLineIdentity(lineId, timestampUnixMilliseconds) + displayLine +
            (hasLineEnding ? Environment.NewLine : string.Empty);
        // Uncolored display and search share the same string. Store the newline
        // separately instead of keeping another full-length copy of every line.
        _lastVisibleLine = new VisibleLine(displayLine, searchableLine, payloadStart, lineId, timestampUnixMilliseconds, direction, hasLineEnding);
        _visibleLines.Enqueue(_lastVisibleLine);
        _visibleCharacterCount += _lastVisibleLine.Length;
        TrackTerminalHighlightLine(_lastVisibleLine);
        return taggedDisplayLine;
    }

    private static string FormatXtermLineIdentity(long lineId, long timestampUnixMilliseconds) =>
        $"{XtermLineIdentityOscPrefix}{lineId.ToString(CultureInfo.InvariantCulture)},{timestampUnixMilliseconds.ToString(CultureInfo.InvariantCulture)}{XtermOscTerminator}";

    private int EstimateLiveBatchCharacterCount(IReadOnlyList<LogLine> lines)
    {
        long estimated = 0;
        foreach (var line in lines)
        {
            long displayTextLength = line.Text.Length;
            if (_rxDisplayMode == RxDisplayMode.Hex &&
                line.Direction == LogDirection.Rx &&
                line.RawBytes is { Length: > 0 } rawBytes)
            {
                displayTextLength = (long)rawBytes.Length * 3;
            }

            estimated += displayTextLength + EstimatedFormattedLineOverheadChars;
            if (estimated >= LiveBatchPreallocationMaxChars)
            {
                return LiveBatchPreallocationMaxChars;
            }
        }

        return (int)Math.Max(0, estimated);
    }

    private void BeginActivePartialRxVisualLine(string displayLine, string searchableLine)
    {
        _partialRxLine = _lastVisibleLine;
        _partialRxDisplayBuilder = new StringBuilder(displayLine);
        _partialRxSearchableBuilder = new StringBuilder(searchableLine);
        _partialRxLineDirty = false;
        _partialRxVisualLineActive = true;
    }

    private void EnsureActivePartialRxBuilders()
    {
        if (_partialRxDisplayBuilder is not null &&
            _partialRxSearchableBuilder is not null &&
            _partialRxLine is not null)
        {
            return;
        }

        if (_lastVisibleLine is null)
        {
            return;
        }

        BeginActivePartialRxVisualLine(_lastVisibleLine.DisplayText, _lastVisibleLine.SearchText);
    }

    private void FlushActivePartialRxLineToNodes()
    {
        if (!_partialRxLineDirty ||
            _partialRxDisplayBuilder is null ||
            _partialRxSearchableBuilder is null ||
            _partialRxLine is null)
        {
            return;
        }

        _partialRxLine.DisplayText = _partialRxDisplayBuilder.ToString();
        _partialRxLine.SearchText = _partialRxSearchableBuilder.ToString();
        _partialRxLineDirty = false;
    }

    private void ClearActivePartialRxBuilders()
    {
        _partialRxDisplayBuilder = null;
        _partialRxSearchableBuilder = null;
        _partialRxLine = null;
        _partialRxLineDirty = false;
    }

    private string AppendPartialRxVisualSegment(
        string displayText,
        string searchableText,
        long lineId,
        DateTimeOffset timestamp,
        LogDirection direction)
    {
        if (_lastVisibleLine is null)
        {
            var taggedDisplayLine = AddVisibleLine(displayText, searchableText, 0, lineId, timestamp, direction);
            BeginActivePartialRxVisualLine(displayText, searchableText);
            _partialRxVisualLength = searchableText.Length;
            return taggedDisplayLine;
        }

        EnsureActivePartialRxBuilders();
        if (_partialRxDisplayBuilder is not null && _partialRxSearchableBuilder is not null)
        {
            _partialRxDisplayBuilder.Append(displayText);
            _partialRxSearchableBuilder.Append(searchableText);
            _partialRxLineDirty = true;
        }
        else
        {
            _lastVisibleLine.DisplayText += displayText;
            _lastVisibleLine.SearchText += searchableText;
        }

        _lastVisibleLine.Length += displayText.Length;
        _visibleCharacterCount += displayText.Length;
        _partialRxVisualLength += searchableText.Length;
        return displayText;
    }

    private bool CompleteActivePartialRxVisualLine(StringBuilder? appendedText = null)
    {
        if (!_partialRxVisualLineActive || _lastVisibleLine is null)
        {
            _partialRxVisualLineActive = false;
            _partialRxVisualLength = 0;
            return false;
        }

        if (_partialRxDisplayBuilder is not null &&
            _partialRxSearchableBuilder is not null &&
            _partialRxLine is not null)
        {
            _partialRxLine.DisplayText = _partialRxDisplayBuilder.ToString();
            _partialRxLine.SearchText = _partialRxSearchableBuilder.ToString();
            ClearActivePartialRxBuilders();
        }
        _lastVisibleLine.HasLineEnding = true;
        _lastVisibleLine.Length += Environment.NewLine.Length;
        _visibleCharacterCount += Environment.NewLine.Length;
        appendedText?.Append(Environment.NewLine);
        _partialRxVisualLineActive = false;
        _partialRxVisualLength = 0;
        return true;
    }

    private bool ShouldMergePartialRxVisually(LogLine line)
    {
        return line.IsPartialRxSegment &&
            line.Direction == LogDirection.Rx;
    }

    private (string DisplayLine, string SearchableLine, int PayloadStart, int RawTextLength, bool HasFormattingError, bool IsHighlighted) FormatPartialRxVisibleSegment(
        LogLine line,
        LogRuleMatcher.CompiledHighlightRule? highlightRule)
    {
        try
        {
            var text = FormatDisplayText(line, _rxDisplayMode);
            if (_partialRxVisualLineActive)
            {
                if (_rxDisplayMode == RxDisplayMode.Hex && text.Length > 0)
                {
                    text = " " + text;
                }

                var searchableText = text;
                var formatted = FormatXtermDisplayLine(line, highlightRule, text, _rxDisplayMode, 0);
                return (formatted.Text, searchableText, 0, searchableText.Length,
                    formatted.HasFormattingError, formatted.IsHighlighted);
            }

            var searchableLine = FormatPlainSafeDisplayLine(
                line,
                _showTimestampInLogView,
                _showRxTxDirectionPrefixInLogView,
                _timestampDisplayFormat,
                _rxDisplayMode,
                out var payloadStart,
                formattedText: text);
            (var displayLine, var highlighted, var hasFormattingError) = FormatXtermDisplayLine(
                line,
                highlightRule,
                searchableLine,
                _rxDisplayMode,
                payloadStart);
            return (displayLine, searchableLine, payloadStart, text.Length, hasFormattingError, highlighted);
        }
        catch
        {
            var payloadStart = 0;
            var fallback = _partialRxVisualLineActive
                ? line.Text
                : FormatPlainSafeDisplayLine(
                    line,
                    _showTimestampInLogView,
                    _showRxTxDirectionPrefixInLogView,
                    _timestampDisplayFormat,
                    _rxDisplayMode,
                    out payloadStart);
            return (fallback, fallback, payloadStart, line.Text.Length, true, false);
        }
    }

    private (string DisplayLine, string SearchableLine, int PayloadStart, bool IsHighlighted, bool HasFormattingError) FormatVisibleLine(
        LogLine line,
        LogRuleMatcher.CompiledHighlightRule? highlightRule)
    {
        string displayLine;
        string searchableLine;
        int payloadStart;
        bool isHighlighted;
        bool hasFormattingError;
        try
        {
            searchableLine = FormatPlainSafeDisplayLine(
                line,
                _showTimestampInLogView,
                _showRxTxDirectionPrefixInLogView,
                _timestampDisplayFormat,
                _rxDisplayMode,
                out payloadStart);
            (displayLine, isHighlighted, hasFormattingError) = FormatXtermDisplayLine(
                line,
                highlightRule,
                searchableLine,
                _rxDisplayMode,
                payloadStart);
        }
        catch
        {
            searchableLine = FormatPlainSafeDisplayLine(
                line,
                _showTimestampInLogView,
                _showRxTxDirectionPrefixInLogView,
                _timestampDisplayFormat,
                _rxDisplayMode,
                out payloadStart);
            displayLine = searchableLine;
            isHighlighted = false;
            hasFormattingError = true;
        }

        return (displayLine, searchableLine, payloadStart, isHighlighted, hasFormattingError);
    }

    private LogRuleMatchMode ActiveRuleMode =>
        _rxDisplayMode == RxDisplayMode.Hex ? LogRuleMatchMode.Hex : LogRuleMatchMode.Terminal;

    private PreparedRuleLine PrepareRuleLine(RetainedLogLine retained, bool captureContext = false,
        StringBuilder? appendedText = null)
    {
        var highlight = _highlightMatcher.Evaluate(retained.Line, retained.HighlightRules, ActiveRuleMode,
            retained.HighlightPrefix, retained.LineId, retained.PreviousRxPosition);
        if (ActiveRuleMode == LogRuleMatchMode.Terminal && retained.Line.Direction == LogDirection.Rx &&
            _terminalLineRules is not null && !ReferenceEquals(_terminalLineRules, retained.HighlightRules))
            highlight = highlight with { BreakBefore = true };
        var filter = _filterMatcher.Evaluate(retained.Line, _viewFilters, ActiveRuleMode,
            retained.FilterPrefix, retained.LineId, retained.PreviousRxPosition);
        var lineHighlight = ResolveTerminalLineHighlight(retained, highlight, appendedText);
        if (captureContext)
            retained = retained with
            {
                HighlightPrefix = highlight.ContextPrefix,
                FilterPrefix = filter.ContextPrefix.Length > highlight.ContextPrefix.Length
                    ? filter.ContextPrefix : highlight.ContextPrefix,
                TerminalLineHighlight = ActiveRuleMode == LogRuleMatchMode.Terminal && retained.Line.Direction == LogDirection.Rx
                    ? lineHighlight : null,
                TerminalLineStart = ActiveRuleMode == LogRuleMatchMode.Terminal && retained.Line.Direction == LogDirection.Rx
                    ? _terminalHighlightStart : 0
            };
        var visibleLine = string.IsNullOrEmpty(filter.HiddenPrefix)
            ? retained.Line
            : StreamingHighlightRuleMatcher.WithText(retained.Line, filter.HiddenPrefix + retained.Line.Text);
        return new(retained, visibleLine, lineHighlight, _viewFilters.Length == 0 || filter.Rule is not null,
            highlight.BreakBefore || (_viewFilters.Length > 0 && filter.BreakBefore), filter.MatchPrefix);
    }

    private PreparedRuleLine RestoreEvictedMatchPrefix(PreparedRuleLine prepared) =>
        _viewFilters.Length == 0 || prepared.MatchPrefix.Length == 0 ? prepared : prepared with
        {
            Line = StreamingHighlightRuleMatcher.WithText(prepared.Source.Line,
                prepared.MatchPrefix + prepared.Source.Line.Text),
            BreakBefore = true
        };

    private static (string Text, bool IsHighlighted, bool HasFormattingError) FormatXtermDisplayLine(
        LogLine line,
        LogRuleMatcher.CompiledHighlightRule? matchedRule,
        string plainLine,
        RxDisplayMode rxDisplayMode,
        int payloadStart)
    {
        // System boundaries and diagnostics must remain visually neutral even when their text
        // happens to match a user highlight rule.
        if (line.Direction == LogDirection.System)
        {
            return ($"{AnsiGray}{plainLine}{AnsiReset}", false, false);
        }

        if (line.Direction == LogDirection.Mark)
        {
            return ($"{AnsiGreen}{plainLine}{AnsiReset}", true, false);
        }

        if (matchedRule is not null)
        {
            if (TryBuildAnsiColor(matchedRule.Rule, out var ruleColor))
            {
                if (!string.IsNullOrWhiteSpace(ruleColor))
                {
                    return ($"{ruleColor}{plainLine}{AnsiReset}", true, false);
                }

                return (plainLine, false, false);
            }

            return (plainLine, false, true);
        }

        if (NormalizeRxDisplayMode(rxDisplayMode) == RxDisplayMode.Terminal)
        {
            var baseColor = line.Direction == LogDirection.Tx
                ? AnsiCyan
                : null;
            if (TryFormatTerminalBracketGroups(plainLine, payloadStart, baseColor, out var bracketText))
            {
                return (bracketText, false, false);
            }
        }

        return line.Direction == LogDirection.Tx
            ? ($"{AnsiCyan}{plainLine}{AnsiReset}", false, false)
            : (plainLine, false, false);
    }

    private string FormatPlainSafeDisplayLine(
        LogLine line,
        bool showTimestamp,
        bool showRxTxDirectionPrefix,
        TimestampDisplayFormat timestampDisplayFormat,
        RxDisplayMode rxDisplayMode,
        out int payloadStart,
        string? formattedText = null)
    {
        var text = formattedText ?? FormatDisplayText(line, rxDisplayMode);
        var directionText = showRxTxDirectionPrefix ||
            line.Direction is not (LogDirection.Rx or LogDirection.Tx)
                ? $"{line.DirectionText} "
                : string.Empty;
        var plainLine = !showTimestamp
            ? $"{directionText}{text}"
            : $"[{line.FormatTimestamp(timestampDisplayFormat)}] {directionText}{text}";
        payloadStart = plainLine.Length - text.Length;
        return plainLine;
    }

    private static bool TryFormatTerminalBracketGroups(
        string text,
        int payloadStart,
        string? baseColor,
        out string formatted)
    {
        formatted = text;
        var openIndex = text.IndexOf('[', Math.Clamp(payloadStart, 0, text.Length));
        if (openIndex < 0)
        {
            return false;
        }

        var closeIndex = text.IndexOf(']', openIndex + 1);
        if (closeIndex < 0)
        {
            return false;
        }

        var builder = new StringBuilder(text.Length + 64);
        if (!string.IsNullOrEmpty(baseColor))
        {
            builder.Append(baseColor);
        }

        var cursor = 0;
        while (openIndex >= 0 && closeIndex >= 0)
        {
            builder.Append(text, cursor, openIndex - cursor);
            builder.Append(AnsiBracketBlue);
            builder.Append(text, openIndex, closeIndex - openIndex + 1);
            builder.Append(AnsiReset);

            cursor = closeIndex + 1;
            if (!string.IsNullOrEmpty(baseColor) && cursor < text.Length)
            {
                builder.Append(baseColor);
            }

            openIndex = text.IndexOf('[', cursor);
            closeIndex = openIndex >= 0
                ? text.IndexOf(']', openIndex + 1)
                : -1;
        }

        builder.Append(text, cursor, text.Length - cursor);
        if (!string.IsNullOrEmpty(baseColor))
        {
            builder.Append(AnsiReset);
        }

        formatted = builder.ToString();
        return true;
    }

    private static TimestampDisplayFormat NormalizeTimestampDisplayFormat(TimestampDisplayFormat timestampDisplayFormat) =>
        Enum.IsDefined(timestampDisplayFormat)
            ? timestampDisplayFormat
            : TimestampDisplayFormat.DateTimeMilliseconds;

    private string FormatDisplayText(LogLine line, RxDisplayMode rxDisplayMode)
    {
        if (line.Direction != LogDirection.Rx)
        {
            return SanitizeForXtermText(_memoryPolicy.Preview(line.Text), preserveTabs: true);
        }

        return rxDisplayMode switch
        {
            RxDisplayMode.Hex => _memoryPolicy.HexPreview(line.RawBytes),
            _ => SanitizeForXtermText(_memoryPolicy.Preview(line.Text), preserveTabs: true)
        };
    }

    private static RxDisplayMode NormalizeRxDisplayMode(RxDisplayMode mode)
    {
        return mode == RxDisplayMode.Hex
            ? RxDisplayMode.Hex
            : RxDisplayMode.Terminal;
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

    private static bool TryBuildAnsiColor(HighlightRule rule, out string? ansiColor)
    {
        ansiColor = null;
        var colorCodes = new List<string>();

        if (!TryGetAnsiColorCode(rule.ForegroundColor, isBackground: false, out var foregroundCode))
        {
            return false;
        }

        if (!TryGetAnsiColorCode(rule.BackgroundColor, isBackground: true, out var backgroundCode))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(backgroundCode))
        {
            colorCodes.Add(string.IsNullOrWhiteSpace(foregroundCode)
                ? AnsiBackgroundContrastForegroundCode
                : foregroundCode);
            colorCodes.Add(backgroundCode);
        }
        else if (!string.IsNullOrWhiteSpace(foregroundCode))
        {
            colorCodes.Add(foregroundCode);
        }

        if (colorCodes.Count == 0)
        {
            return true;
        }

        ansiColor = $"\u001b[{string.Join(';', colorCodes)}m";
        return true;
    }

    private static bool TryGetAnsiColorCode(string? color, bool isBackground, out string? ansiCode)
    {
        ansiCode = null;
        if (string.IsNullOrWhiteSpace(color))
        {
            return true;
        }

        var normalizedColor = color.Trim().ToLowerInvariant();
        if (isBackground)
        {
            ansiCode = normalizedColor switch
            {
                "default" or "none" => null,
                "red" => "48;5;17",
                "orange" => "48;5;18",
                "green" => "48;5;19",
                "yellow" => "48;5;20",
                "blue" => "48;5;21",
                "magenta" => "48;5;22",
                "cyan" => "48;5;23",
                "white" => "48;5;24",
                "gray" or "grey" => "48;5;25",
                "black" => "48;5;26",
                _ => string.Empty
            };

            return ansiCode != string.Empty;
        }

        var foregroundCode = normalizedColor switch
        {
            "default" or "none" => null,
            "black" => "30",
            "red" => "31",
            "orange" => "38;5;208",
            "green" => "32",
            "yellow" => "33",
            "blue" => "34",
            "magenta" => "35",
            "cyan" => "36",
            "white" => "37",
            "gray" or "grey" => "90",
            "brightred" or "bright red" => "91",
            "brightgreen" or "bright green" => "92",
            "brightyellow" or "bright yellow" => "93",
            "brightblue" or "bright blue" => "94",
            "brightmagenta" or "bright magenta" => "95",
            "brightcyan" or "bright cyan" => "96",
            "brightwhite" or "bright white" => "97",
            _ => string.Empty
        };

        if (foregroundCode == string.Empty)
        {
            return false;
        }

        if (foregroundCode is null)
        {
            return true;
        }

        ansiCode = foregroundCode;
        return true;
    }

    private static string SanitizeForXtermText(string text, bool preserveTabs)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder? builder = null;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (!char.IsControl(ch) || (preserveTabs && ch == '\t'))
            {
                builder?.Append(ch);
                continue;
            }

            builder ??= new StringBuilder(text.Length + 8).Append(text, 0, i);
            AppendControlCharacter(builder, ch, preserveTabs);
        }

        return builder?.ToString() ?? text;
    }

    private static void AppendControlCharacter(StringBuilder builder, char ch, bool preserveTabs)
    {
        switch (ch)
        {
            case '\r':
                builder.Append("\\r");
                break;
            case '\n':
                builder.Append("\\n");
                break;
            case '\t':
                builder.Append(preserveTabs ? "\t" : "\\t");
                break;
            case '\u001b':
                builder.Append("\\x1B");
                break;
            default:
                builder.Append("\\x");
                builder.Append(((int)ch).ToString("X2", CultureInfo.InvariantCulture));
                break;
        }
    }
}
