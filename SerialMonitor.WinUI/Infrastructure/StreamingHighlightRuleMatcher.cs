using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

// One instance per projection/worker. Literal Terminal/HEX RX keeps a bounded
// character/byte suffix. Regex keeps at most 16K characters
// until the logical RX line completes; longer lines are skipped.
internal sealed class StreamingHighlightRuleMatcher
{
    internal readonly record struct MatchResult(
        LogRuleMatcher.CompiledHighlightRule? Rule, string HiddenPrefix, bool BreakBefore,
        string ContextPrefix, string MatchPrefix, string RegexPrefix = "", string? Error = null,
        byte[]? HexContextPrefix = null, byte[]? HexHiddenPrefix = null, byte[]? HexMatchPrefix = null);

    private readonly Dictionary<LogRuleMatcher.CompiledHighlightRule, string> _tails = new();
    private readonly Dictionary<LogRuleMatcher.CompiledHighlightRule, byte[]> _hexTails = new();
    private int _hiddenBytes;
    private RegexLineBuffer _regexBuffer = new();
    private object? _rules;
    private LogRuleMatchMode _activeMode;
    private LogRuleMatchMode _contentMode;
    private long? _lastRxSequence;
    private bool _previousRxPartial;
    private int _hiddenCharacters;
    private long _lastRxPosition;
    private bool _afterBoundary;

    internal int RetainedCharacterCount => _regexBuffer.Length + _tails.Values.Sum(tail => tail.Length);
    internal int RetainedByteCount => _hexTails.Values.Sum(tail => tail.Length);
    internal bool PreviousRxPartial => _previousRxPartial;
    internal long LastRxPosition => _lastRxPosition;

    public void Reset()
    {
        _tails.Clear();
        _hexTails.Clear();
        _hiddenBytes = 0;
        _regexBuffer.Reset();
        _rules = null;
        _lastRxSequence = null;
        _previousRxPartial = false;
        _hiddenCharacters = 0;
        _lastRxPosition = 0;
        _afterBoundary = false;
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
        // Regex context is line-wide, independent of the pattern. A filter
        // without regex may have no buffer while highlighting kept the full
        // prefix (or the overflow marker) for the same input position.
        var regexSource = new[] { live, highlight }
            .Where(source => source._lastRxPosition == latest._lastRxPosition && source._activeMode == mode)
            .OrderByDescending(source => source._regexBuffer.Prefix.Length).FirstOrDefault();
        var result = new StreamingHighlightRuleMatcher
        {
            _rules = rules, _activeMode = mode, _contentMode = latest._contentMode,
            _lastRxSequence = latest._lastRxSequence, _previousRxPartial = latest._previousRxPartial,
            _lastRxPosition = latest._lastRxPosition,
            _regexBuffer = regexSource?._regexBuffer.Copy() ?? new(), _afterBoundary = latest._afterBoundary
        };
        if (result._previousRxPartial && result._regexBuffer.Prefix.Length == 0)
            result._regexBuffer.Invalidate();
        foreach (var source in new[] { live, highlight })
        {
            if (source._lastRxPosition != latest._lastRxPosition || source._activeMode != mode) continue;
            foreach (var rule in rules)
            foreach (var (previous, tail) in source._tails)
            {
                if (rule.IsTerminalRule && previous.IsTerminalRule &&
                    rule.Rule.Keyword == previous.Rule.Keyword &&
                    rule.Rule.CaseSensitive == previous.Rule.CaseSensitive &&
                    rule.Rule.UseRegex == previous.Rule.UseRegex)
                    result._tails[rule] = tail;
            }
            foreach (var rule in rules)
            foreach (var (previous, tail) in source._hexTails)
                if (rule.IsHexRule && previous.IsHexRule && rule.HexPattern!.AsSpan().SequenceEqual(previous.HexPattern))
                    result._hexTails[rule] = tail;
        }
        result._hiddenCharacters = Math.Max(result._regexBuffer.Length, result._tails.Values.Select(tail => tail.Length).DefaultIfEmpty().Max());
        result._hiddenBytes = result._hexTails.Values.Select(tail => tail.Length).DefaultIfEmpty().Max();
        return result;
    }

    public MatchResult Evaluate(LogLine line,
        LogRuleMatcher.CompiledHighlightRule[] rules, LogRuleMatchMode activeMode,
        string retainedPrefix = "", long position = 0, long? previousRxPosition = null,
        string regexRetainedPrefix = "", bool continuesRxLine = false, byte[]? hexRetainedPrefix = null)
    {
        var isRx = line.Direction == LogDirection.Rx;
        var previousRxPartial = _previousRxPartial;
        if (line.IsRxStreamBoundary)
        {
            Reset();
            _afterBoundary = true;
            _lastRxPosition = position;
            return new(null, "", true, "", "");
        }
        var sequenceGap = _lastRxSequence is { } previous && line.SequenceNumber is { } current && current != previous + 1;
        var breakBefore = isRx && (!_previousRxPartial || _contentMode != line.ContentMode ||
            (previousRxPosition.HasValue && previousRxPosition.Value != _lastRxPosition) ||
            sequenceGap);
        var rulesChanged = !ReferenceEquals(_rules, rules) || _activeMode != activeMode;
        if (rulesChanged || breakBefore)
        {
            _tails.Clear();
            _hexTails.Clear();
            _hiddenBytes = 0;
            _regexBuffer.Reset();
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
        var hasRegex = activeMode == LogRuleMatchMode.Terminal && rules.Any(r => r.Rule.UseRegex && r.Rule.Enabled);
        if (_afterBoundary && isRx) { _regexBuffer.Invalidate(); _afterBoundary = false; }
        if (hasRegex && (rulesChanged && previousRxPartial ||
            isRx && (sequenceGap || (breakBefore || rulesChanged) && continuesRxLine)) && regexRetainedPrefix.Length == 0)
            _regexBuffer.Invalidate();
        var bufferedPrefix = hasRegex && isRx ? _regexBuffer.Prefix : "";
        var regexPrefix = regexRetainedPrefix.Length > bufferedPrefix.Length ? regexRetainedPrefix : bufferedPrefix;
        var regexLine = hasRegex ? _regexBuffer.Append(line, regexRetainedPrefix) : null;
        string? matchError = null;
        if (line.IsPartialRxTerminator) { _tails.Clear(); _hexTails.Clear(); }

        LogRuleMatcher.CompiledHighlightRule? best = null;
        var prefix = string.Empty;
        var contextPrefix = string.Empty;
        var matchPrefix = string.Empty;
        var maxRetainedLength = 0;
        byte[] hexContext = [], hexHidden = [], hexMatch = [];
        var maxRetainedBytes = 0;
        foreach (var rule in rules)
        {
            var source = rule.Rule;
            if (source.UseRegex)
            {
                maxRetainedLength = Math.Max(maxRetainedLength, _regexBuffer.Length);
                if (regexLine is null) continue;
                if (!LogRuleMatcher.IsMatch(WithCompletedText(line, regexLine), rule, activeMode, out var regexError))
                {
                    if (regexError is not null) matchError = source.Name + ": " + regexError;
                    continue;
                }
                if (best is null || source.Priority > best.Rule.Priority) best = rule;
                if (isRx)
                {
                    var tailLength = Math.Max(0, regexLine.Length - line.Text.Length);
                    var regexTail = regexLine[..tailLength];
                    if (regexTail.Length > matchPrefix.Length) matchPrefix = regexTail;
                    var hidden = Math.Min(regexTail.Length, _hiddenCharacters);
                    if (regexRetainedPrefix.Length > 0 && _hiddenCharacters == 0 && breakBefore) hidden = regexTail.Length;
                    if (hidden > prefix.Length) prefix = regexTail[^hidden..];
                }
                continue;
            }
            if (line.IsPartialRxTerminator) continue;
            if (isRx && activeMode == LogRuleMatchMode.Hex && rule.IsHexRule &&
                source.Enabled && source.MatchDirection != HighlightMatchDirection.TxOnly)
            {
                _hexTails.TryGetValue(rule, out var hexTail);
                var retainedLength = Math.Min(rule.HexPattern!.Length - 1, hexRetainedPrefix?.Length ?? 0);
                var restored = retainedLength > (hexTail?.Length ?? 0);
                if (restored) hexTail = HexRuleTail.Keep(hexRetainedPrefix!, retainedLength);
                if ((hexTail?.Length ?? 0) > hexContext.Length) hexContext = hexTail!;
                var bytes = HexRuleTail.Join(hexTail, line.RawBytes);
                var next = HexRuleTail.Keep(bytes, rule.HexPattern.Length - 1);
                maxRetainedBytes = Math.Max(maxRetainedBytes, next.Length);
                if (line.IsPartialRxSegment) _hexTails[rule] = next;
                else _hexTails.Remove(rule);
                var start = bytes.AsSpan().IndexOf(rule.HexPattern);
                if (start < 0) continue;
                if (best is null || source.Priority > best.Rule.Priority) best = rule;
                var missing = Math.Max(0, (hexTail?.Length ?? 0) - start);
                if (missing > hexMatch.Length) hexMatch = hexTail![^missing..];
                var hidden = restored ? missing : Math.Min(missing, _hiddenBytes);
                if (restored && missing > 0) breakBefore = true;
                if (hidden > hexHidden.Length) hexHidden = hexTail![^hidden..];
                continue;
            }
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
        {
            _hiddenCharacters = best is null && line.IsPartialRxSegment
                ? (int)Math.Min(maxRetainedLength, (long)_hiddenCharacters + line.Text.Length) : 0;
            _hiddenBytes = best is null && line.IsPartialRxSegment
                ? (int)Math.Min(maxRetainedBytes, (long)_hiddenBytes + (line.RawBytes?.Length ?? 0)) : 0;
        }
        return new(best, prefix, breakBefore, contextPrefix, matchPrefix, regexPrefix, matchError,
            hexContext, hexHidden, hexMatch);
    }

    internal static LogLine WithCompletedText(LogLine line, string text) => new(
        line.Timestamp, line.Direction, text, line.RawBytes, line.SequenceNumber,
        contentMode: line.ContentMode, fileText: text);

    internal static LogLine WithText(LogLine line, string text) => new(
        line.Timestamp, line.Direction, text, line.RawBytes, line.SequenceNumber,
        line.IsPartialRxSegment, line.IsPartialRxTerminator, contentMode: line.ContentMode,
        fileText: line.FileText, isRxStreamBoundary: line.IsRxStreamBoundary);
}
