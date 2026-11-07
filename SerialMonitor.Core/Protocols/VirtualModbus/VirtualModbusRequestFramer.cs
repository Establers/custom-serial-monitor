namespace SerialMonitor.Core.Protocols.VirtualModbus;

// Explicit request role: never infer an incoming response layout as a request.
// A malformed frame is abandoned through the next idle boundary; do not scan
// its payload for CRC-looking subframes that could mutate register memory.
public sealed class VirtualModbusRequestFramer
{
    private readonly byte[] _buffer = new byte[256];
    private int _count;
    private bool _discard;
    public bool HasPending => _count > 0 || _discard;
    public void Reset() { _count = 0; _discard = false; }

    public void Append(byte value)
    {
        if (_discard) return;
        if (_count == _buffer.Length) { _count = 0; _discard = true; return; }
        _buffer[_count++] = value;
    }

    public byte[]? Flush()
    {
        // A valid CRC prefix is not a frame boundary. Commit only after the
        // complete observed idle-delimited ADU is available (including garbage).
        var frame = _discard ? [] : _count == 0 ? null : _buffer.AsSpan(0, _count).ToArray();
        Reset();
        return frame;
    }
}
