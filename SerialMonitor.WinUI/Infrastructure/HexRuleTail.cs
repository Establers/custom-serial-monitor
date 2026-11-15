using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

// Immutable byte suffixes: at most pattern.Length - 1 bytes per rule. A suffix
// alone cannot repeat a match, and an unterminated HEX group cannot grow it.
internal static class HexRuleTail
{
    internal static byte[] Join(byte[]? prefix, byte[]? bytes)
    {
        if (prefix is not { Length: > 0 }) return bytes ?? [];
        if (bytes is not { Length: > 0 }) return prefix;
        var joined = new byte[checked(prefix.Length + bytes.Length)];
        prefix.CopyTo(joined, 0);
        bytes.CopyTo(joined, prefix.Length);
        return joined;
    }

    internal static byte[] Keep(byte[] bytes, int limit) =>
        limit <= 0 ? [] : bytes.AsSpan(Math.Max(0, bytes.Length - limit)).ToArray();

    internal static LogLine WithPrefix(LogLine line, byte[]? prefix)
    {
        if (prefix is not { Length: > 0 }) return line;
        var bytes = Join(prefix, line.RawBytes);
        var text = HexFormatter.Format(bytes);
        return new(line.Timestamp, line.Direction, text, bytes, line.SequenceNumber,
            line.IsPartialRxSegment, line.IsPartialRxTerminator, contentMode: line.ContentMode,
            isRxStreamBoundary: line.IsRxStreamBoundary);
    }
}
