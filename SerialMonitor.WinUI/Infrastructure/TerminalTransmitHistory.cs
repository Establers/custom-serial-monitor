using System.Text;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

// Retained UI evidence is separate from live echo and per-write file/protocol
// evidence. Never claim to reconstruct a remote shell's completed command.
internal sealed class TerminalTransmitHistory(Action<LogLine> retain)
{
    internal const int MaxRecordCharacters = 4096;
    private readonly object _gate = new();
    private readonly StringBuilder _raw = new();
    private DateTimeOffset _started;
    private bool _swallowLf;
    private long _pendingCharacters;
    private bool _remoteEdit;

    internal bool MayHavePendingCommand
    {
        get { lock (_gate) return _pendingCharacters > 0 || _remoteEdit; }
    }

    // Called only after a successful write. The same lock protects a final flush
    // after the worker stops; unsent or rejected keys never enter this history.
    internal void Sent(string text, DateTimeOffset timestamp)
    {
        lock (_gate)
        {
            foreach (var rune in text.EnumerateRunes())
            {
                if (_swallowLf && rune.Value == '\n') { _swallowLf = false; continue; }
                _swallowLf = false;
                if (_raw.Length + rune.Utf16SequenceLength > MaxRecordCharacters) FlushCore();
                if (_raw.Length == 0) _started = timestamp;
                _raw.Append(rune.ToString());
                switch (rune.Value)
                {
                    case '\r': case '\n':
                        _swallowLf = rune.Value == '\r';
                        _pendingCharacters = 0; _remoteEdit = false; FlushCore(); break;
                    case 3:
                        _pendingCharacters = 0; _remoteEdit = false; FlushCore(); break;
                    case 8: case 127: _pendingCharacters = Math.Max(0, _pendingCharacters - 1); break;
                    case 9: case 27: _remoteEdit = true; break;
                    default: if (rune.Value >= 32) { if (_pendingCharacters < long.MaxValue) _pendingCharacters++; else _remoteEdit = true; } break;
                }
            }
        }
    }

    internal void Flush() { lock (_gate) FlushCore(); }

    private void FlushCore()
    {
        if (_raw.Length == 0) return;
        var raw = _raw.ToString();
        _raw.Clear();
        var display = new StringBuilder();
        foreach (var rune in raw.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '\r': case '\n': break;
                case '\\': display.Append(@"\\"); break;
                case 8: display.Append(@"\b"); break;
                case 9: display.Append(@"\t"); break;
                default:
                    if (Rune.IsControl(rune)) display.Append(@"\x").Append(rune.Value.ToString("X2"));
                    else display.Append(rune.ToString());
                    break;
            }
        }
        retain(new LogLine(_started, LogDirection.Tx, display.ToString(), Encoding.UTF8.GetBytes(raw)));
    }
}
