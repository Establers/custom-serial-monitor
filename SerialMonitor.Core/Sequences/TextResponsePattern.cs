using System.Text;

namespace SerialMonitor.Core.Sequences;

// Decode incrementally so a byte inside a multibyte character is never a text match.
// Only pattern-sized state is retained, including raw byte positions for result highlighting.
internal sealed class TextResponsePattern
{
    private readonly string _pattern;
    private readonly int[] _prefix;
    private readonly long[] _starts;
    private readonly Decoder _decoder;
    private readonly char[] _decoded;
    private int _matched;
    private bool _primed;
    private long _position, _characterStart, _characters;
    public long? MatchOffset { get; private set; }
    public int MatchLength { get; private set; }

    public TextResponsePattern(string pattern, Encoding encoding, ReadOnlySpan<byte> decoderPrefix = default)
    {
        _pattern = pattern;
        _prefix = new int[pattern.Length];
        _starts = new long[pattern.Length];
        for (var i = 1; i < pattern.Length; i++)
        {
            var j = _prefix[i - 1];
            while (j > 0 && pattern[i] != pattern[j]) j = _prefix[j - 1];
            if (pattern[i] == pattern[j]) j++;
            _prefix[i] = j;
        }
        var decoding = (Encoding)encoding.Clone();
        decoding.DecoderFallback = new InvalidDataFallback(end =>
        {
            // The index can be negative when the invalid character began in a
            // previous call. Keep valid text after it at its actual byte offset.
            _matched = 0;
            _characterStart = _position - 1 + end;
        });
        _decoder = decoding.GetDecoder();
        _decoded = new char[Math.Max(2, decoding.GetMaxCharCount(1))];
        if (!decoderPrefix.IsEmpty) Prime(decoderPrefix);
    }

    internal void Prime(ReadOnlySpan<byte> decoderPrefix)
    {
        if (_primed || _position != 0 || _characters != 0 || MatchOffset.HasValue)
            throw new InvalidOperationException("Text context must be set before response matching starts.");
        _primed = true;
        // Negative character starts belong to the context outside the range.
        _position = _characterStart = -decoderPrefix.Length;
        Feed(decoderPrefix);
    }

    public bool Feed(ReadOnlySpan<byte> bytes)
    {
        if (MatchOffset.HasValue) return true;
        Span<byte> input = stackalloc byte[1];
        foreach (var value in bytes)
        {
            input[0] = value; _position++;
            var count = Decode(input);
            for (var i = 0; i < count; i++)
            {
                var valueChar = _decoded[i];
                if (_characterStart < 0) { _matched = 0; continue; }
                _starts[(int)(_characters++ % _starts.Length)] = _characterStart;
                while (_matched > 0 && valueChar != _pattern[_matched]) _matched = _prefix[_matched - 1];
                if (valueChar == _pattern[_matched]) _matched++;
                if (_matched != _pattern.Length) continue;
                var start = _starts[(int)((_characters - _pattern.Length) % _starts.Length)];
                MatchOffset = start; MatchLength = checked((int)(_position - start));
                return true;
            }
            if (count > 0) _characterStart = _position;
        }
        return false;
    }

    private int Decode(ReadOnlySpan<byte> input)
    {
        _decoder.Convert(input, _decoded, false, out _, out var count, out _);
        return count;
    }

    // Invalid bytes are a match boundary, never replacement text. A zero-output
    // fallback lets the decoder recover without throwing for each bad RX byte.
    internal sealed class InvalidDataFallback(Action<int> invalidEnd) : DecoderFallback
    {
        public override int MaxCharCount => 0;
        public override DecoderFallbackBuffer CreateFallbackBuffer() => new Buffer(invalidEnd);

        private sealed class Buffer(Action<int> invalidEnd) : DecoderFallbackBuffer
        {
            public override bool Fallback(byte[] bytesUnknown, int index)
            {
                invalidEnd(index + bytesUnknown.Length);
                return false;
            }
            public override char GetNextChar() => '\0';
            public override bool MovePrevious() => false;
            public override int Remaining => 0;
        }
    }
}
