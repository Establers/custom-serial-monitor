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

public sealed class LogViewModel : ViewModelBase
{
    // Rule snapshots are shared by all lines received under the same rule version.
    private readonly record struct RetainedLogLine(
        LogLine Line,
        LogRuleMatcher.CompiledHighlightRule[] HighlightRules,
        LogRuleMatcher.CompiledHighlightRule? ViewFilter);

    private const string AnsiReset = "\u001b[0m";
    private const int SnapshotPreallocationMaxChars = 64 * 1024 * 1024;
    private const int LiveBatchPreallocationMaxChars = 1024 * 1024;
    private const int EstimatedFormattedLineOverheadChars = 64;
    private const string AnsiBracketBlue = "\u001b[38;2;207;232;255m";
    private const string AnsiCyan = "\u001b[36m";
    private const string AnsiGreen = "\u001b[32m";
    private const string AnsiGray = "\u001b[90m";

    private int _capacity;
    private readonly Queue<RetainedLogLine> _retainedLines = new();
    private readonly Queue<int> _retainedVisibleLineContributions = new();
    private readonly LinkedList<int> _visibleLineLengths = new();
    private readonly LinkedList<string> _visibleLines = new();
    private readonly LinkedList<string> _searchableVisibleLines = new();
    private LogRuleMatcher.CompiledHighlightRule[] _highlightRules = Array.Empty<LogRuleMatcher.CompiledHighlightRule>();
    private LogRuleMatcher.CompiledHighlightRule? _viewFilter;
    private bool _showTimestampInLogView = true;
    private TimestampDisplayFormat _timestampDisplayFormat = TimestampDisplayFormat.DateTimeMilliseconds;
    private RxDisplayMode _rxDisplayMode = RxDisplayMode.Terminal;
    private bool _partialRxVisualLineActive;
    private int _partialRxVisualLength;
    private StringBuilder? _partialRxDisplayBuilder;
    private StringBuilder? _partialRxSearchableBuilder;
    private LinkedListNode<string>? _partialRxDisplayNode;
    private LinkedListNode<string>? _partialRxSearchableNode;
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

    public LogViewModel(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        }

        _capacity = capacity;
    }

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

    public int CurrentVisibleLineCount => _visibleLineLengths.Count;

    public int FilteredVisibleLineCount => _visibleLineLengths.Count;

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

        _capacity = capacity;
        var trimmed = TrimRetainedLinesToCapacity(out _);
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
        _showTimestampInLogView = showTimestamp;
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

    public void SetRxDisplayMode(RxDisplayMode mode)
    {
        mode = NormalizeRxDisplayMode(mode);

        if (_rxDisplayMode == mode)
        {
            return;
        }

        _rxDisplayMode = mode;
        RebuildVisibleLinesFromRetained();
        TextRebuilt?.Invoke(this, EventArgs.Empty);
    }

    public void SetViewFilter(HighlightRule? viewFilter, bool rebuildExisting = true)
    {
        _viewFilter = viewFilter is null
            ? null
            : LogRuleMatcher.Compile(viewFilter);
        if (!rebuildExisting)
        {
            return;
        }

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
        foreach (var line in lines)
        {
            var retainedLine = new RetainedLogLine(line, _highlightRules, _viewFilter);
            _retainedLines.Enqueue(retainedLine);
            var visibleContribution = 0;
            if (line.IsPartialRxTerminator)
            {
                if (CompleteActivePartialRxVisualLine(builder))
                {
                    appendedVisibleLineCount++;
                }

                _retainedVisibleLineContributions.Enqueue(visibleContribution);
                continue;
            }

            if (!IsVisibleByFilter(line, retainedLine.ViewFilter))
            {
                _retainedVisibleLineContributions.Enqueue(visibleContribution);
                continue;
            }

            if (ShouldMergePartialRxVisually(line))
            {
                var formattedPartial = FormatPartialRxVisibleSegment(line, retainedLine.HighlightRules);
                if (formattedPartial.HasFormattingError)
                {
                    formattingErrors++;
                }

                if (_partialRxVisualLineActive && _visibleLines.Last is not null)
                {
                    AppendPartialRxVisualSegment(formattedPartial.DisplayLine, formattedPartial.SearchableLine);
                    builder.Append(formattedPartial.DisplayLine);
                    Interlocked.Increment(ref _partialRxAppendInPlaceCount);
                }
                else
                {
                    AddVisibleLine(formattedPartial.DisplayLine, formattedPartial.SearchableLine);
                    BeginActivePartialRxVisualLine(formattedPartial.DisplayLine, formattedPartial.SearchableLine);
                    builder.Append(formattedPartial.DisplayLine);
                    _partialRxVisualLength = formattedPartial.RawTextLength;
                    visibleContribution = 1;
                }

                appendedVisibleLineCount++;
                _retainedVisibleLineContributions.Enqueue(visibleContribution);
                continue;
            }

            if (CompleteActivePartialRxVisualLine(builder))
            {
                appendedVisibleLineCount++;
            }

            var formatted = FormatVisibleLine(line, retainedLine.HighlightRules);
            if (formatted.HasFormattingError)
            {
                formattingErrors++;
            }

            builder.Append(formatted.DisplayLine);
            AddVisibleLine(formatted.DisplayLine, formatted.SearchableLine);
            appendedVisibleLineCount++;
            visibleContribution = 1;

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

        var droppedCount = TrimRetainedLinesToCapacity(out var trimCharacterCount);

        DisplayedLineCount += lines.Count;
        DroppedVisibleLineCount += droppedCount;
        HighlightedLineCount += highlightedLines;
        XtermFormattingErrorCount += formattingErrors;
        RaiseVisibleCountProperties();

        if (appendedVisibleLineCount > 0)
        {
            TextBatchAppended?.Invoke(this, new LogTextBatch(builder.ToString(), trimCharacterCount, appendedVisibleLineCount, DisplayedLineCount));
        }
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
            builder.Append(line);
        }

        return builder.ToString();
    }

    public IReadOnlyList<string> GetVisibleSearchLinesSnapshot()
    {
        FlushActivePartialRxLineToNodes();
        return _searchableVisibleLines.Count == 0
            ? Array.Empty<string>()
            : _searchableVisibleLines.ToArray();
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
        if (_retainedLines.Count == 0 &&
            _visibleLineLengths.Count == 0)
        {
            return;
        }

        _retainedLines.Clear();
        _retainedVisibleLineContributions.Clear();
        _visibleLineLengths.Clear();
        _visibleLines.Clear();
        _searchableVisibleLines.Clear();
        _visibleCharacterCount = 0;
        _partialRxVisualLineActive = false;
        _partialRxVisualLength = 0;
        ClearActivePartialRxBuilders();
        RaiseVisibleCountProperties();
        TextCleared?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildVisibleLinesFromRetained()
    {
        _visibleLineLengths.Clear();
        _visibleLines.Clear();
        _searchableVisibleLines.Clear();
        _retainedVisibleLineContributions.Clear();
        _visibleCharacterCount = 0;
        _partialRxVisualLineActive = false;
        _partialRxVisualLength = 0;
        ClearActivePartialRxBuilders();

        var formattingErrors = 0;
        foreach (var retainedLine in _retainedLines)
        {
            var line = retainedLine.Line;
            var visibleContribution = 0;
            if (line.IsPartialRxTerminator)
            {
                CompleteActivePartialRxVisualLine();
                _retainedVisibleLineContributions.Enqueue(visibleContribution);
                continue;
            }

            if (!IsVisibleByFilter(line, retainedLine.ViewFilter))
            {
                _retainedVisibleLineContributions.Enqueue(visibleContribution);
                continue;
            }

            if (ShouldMergePartialRxVisually(line))
            {
                var formattedPartial = FormatPartialRxVisibleSegment(line, retainedLine.HighlightRules);
                if (formattedPartial.HasFormattingError)
                {
                    formattingErrors++;
                }

                if (_partialRxVisualLineActive && _visibleLines.Last is not null)
                {
                    AppendPartialRxVisualSegment(formattedPartial.DisplayLine, formattedPartial.SearchableLine);
                }
                else
                {
                    AddVisibleLine(formattedPartial.DisplayLine, formattedPartial.SearchableLine);
                    BeginActivePartialRxVisualLine(formattedPartial.DisplayLine, formattedPartial.SearchableLine);
                    _partialRxVisualLineActive = true;
                    _partialRxVisualLength = formattedPartial.RawTextLength;
                    visibleContribution = 1;
                }

                _retainedVisibleLineContributions.Enqueue(visibleContribution);
                continue;
            }

            CompleteActivePartialRxVisualLine();
            var formatted = FormatVisibleLine(line, retainedLine.HighlightRules);
            if (formatted.HasFormattingError)
            {
                formattingErrors++;
            }

            AddVisibleLine(formatted.DisplayLine, formatted.SearchableLine);
            _retainedVisibleLineContributions.Enqueue(1);
        }

        XtermFormattingErrorCount += formattingErrors;
        if (_retainedLines.Count > MaxRetainedLineCountSeen)
        {
            MaxRetainedLineCountSeen = _retainedLines.Count;
        }

        RaiseVisibleCountProperties();
    }

    private int TrimRetainedLinesToCapacity(out int trimCharacterCount)
    {
        var droppedCount = 0;
        var previousVisibleCharacterCount = VisibleCharacterCount;
        var needsRebuild = _retainedVisibleLineContributions.Count != _retainedLines.Count;
        while (_retainedLines.Count > _capacity)
        {
            var removedLine = _retainedLines.Dequeue().Line;
            var visibleContribution = _retainedVisibleLineContributions.Count > 0
                ? _retainedVisibleLineContributions.Dequeue()
                : 0;

            droppedCount++;

            if (removedLine.IsPartialRxSegment || removedLine.IsPartialRxTerminator)
            {
                needsRebuild = true;
            }

            if (!needsRebuild && visibleContribution > 0)
            {
                for (var index = 0; index < visibleContribution; index++)
                {
                    if (!RemoveFirstVisibleLine())
                    {
                        needsRebuild = true;
                        break;
                    }
                }
            }
        }

        if (droppedCount > 0 && needsRebuild)
        {
            RebuildVisibleLinesFromRetained();
        }

        trimCharacterCount = (int)Math.Min(
            int.MaxValue,
            Math.Max(0, previousVisibleCharacterCount - VisibleCharacterCount));
        return droppedCount;
    }

    private bool RemoveFirstVisibleLine()
    {
        if (_visibleLineLengths.First is null ||
            _visibleLines.First is null ||
            _searchableVisibleLines.First is null)
        {
            return false;
        }

        var removedDisplayNode = _visibleLines.First;
        var removedSearchableNode = _searchableVisibleLines.First;
        _visibleCharacterCount = Math.Max(0, _visibleCharacterCount - _visibleLineLengths.First.Value);
        _visibleLineLengths.RemoveFirst();
        _visibleLines.RemoveFirst();
        _searchableVisibleLines.RemoveFirst();
        if (ReferenceEquals(removedDisplayNode, _partialRxDisplayNode) ||
            ReferenceEquals(removedSearchableNode, _partialRxSearchableNode))
        {
            _partialRxVisualLineActive = false;
            _partialRxVisualLength = 0;
            ClearActivePartialRxBuilders();
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

    private void AddVisibleLine(string displayLine, string searchableLine)
    {
        _visibleLineLengths.AddLast(displayLine.Length);
        _visibleLines.AddLast(displayLine);
        _searchableVisibleLines.AddLast(searchableLine);
        _visibleCharacterCount += displayLine.Length;
    }

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
        _partialRxDisplayNode = _visibleLines.Last;
        _partialRxSearchableNode = _searchableVisibleLines.Last;
        _partialRxDisplayBuilder = new StringBuilder(displayLine);
        _partialRxSearchableBuilder = new StringBuilder(searchableLine);
        _partialRxLineDirty = false;
        _partialRxVisualLineActive = true;
    }

    private void EnsureActivePartialRxBuilders()
    {
        if (_partialRxDisplayBuilder is not null &&
            _partialRxSearchableBuilder is not null &&
            _partialRxDisplayNode is not null &&
            _partialRxSearchableNode is not null)
        {
            return;
        }

        if (_visibleLines.Last is null || _searchableVisibleLines.Last is null)
        {
            return;
        }

        BeginActivePartialRxVisualLine(_visibleLines.Last.Value, _searchableVisibleLines.Last.Value);
    }

    private void FlushActivePartialRxLineToNodes()
    {
        if (!_partialRxLineDirty ||
            _partialRxDisplayBuilder is null ||
            _partialRxSearchableBuilder is null ||
            _partialRxDisplayNode is null ||
            _partialRxSearchableNode is null)
        {
            return;
        }

        _partialRxDisplayNode.Value = _partialRxDisplayBuilder.ToString();
        _partialRxSearchableNode.Value = _partialRxSearchableBuilder.ToString();
        _partialRxLineDirty = false;
    }

    private void ClearActivePartialRxBuilders()
    {
        _partialRxDisplayBuilder = null;
        _partialRxSearchableBuilder = null;
        _partialRxDisplayNode = null;
        _partialRxSearchableNode = null;
        _partialRxLineDirty = false;
    }

    private void AppendPartialRxVisualSegment(string displayText, string searchableText)
    {
        if (_visibleLines.Last is null ||
            _searchableVisibleLines.Last is null ||
            _visibleLineLengths.Last is null)
        {
            AddVisibleLine(displayText, searchableText);
            BeginActivePartialRxVisualLine(displayText, searchableText);
            _partialRxVisualLength = searchableText.Length;
            return;
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
            _visibleLines.Last.Value += displayText;
            _searchableVisibleLines.Last.Value += searchableText;
        }

        _visibleLineLengths.Last.Value += displayText.Length;
        _visibleCharacterCount += displayText.Length;
        _partialRxVisualLength += searchableText.Length;
    }

    private bool CompleteActivePartialRxVisualLine(StringBuilder? appendedText = null)
    {
        if (!_partialRxVisualLineActive || _visibleLines.Last is null || _visibleLineLengths.Last is null)
        {
            _partialRxVisualLineActive = false;
            _partialRxVisualLength = 0;
            return false;
        }

        if (_partialRxDisplayBuilder is not null &&
            _partialRxSearchableBuilder is not null &&
            _partialRxDisplayNode is not null &&
            _partialRxSearchableNode is not null)
        {
            _partialRxDisplayBuilder.Append(Environment.NewLine);
            _partialRxDisplayNode.Value = _partialRxDisplayBuilder.ToString();
            _partialRxSearchableNode.Value = _partialRxSearchableBuilder.ToString();
            ClearActivePartialRxBuilders();
        }
        else
        {
            _visibleLines.Last.Value += Environment.NewLine;
        }

        _visibleLineLengths.Last.Value += Environment.NewLine.Length;
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

    private (string DisplayLine, string SearchableLine, int RawTextLength, bool HasFormattingError) FormatPartialRxVisibleSegment(
        LogLine line,
        IEnumerable<LogRuleMatcher.CompiledHighlightRule> highlightRules)
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
                var displayText = _rxDisplayMode == RxDisplayMode.Terminal &&
                    TryFormatTerminalBracketGroups(text, payloadStart: 0, baseColor: null, out var bracketText)
                        ? bracketText
                        : text;
                return (displayText, searchableText, searchableText.Length, false);
            }

            var searchableLine = FormatPlainSafeDisplayLine(
                line,
                _showTimestampInLogView,
                _timestampDisplayFormat,
                _rxDisplayMode,
                out var payloadStart);
            (var displayLine, _, var hasFormattingError) = FormatXtermDisplayLine(
                line,
                highlightRules,
                searchableLine,
                _rxDisplayMode,
                payloadStart);
            return (displayLine, searchableLine, text.Length, hasFormattingError);
        }
        catch
        {
            var fallback = _partialRxVisualLineActive
                ? line.Text
                : _showTimestampInLogView
                    ? line.Format(_timestampDisplayFormat)
                    : $"{line.DirectionText} {line.Text}";
            return (fallback, fallback, line.Text.Length, true);
        }
    }

    private (string DisplayLine, string SearchableLine, bool IsHighlighted, bool HasFormattingError) FormatVisibleLine(
        LogLine line,
        IEnumerable<LogRuleMatcher.CompiledHighlightRule> highlightRules)
    {
        string displayLine;
        string searchableLine;
        bool isHighlighted;
        bool hasFormattingError;
        try
        {
            searchableLine = FormatPlainSafeDisplayLine(
                line,
                _showTimestampInLogView,
                _timestampDisplayFormat,
                _rxDisplayMode,
                out var payloadStart);
            (displayLine, isHighlighted, hasFormattingError) = FormatXtermDisplayLine(
                line,
                highlightRules,
                searchableLine,
                _rxDisplayMode,
                payloadStart);
        }
        catch
        {
            searchableLine = _showTimestampInLogView
                ? line.Format(_timestampDisplayFormat)
                : $"{line.DirectionText} {line.Text}";
            displayLine = searchableLine;
            isHighlighted = false;
            hasFormattingError = true;
        }

        return (displayLine + Environment.NewLine, searchableLine, isHighlighted, hasFormattingError);
    }

    private bool IsVisibleByFilter(
        LogLine line,
        LogRuleMatcher.CompiledHighlightRule? viewFilter)
    {
        if (viewFilter is null)
        {
            return true;
        }

        try
        {
            var matched = IsRuleMatch(line, viewFilter, _rxDisplayMode, out var matchError);
            if (!string.IsNullOrWhiteSpace(matchError))
            {
                ViewFilterMatchErrorCount++;
                return false;
            }

            return matched;
        }
        catch
        {
            ViewFilterMatchErrorCount++;
            return false;
        }
    }

    private static (string Text, bool IsHighlighted, bool HasFormattingError) FormatXtermDisplayLine(
        LogLine line,
        IEnumerable<LogRuleMatcher.CompiledHighlightRule> highlightRules,
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

        var matchedRule = ResolveHighlightRule(line, highlightRules, rxDisplayMode);
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

    private static string FormatPlainSafeDisplayLine(
        LogLine line,
        bool showTimestamp,
        TimestampDisplayFormat timestampDisplayFormat,
        RxDisplayMode rxDisplayMode,
        out int payloadStart)
    {
        var text = FormatDisplayText(line, rxDisplayMode);
        var plainLine = !showTimestamp
            ? $"{line.DirectionText} {text}"
            : $"[{line.FormatTimestamp(timestampDisplayFormat)}] {line.DirectionText} {text}";
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

    private static string FormatDisplayText(LogLine line, RxDisplayMode rxDisplayMode)
    {
        if (line.Direction != LogDirection.Rx)
        {
            return SanitizeForXtermText(line.Text, preserveTabs: true);
        }

        return rxDisplayMode switch
        {
            RxDisplayMode.Hex => FormatRawBytesAsHex(line.RawBytes),
            _ => SanitizeForXtermText(line.Text, preserveTabs: true)
        };
    }

    private static RxDisplayMode NormalizeRxDisplayMode(RxDisplayMode mode)
    {
        return mode == RxDisplayMode.Hex
            ? RxDisplayMode.Hex
            : RxDisplayMode.Terminal;
    }

    private static string FormatRawBytesAsHex(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
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

    private static LogRuleMatcher.CompiledHighlightRule? ResolveHighlightRule(
        LogLine line,
        IEnumerable<LogRuleMatcher.CompiledHighlightRule> highlightRules,
        RxDisplayMode rxDisplayMode)
    {
        LogRuleMatcher.CompiledHighlightRule? bestMatch = null;
        foreach (var rule in highlightRules)
        {
            if (!IsRuleMatch(line, rule, rxDisplayMode, out _))
            {
                continue;
            }

            if (bestMatch is null || rule.Rule.Priority > bestMatch.Rule.Priority)
            {
                bestMatch = rule;
            }
        }

        return bestMatch;
    }

    private static bool IsRuleMatch(
        LogLine line,
        LogRuleMatcher.CompiledHighlightRule rule,
        RxDisplayMode rxDisplayMode,
        out string? error)
    {
        var activeMode = NormalizeRxDisplayMode(rxDisplayMode) == RxDisplayMode.Hex
            ? LogRuleMatchMode.Hex
            : LogRuleMatchMode.Terminal;
        return LogRuleMatcher.IsMatch(line, rule, activeMode, out error);
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

        if (!string.IsNullOrWhiteSpace(foregroundCode))
        {
            colorCodes.Add(foregroundCode);
        }

        if (!string.IsNullOrWhiteSpace(rule.BackgroundColor))
        {
            if (!TryGetAnsiColorCode(rule.BackgroundColor, isBackground: true, out var backgroundCode))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(backgroundCode))
            {
                colorCodes.Add(backgroundCode);
            }
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

        var foregroundCode = color.Trim().ToLowerInvariant() switch
        {
            "default" or "none" => null,
            "black" => "30",
            "red" => "31",
            "orange" => isBackground ? "48;5;208" : "38;5;208",
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

        if (!isBackground)
        {
            ansiCode = foregroundCode;
            return true;
        }

        if (foregroundCode.Contains(';', StringComparison.Ordinal))
        {
            ansiCode = foregroundCode;
            return true;
        }

        var code = int.Parse(foregroundCode, CultureInfo.InvariantCulture);
        ansiCode = (code + 10).ToString(CultureInfo.InvariantCulture);
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
