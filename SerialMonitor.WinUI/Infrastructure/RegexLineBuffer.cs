using System.Text;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

// Regex anchors need a complete logical line. Never retain an unbounded UART stream.
internal sealed class RegexLineBuffer
{
    private readonly StringBuilder _text = new();
    private bool _overflow;
    private static readonly string OverflowPrefix = new('\0', RuleRegex.MaxInputLength + 1);
    internal int Length => _text.Length;
    internal void Invalidate() { _text.Clear(); _overflow = true; }
    internal void Reset() { _text.Clear(); _overflow = false; }

    internal string? Append(LogLine line, string restoredPrefix = "")
    {
        if (line.IsRxStreamBoundary) { Reset(); return null; }
        if (line.Direction != LogDirection.Rx)
            return line.Text.Length <= RuleRegex.MaxInputLength ? line.Text : null;
        if (!_overflow && restoredPrefix.Length > _text.Length)
        {
            _text.Clear();
            if (restoredPrefix.Length <= RuleRegex.MaxInputLength) _text.Append(restoredPrefix);
            else _overflow = true;
        }
        if (!_overflow && _text.Length + (long)line.Text.Length <= RuleRegex.MaxInputLength)
            _text.Append(line.Text);
        else { _text.Clear(); _overflow = true; }
        if (line.IsPartialRxSegment && !line.IsPartialRxTerminator) return null;
        var completed = _overflow ? null : _text.ToString();
        Reset();
        return completed;
    }

    internal string Prefix => _overflow ? OverflowPrefix : _text.ToString();
    internal RegexLineBuffer Copy()
    {
        var copy = new RegexLineBuffer { _overflow = _overflow };
        copy._text.Append(_text);
        return copy;
    }
}
