using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

// One instance per projection/worker. HEX patterns are record-local; only
// Terminal RX keeps a bounded suffix, never a complete unterminated line.
internal sealed class StreamingHighlightRuleMatcher
{
    internal readonly record struct MatchResult(
        LogRuleMatcher.CompiledHighlightRule? Rule, string HiddenPrefix, bool BreakBefore,
        string ContextPrefix, string MatchPrefix);

    private readonly Dictionary<LogRuleMatcher.CompiledHighlightRule, string> _tails = new();
    private object? _rules;
    private LogRuleMatchMode _activeMode;
    private LogRuleMatchMode _contentMode;
    private long? _lastRxSequence;
    private bool _previousRxPartial;
    private int _hiddenCharacters;
    private long _lastRxPosition;

    internal int RetainedCharacterCount => _tails.Values.Sum(tail => tail.Length);
    internal long LastRxPosition => _lastRxPosition;

    public void Reset()
    {
        _tails.Clear();
        _rules = null;
        _lastRxSequence = null;
        _previousRxPartial = false;
        _hiddenCharacters = 0;
        _lastRxPosition = 0;
    }

    // A screen-only rejected record is absent from a filter worker's history.
    // Keep its live suffix when publishing, rebinding only identical patterns.
    // Unseen patterns cannot inherit stale context across that missing record.
    internal StreamingHighlightRuleMatcher WithLatestContext(
        StreamingHighlightRuleMatcher live, StreamingHighlightRuleMatcher highlight,
        LogRuleMatcher.CompiledHighlightRule[] rules, LogRuleMatchMode mode)
    {
        var latest = live._lastRxPosition >= highlight._lastRxPosition ? live : highlight;
        if (_lastRxPosition >= latest._lastRxPosition) return this;
        var result = new StreamingHighlightRuleMatcher
        {
            _rules = rules, _activeMode = mode, _contentMode = latest._contentMode,
            _lastRxSequence = latest._lastRxSequence, _previousRxPartial = latest._previousRxPartial,
            _lastRxPosition = latest._lastRxPosition
        };
        foreach (var source in new[] { live, highlight })
        {
            if (source._lastRxPosition != latest._lastRxPosition || source._activeMode != mode) continue;
            foreach (var rule in rules)
            foreach (var (previous, tail) in source._tails)
            {
                if (rule.IsTerminalRule && previous.IsTerminalRule &&
                    rule.Rule.Keyword == previous.Rule.Keyword &&
                    rule.Rule.CaseSensitive == previous.Rule.CaseSensitive)
                    result._tails[rule] = tail;
            }
        }
        result._hiddenCharacters = result._tails.Values.Select(tail => tail.Length).DefaultIfEmpty().Max();
        return result;
    }

    public MatchResult Evaluate(LogLine line,
        LogRuleMatcher.CompiledHighlightRule[] rules, LogRuleMatchMode activeMode,
        string retainedPrefix = "", long position = 0, long? previousRxPosition = null)
    {
        var isRx = line.Direction == LogDirection.Rx;
        var breakBefore = isRx && (!_previousRxPartial || _contentMode != line.ContentMode ||
            (previousRxPosition.HasValue && previousRxPosition.Value != _lastRxPosition) ||
            (_lastRxSequence is { } previous && line.SequenceNumber is { } current && current != previous + 1));
        if (!ReferenceEquals(_rules, rules) || _activeMode != activeMode || breakBefore || line.IsPartialRxTerminator)
        {
            _tails.Clear();
            _hiddenCharacters = 0;
        }
        _rules = rules;
        _activeMode = activeMode;
        if (isRx)
        {
            _lastRxSequence = line.SequenceNumber;
            _contentMode = line.ContentMode;
            _previousRxPartial = line.IsPartialRxSegment && !line.IsPartialRxTerminator;
            _lastRxPosition = position;
        }
        if (line.IsPartialRxTerminator)
            return new(null, string.Empty, true, string.Empty, string.Empty);

        LogRuleMatcher.CompiledHighlightRule? best = null;
        var prefix = string.Empty;
        var contextPrefix = string.Empty;
        var matchPrefix = string.Empty;
        var maxRetainedLength = 0;
        foreach (var rule in rules)
        {
            var source = rule.Rule;
            var candidate = line;
            string? tail = null;
            var restoredContext = false;
            if (isRx && activeMode == LogRuleMatchMode.Terminal && rule.IsTerminalRule &&
                source.Enabled && !string.IsNullOrWhiteSpace(source.Keyword) &&
                source.MatchDirection != HighlightMatchDirection.TxOnly)
            {
                _tails.TryGetValue(rule, out tail);
                var retainedLength = Math.Min(source.Keyword.Length - 1, retainedPrefix.Length);
                if (retainedLength > (tail?.Length ?? 0))
                {
                    tail = retainedPrefix[^retainedLength..];
                    restoredContext = true;
                }
                if ((tail?.Length ?? 0) > contextPrefix.Length) contextPrefix = tail!;
                var text = string.Concat(tail, line.Text);
                if (!string.IsNullOrEmpty(tail))
                    candidate = WithText(line, text);
                var length = Math.Min(source.Keyword.Length - 1, text.Length);
                maxRetainedLength = Math.Max(maxRetainedLength, length);
                if (line.IsPartialRxSegment && length > 0)
                    _tails[rule] = text[^length..];
                else
                    _tails.Remove(rule);
            }

            if (!LogRuleMatcher.IsMatch(candidate, rule, activeMode, out _))
                continue;
            if (best is null || source.Priority > best.Rule.Priority)
                best = rule;
            // Restore only text hidden by the filter, without duplicating a
            // suffix which is already present in the visible partial line.
            var matchStart = tail is { Length: > 0 }
                ? candidate.Text.IndexOf(source.Keyword, rule.TextComparison) : -1;
            var missingPrefix = matchStart >= 0 && matchStart < (tail?.Length ?? 0)
                ? tail!.Length - matchStart : 0;
            if (missingPrefix > matchPrefix.Length) matchPrefix = tail![^missingPrefix..];
            // Cached context bridges screen eviction, not a real RX gap. Start
            // a fresh visual line if its earlier text is no longer available.
            var hiddenLength = restoredContext ? missingPrefix : Math.Min(missingPrefix, _hiddenCharacters);
            if (restoredContext && missingPrefix > 0) breakBefore = true;
            if (hiddenLength > prefix.Length)
                prefix = tail![^hiddenLength..];
        }

        if (isRx)
            _hiddenCharacters = best is null && line.IsPartialRxSegment
                ? (int)Math.Min(maxRetainedLength, (long)_hiddenCharacters + line.Text.Length) : 0;
        return new(best, prefix, breakBefore, contextPrefix, matchPrefix);
    }

    internal static LogLine WithText(LogLine line, string text) => new(
        line.Timestamp, line.Direction, text, line.RawBytes, line.SequenceNumber,
        line.IsPartialRxSegment, line.IsPartialRxTerminator, contentMode: line.ContentMode,
        fileText: line.FileText);
}
