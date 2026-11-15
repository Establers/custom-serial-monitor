using System.Text;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

// Reveal the retained leading text and subsequent fragments of a matching RX
// line. Matching itself stays in StreamingHighlightRuleMatcher, including the
// requirement that regex waits for the complete line.
internal sealed class TerminalFilterLineContext
{
    private readonly Queue<(long Id, string Text)> _pending = new();
    private int _characters;
    private long _position;
    private object? _rules;
    private bool _matched;
    internal int RetainedCharacterCount => _characters;

    internal void DiscardPendingPrefix() { _pending.Clear(); _characters = 0; }

    internal void Reset()
    {
        _pending.Clear(); _characters = 0; _position = 0; _rules = null; _matched = false;
    }

    internal (bool Visible, string Prefix) Evaluate(LogLine line, long position, long previousRxPosition,
        long firstRetainedId, object rules, bool breakBefore, bool matches, string matchPrefix,
        int maxRecords, int maxCharacters)
    {
        if (line.Direction != LogDirection.Rx) return (matches, "");
        if (breakBefore || !ReferenceEquals(_rules, rules) || _position != previousRxPosition) Reset();
        _position = position;
        _rules = rules;
        while (_pending.TryPeek(out var first) && first.Id < firstRetainedId) DropFirst();
        if (_matched)
        {
            if (line.IsPartialRxTerminator) { Reset(); return (false, ""); }
            return (true, "");
        }
        // A standalone newline may be the first regex match. Return its hidden
        // text before clearing, so the caller can display the completed line.
        if (matches)
        {
            var builder = new StringBuilder(_characters);
            foreach (var part in _pending) builder.Append(part.Text);
            var prefix = matchPrefix.Length > _characters ? matchPrefix : builder.ToString();
            _pending.Clear(); _characters = 0; _matched = true;
            if (line.IsPartialRxTerminator) Reset();
            return (true, prefix);
        }
        if (line.IsPartialRxTerminator) { Reset(); return (false, ""); }
        if (line.IsPartialRxSegment && line.Text.Length > 0)
        {
            var text = line.Text.Length > maxCharacters ? line.Text[^maxCharacters..] : line.Text;
            while (_pending.Count > 0 && (_pending.Count >= maxRecords || (long)_characters + text.Length > maxCharacters)) DropFirst();
            _pending.Enqueue((position, text)); _characters += text.Length;
        }
        if (!line.IsPartialRxSegment) { _pending.Clear(); _characters = 0; }
        return (false, "");
    }

    private void DropFirst() => _characters -= _pending.Dequeue().Text.Length;
}
