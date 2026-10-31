using System.Text;

namespace SerialMonitor.Core.Sequences;

// Retains only the unfinished character, never prior decoded text. Callers
// advance this over bounded pages before taking a response-range boundary.
internal sealed class TextDecoderContext
{
    private readonly Decoder _decoder;
    private readonly char[] _chars;
    private readonly byte[] _tail;
    private long _position, _characterStart;

    public TextDecoderContext(Encoding encoding)
    {
        var decoding = (Encoding)encoding.Clone();
        decoding.DecoderFallback = new TextResponsePattern.InvalidDataFallback(end =>
            _characterStart = _position - 1 + end);
        _decoder = decoding.GetDecoder();
        _chars = new char[Math.Max(2, decoding.GetMaxCharCount(1))];
        _tail = new byte[Math.Max(4, decoding.GetMaxByteCount(1))];
    }

    public void Observe(ReadOnlySpan<byte> bytes)
    {
        Span<byte> input = stackalloc byte[1];
        foreach (var value in bytes)
        {
            _tail[(int)(_position % _tail.Length)] = value;
            input[0] = value; _position++;
            _decoder.Convert(input, _chars, false, out _, out var count, out _);
            if (count > 0) _characterStart = _position;
        }
    }

    public byte[] PendingBytes()
    {
        var count = checked((int)(_position - _characterStart));
        if (count < 0 || count > _tail.Length) throw new InvalidOperationException("Unsupported decoder context size.");
        var result = new byte[count];
        for (var index = 0; index < count; index++)
            result[index] = _tail[(int)((_characterStart + index) % _tail.Length)];
        return result;
    }
}
