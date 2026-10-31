using System.Globalization;
using System.Text;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class LogViewModel
{
    // Only references to retained visible RX lines, plus one winning rule. No
    // complete unterminated RX payload is buffered for matching or recoloring.
    private Queue<VisibleLine> _terminalHighlightLines = new();
    private LogRuleMatcher.CompiledHighlightRule[]? _terminalLineRules;
    private LogRuleMatcher.CompiledHighlightRule? _terminalLineHighlight;
    private long _terminalHighlightPosition;
    private long _terminalHighlightStart;

    private readonly record struct TerminalHighlightContext(
        LogRuleMatcher.CompiledHighlightRule[]? Rules, LogRuleMatcher.CompiledHighlightRule? Winner,
        long Position, long Start);

    private TerminalHighlightContext CaptureTerminalHighlightContext() =>
        new(_terminalLineRules, _terminalLineHighlight, _terminalHighlightPosition, _terminalHighlightStart);

    private void ResetTerminalLineHighlight()
    {
        _terminalHighlightLines.Clear();
        _terminalLineRules = null;
        _terminalLineHighlight = null;
        _terminalHighlightPosition = 0;
        _terminalHighlightStart = 0;
    }

    private void TrackTerminalHighlightLine(VisibleLine line)
    {
        if (_rxDisplayMode == RxDisplayMode.Terminal && line.Direction == LogDirection.Rx)
            _terminalHighlightLines.Enqueue(line);
    }

    private void ForgetTerminalHighlightLine(long removedId)
    {
        while (_terminalHighlightLines.TryPeek(out var line) && line.Id <= removedId)
            _terminalHighlightLines.Dequeue();
    }

    private LogRuleMatcher.CompiledHighlightRule? ResolveTerminalLineHighlight(
        RetainedLogLine retained, StreamingHighlightRuleMatcher.MatchResult match, StringBuilder? appendedText)
    {
        var line = retained.Line;
        if (_rxDisplayMode != RxDisplayMode.Terminal || line.Direction != LogDirection.Rx)
            return match.Rule;

        if (match.BreakBefore || line.IsPartialRxTerminator ||
            !ReferenceEquals(_terminalLineRules, retained.HighlightRules))
        {
            _terminalHighlightLines.Clear();
            _terminalLineHighlight = null;
            _terminalHighlightStart = retained.TerminalLineStart > 0 ? retained.TerminalLineStart : retained.LineId;
        }
        _terminalLineRules = retained.HighlightRules;
        _terminalHighlightPosition = retained.LineId;
        if (line.IsPartialRxTerminator) return null;

        // Receipt-time state survives screen eviction. It is scoped to this
        // rule snapshot, so editing rules never recolors older logical lines.
        var winner = PreferTerminalHighlight(_terminalLineHighlight, retained.TerminalLineHighlight);
        winner = PreferTerminalHighlight(winner, match.Rule);
        if (!ReferenceEquals(winner, _terminalLineHighlight))
        {
            _terminalLineHighlight = winner;
            if (winner is not null) RecolorTerminalLines(winner, appendedText);
        }
        return winner;
    }

    private LogRuleMatcher.CompiledHighlightRule? PreferTerminalHighlight(
        LogRuleMatcher.CompiledHighlightRule? current, LogRuleMatcher.CompiledHighlightRule? candidate)
    {
        if (candidate is null) return current;
        if (current is null || candidate.Rule.Priority > current.Rule.Priority) return candidate;
        if (candidate.Rule.Priority == current.Rule.Priority &&
            Array.IndexOf(_terminalLineRules!, candidate) < Array.IndexOf(_terminalLineRules!, current)) return candidate;
        return current;
    }

    private void RecolorTerminalLines(LogRuleMatcher.CompiledHighlightRule winner, StringBuilder? appendedText)
    {
        FlushActivePartialRxLineToNodes();
        TryBuildAnsiColor(winner.Rule, out var color);
        // OSC 778 only changes already-written cells of the tagged line. New
        // text uses ordinary SGR; snapshots contain only ordinary ANSI text.
        var sgr = string.IsNullOrEmpty(color) ? "0" : color[2..^1];
        foreach (var line in _terminalHighlightLines)
        {
            var display = string.IsNullOrEmpty(color) ? line.SearchText : color + line.SearchText + AnsiReset;
            var delta = display.Length - line.DisplayText.Length;
            line.DisplayText = display;
            line.Length += delta;
            _visibleCharacterCount += delta;
            if (ReferenceEquals(line, _partialRxLine))
            {
                _partialRxDisplayBuilder!.Clear().Append(display);
                _partialRxLineDirty = false;
            }
            appendedText?.Append("\u001b]778;").Append(line.Id.ToString(CultureInfo.InvariantCulture))
                .Append(';').Append(sgr).Append(XtermOscTerminator);
        }
    }

    private void PublishTerminalHighlightState(LogViewModel rebuilt)
    {
        // A worker can miss a record rejected by the screen byte budget. Keep
        // live boundary/winner state without retaining stale visible references.
        var live = CaptureTerminalHighlightContext();
        _terminalLineRules = rebuilt._terminalLineRules;
        _terminalLineHighlight = rebuilt._terminalLineHighlight;
        _terminalHighlightPosition = rebuilt._terminalHighlightPosition;
        _terminalHighlightStart = rebuilt._terminalHighlightStart;
        _terminalHighlightLines = rebuilt._terminalHighlightLines;
        RestoreTerminalHighlightContext(live);
    }

    private void RestoreTerminalHighlightContext(TerminalHighlightContext live)
    {
        if (live.Position <= _terminalHighlightPosition) return;
        if (live.Start != _terminalHighlightStart)
        {
            _terminalHighlightLines.Clear();
            CompleteActivePartialRxVisualLine();
        }
        _terminalLineRules = live.Rules;
        _terminalLineHighlight = live.Winner;
        _terminalHighlightPosition = live.Position;
        _terminalHighlightStart = live.Start;
        if (live.Winner is not null) RecolorTerminalLines(live.Winner, appendedText: null);
    }
}
