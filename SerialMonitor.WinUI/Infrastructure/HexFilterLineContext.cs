using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

// The filter reveals a matching logical HEX line, including its retained
// leading bytes and later fragments. Pending references obey screen limits.
internal sealed class HexFilterLineContext
{
    private readonly Queue<(long Id, byte[] Bytes)> _pending = new();
    private int _bytes;
    private long _position;
    private object? _rules;
    private bool _matched;
    internal int RetainedByteCount => _bytes;

    internal void DiscardPendingPrefix() { _pending.Clear(); _bytes = 0; }

    internal void Reset()
    {
        _pending.Clear(); _bytes = 0; _position = 0; _rules = null; _matched = false;
    }

    internal (bool Visible, byte[] Prefix) Evaluate(LogLine line, long position, long previousRxPosition,
        long firstRetainedId, object rules, bool breakBefore, bool matches, byte[]? matchPrefix,
        int maxRecords, int maxBytes)
    {
        if (line.Direction != LogDirection.Rx) return (matches, []);
        if (breakBefore || !ReferenceEquals(_rules, rules) || _position != previousRxPosition) Reset();
        _position = position;
        _rules = rules;
        while (_pending.TryPeek(out var first) && first.Id < firstRetainedId) DropFirst();
        if (line.IsPartialRxTerminator) { Reset(); return (false, []); }
        if (_matched) return (true, []);
        if (matches)
        {
            var prefix = new byte[_bytes];
            var offset = 0;
            foreach (var part in _pending) { part.Bytes.CopyTo(prefix, offset); offset += part.Bytes.Length; }
            if ((matchPrefix?.Length ?? 0) > prefix.Length) prefix = matchPrefix!;
            _pending.Clear(); _bytes = 0; _matched = true;
            return (true, prefix);
        }
        if (line.IsPartialRxSegment && line.RawBytes is { Length: > 0 } bytes)
        {
            if (bytes.Length > maxBytes) bytes = HexRuleTail.Keep(bytes, maxBytes);
            while (_pending.Count > 0 && (_pending.Count >= maxRecords || (long)_bytes + bytes.Length > maxBytes)) DropFirst();
            _pending.Enqueue((position, bytes)); _bytes += bytes.Length;
        }
        if (!line.IsPartialRxSegment) { _pending.Clear(); _bytes = 0; }
        return (false, []);
    }

    private void DropFirst() => _bytes -= _pending.Dequeue().Bytes.Length;
}
