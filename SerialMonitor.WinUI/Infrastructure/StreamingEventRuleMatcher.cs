using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

// Owned by the detector worker. Keep only keyword.Length - 1 characters per
// Terminal rule, so retained text alone can never repeat a previous match.
internal sealed class StreamingEventRuleMatcher
{
    private readonly Dictionary<LogRuleMatcher.CompiledEventRule, string> _tails = new();
    private object? _rules;
    private LogRuleMatchMode _activeMode;
    private LogRuleMatchMode _contentMode;
    private long? _lastRxSequence;

    public void Reset()
    {
        _tails.Clear();
        _rules = null;
        _lastRxSequence = null;
    }

    public void BeginLine(LogLine line, object rules, LogRuleMatchMode activeMode)
    {
        if (!ReferenceEquals(_rules, rules) || _activeMode != activeMode)
            _tails.Clear();
        _rules = rules;
        _activeMode = activeMode;
        if (line.Direction != LogDirection.Rx)
            return;

        if (line.IsPartialRxTerminator || _contentMode != line.ContentMode ||
            (_lastRxSequence is { } previous && line.SequenceNumber is { } current && current != previous + 1))
            _tails.Clear();
        _contentMode = line.ContentMode;
        _lastRxSequence = line.SequenceNumber;
    }

    public bool IsMatch(LogLine line, LogRuleMatcher.CompiledEventRule rule,
        LogRuleMatchMode activeMode, out LogLine matchedLine, out string? error)
    {
        matchedLine = line;
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
