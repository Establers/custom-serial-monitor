using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

// Owned by the detector worker. Keep only keyword.Length - 1 characters per
// literal Terminal rule, so retained text alone cannot repeat a match. Regex
// uses a separate bounded buffer and evaluates only completed logical lines.
internal sealed class StreamingEventRuleMatcher
{
    private readonly Dictionary<LogRuleMatcher.CompiledEventRule, string> _tails = new();
    private readonly RegexLineBuffer _regexBuffer = new();
    private string? _regexLine;
    private object? _rules;
    private LogRuleMatchMode _activeMode;
    private LogRuleMatchMode _contentMode;
    private long? _lastRxSequence;
    private bool _previousRxPartial;
    private bool _afterBoundary;

    public void Reset()
    {
        _tails.Clear();
        _regexBuffer.Reset();
        _regexLine = null;
        _rules = null;
        _lastRxSequence = null;
        _previousRxPartial = false;
        _afterBoundary = false;
    }

    public void BeginLine(LogLine line, object rules, LogRuleMatchMode activeMode)
    {
        if (line.IsRxStreamBoundary) { Reset(); _afterBoundary = true; return; }
        if (!ReferenceEquals(_rules, rules) || _activeMode != activeMode)
        {
            _tails.Clear();
            _regexBuffer.Reset();
            if (_previousRxPartial) _regexBuffer.Invalidate();
        }
        _rules = rules;
        _activeMode = activeMode;
        _regexLine = line.Direction == LogDirection.Tx ? line.Text : null;
        if (line.Direction != LogDirection.Rx) return;

        var sequenceGap = _lastRxSequence is { } previous && line.SequenceNumber is { } current && current != previous + 1;
        if (_contentMode != line.ContentMode || sequenceGap)
        {
            _tails.Clear();
            _regexBuffer.Reset();
            if (_previousRxPartial || sequenceGap) _regexBuffer.Invalidate();
        }
        if (line.IsPartialRxTerminator) _tails.Clear();
        if (_afterBoundary) { _regexBuffer.Invalidate(); _afterBoundary = false; }
        var hasRegex = rules is LogRuleMatcher.CompiledEventRule single ? single.Rule.UseRegex
            : rules is LogRuleMatcher.CompiledEventRule[] array && array.Any(r => r.Rule.UseRegex);
        if (hasRegex && activeMode == LogRuleMatchMode.Terminal) _regexLine = _regexBuffer.Append(line);
        _contentMode = line.ContentMode;
        _lastRxSequence = line.SequenceNumber;
        _previousRxPartial = line.IsPartialRxSegment && !line.IsPartialRxTerminator;
    }

    public bool IsMatch(LogLine line, LogRuleMatcher.CompiledEventRule rule,
        LogRuleMatchMode activeMode, out LogLine matchedLine, out string? error)
    {
        matchedLine = line;
        if (rule.Rule.UseRegex)
        {
            error = null;
            if (_regexLine is null) return false;
            matchedLine = StreamingHighlightRuleMatcher.WithCompletedText(line, _regexLine);
            return LogRuleMatcher.IsMatch(matchedLine, rule, activeMode, out error);
        }
        if (line.IsPartialRxTerminator)
        {
            error = null;
            return false;
        }

        var source = rule.Rule;
        if (line.Direction != LogDirection.Rx || activeMode != LogRuleMatchMode.Terminal ||
            !rule.IsTerminalRule || !source.Enabled || string.IsNullOrWhiteSpace(source.Keyword) ||
            source.MatchDirection == EventMatchDirection.TxOnly)
            return LogRuleMatcher.IsMatch(line, rule, activeMode, out error);

        _tails.TryGetValue(rule, out var tail);
        var text = string.Concat(tail, line.Text);
        if (!string.IsNullOrEmpty(tail))
            matchedLine = new LogLine(line.Timestamp, line.Direction, text,
                sequenceNumber: line.SequenceNumber, isPartialRxSegment: line.IsPartialRxSegment,
                contentMode: LogRuleMatchMode.Terminal);

        var retainedLength = Math.Min(source.Keyword.Length - 1, text.Length);
        if (line.IsPartialRxSegment && retainedLength > 0)
            _tails[rule] = text[^retainedLength..];
        else
            _tails.Remove(rule);

        return LogRuleMatcher.IsMatch(matchedLine, rule, activeMode, out error);
    }
}
