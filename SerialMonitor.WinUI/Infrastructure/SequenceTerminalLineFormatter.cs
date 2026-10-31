using System.Text;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

// Reconstruct terminal lines from decoded fragments, retaining byte-match styling.
// A driver read boundary is neither a newline nor a character boundary.
internal sealed class SequenceTerminalLineFormatter(RxLineEndingMode mode,
    Func<DateTimeOffset?, string> time, List<SequenceTimelineLine> rows,
    SequenceRxPageContinuation continuation = default, bool pendingCrMatched = false)
{
    private readonly List<SequenceTimelinePart> _parts = [];
    private readonly StringBuilder _text = new();
    private bool _matched;
    private bool _previousWasCr = continuation.AfterCr;
    private bool _pendingCr = continuation.AfterCr;
    private bool _pendingCrMatched = pendingCrMatched;
    private bool _suppressEmptyTerminator = continuation.SuppressEmptyTerminator(mode);
    private DateTimeOffset? _lastAt;
    private DateTimeOffset? _pendingCrAt = continuation.LastReceivedAt;

    public void Append(string text, bool matched, DateTimeOffset? at)
    {
        foreach (var value in text)
        {
            if (mode == RxLineEndingMode.Crlf)
            {
                if (_pendingCr)
                {
                    _pendingCr = false;
                    if (value == '\n') { Complete(at); continue; }
                    Add('\r', _pendingCrMatched, _pendingCrAt);
                }
                if (value == '\r') { _pendingCr = true; _pendingCrMatched = matched; _pendingCrAt = at; }
                else Add(value, matched, at);
            }
            else if (mode == RxLineEndingMode.Cr || mode == RxLineEndingMode.Lf)
            {
                if (value == (mode == RxLineEndingMode.Cr ? '\r' : '\n')) Complete(at);
                else Add(value, matched, at);
            }
            else
            {
                if (value == '\r') { Complete(at); _previousWasCr = true; }
                else if (value == '\n')
                {
                    if (!_previousWasCr) Complete(at);
                    _previousWasCr = false;
                }
                else { _previousWasCr = false; Add(value, matched, at); }
            }
        }
    }

    private void Add(char value, bool matched, DateTimeOffset? at)
    {
        if (_matched != matched) FlushPart();
        _matched = matched;
        _text.Append(value);
        _lastAt = at;
    }

    private void FlushPart()
    {
        if (_text.Length == 0) return;
        _parts.Add(new(_text.ToString(), _matched));
        _text.Clear();
    }

    private void Complete(DateTimeOffset? at)
    {
        FlushPart();
        if (_parts.Count > 0 || !_suppressEmptyTerminator)
            rows.Add(new(time(at), "RX", _parts.ToArray()));
        _parts.Clear();
        _lastAt = null;
        _suppressEmptyTerminator = false;
    }

    public void EndInput()
    {
        // Like LineParser.FlushPartial, a trailing CR in CRLF mode remains
        // an incomplete terminator rather than visible payload.
        if (_text.Length > 0 || _parts.Count > 0) Complete(_lastAt);
        _pendingCr = _previousWasCr = false;
        _suppressEmptyTerminator = false;
    }
}
