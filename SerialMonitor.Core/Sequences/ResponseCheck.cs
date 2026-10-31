using System.Text;

namespace SerialMonitor.Core.Sequences;

// Deliberately independent of legacy AutomationResponseExpectation/CaptureMs.
public sealed record ResponseCheck(string Kind, string Pattern, int TimeoutMs = 2000)
{
    public void Validate(Encoding? encoding = null) => _ = ResponsePattern.Compile(this, encoding ?? Encoding.UTF8);
}

public sealed class ResponsePattern
{
    private readonly Dictionary<byte, ulong[]> _masks;
    private readonly ulong[] _wildcards;
    private readonly ulong[] _state;
    private readonly byte[]? _literal;
    private readonly int[]? _prefix;
    private readonly TextResponsePattern? _text;
    private int _literalMatched;
    private long _position;
    public int Length { get; private set; }
    public long? MatchOffset { get; private set; }

    private ResponsePattern(string pattern, Encoding encoding, int encodedLength)
    {
        _text = new(pattern, encoding);
        Length = encodedLength;
        _masks = []; _wildcards = []; _state = [];
    }

    private ResponsePattern(byte?[] bytes)
    {
        Length = bytes.Length;
        if (bytes.All(b => b.HasValue))
        {
            _literal = bytes.Select(b => b!.Value).ToArray();
            _prefix = new int[Length];
            for (int i = 1, previous = 0; i < Length; i++)
            {
                while (previous > 0 && _literal[i] != _literal[previous]) previous = _prefix[previous - 1];
                if (_literal[i] == _literal[previous]) previous++;
                _prefix[i] = previous;
            }
            _state = []; _wildcards = []; _masks = new();
            return;
        }
        _state = new ulong[(Length + 63) / 64];
        _wildcards = new ulong[_state.Length];
        _masks = new();
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] is not { } value) _wildcards[i / 64] |= 1UL << (i % 64);
            else
            {
                if (!_masks.TryGetValue(value, out var mask)) _masks[value] = mask = new ulong[_state.Length];
                mask[i / 64] |= 1UL << (i % 64);
            }
        }
    }

    public static ResponsePattern Compile(ResponseCheck check, Encoding encoding)
    {
        if (check.TimeoutMs is < 1 or > 60000)
            throw new ArgumentException("Response timeout must be 1..60000 ms.");
        if (check.Pattern is not { Length: > 0 and <= 4096 })
            throw new ArgumentException("Response pattern must contain 1..4096 characters.");
        byte?[] bytes;
        if (check.Kind == "text")
        {
            var exact = (Encoding)encoding.Clone();
            exact.EncoderFallback = EncoderFallback.ExceptionFallback;
            var encodedLength = exact.GetByteCount(check.Pattern);
            return new ResponsePattern(check.Pattern, exact, encodedLength);
        }
        else if (check.Kind == "hex")
        {
            var digits = new string(check.Pattern.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (digits.Length == 0 || digits.Length % 2 != 0)
                throw new ArgumentException("HEX pattern requires complete bytes or ?? (for example AA 55 ?? 00).");
            bytes = new byte?[digits.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                var pair = digits.Substring(i * 2, 2);
                if (pair == "??") continue;
                if (!byte.TryParse(pair, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                    throw new ArgumentException("HEX pattern supports full bytes and ?? only.");
                bytes[i] = value;
            }
            if (bytes.All(b => b is null)) throw new ArgumentException("HEX pattern must contain at least one fixed byte.");
        }
        else throw new ArgumentException("Response kind must be text or hex.");
        return new ResponsePattern(bytes);
    }

    // Decode an unfinished character from outside the response range without
    // allowing it to become match evidence. HEX patterns remain byte-oriented.
    internal void PrimeText(ReadOnlySpan<byte> decoderPrefix) => _text?.Prime(decoderPrefix);

    // Shift-And handles overlapping wildcard patterns without an invalid KMP prefix table.
    // Only pattern-sized state survives chunk boundaries; no RX buffer grows here.
    public bool Feed(ReadOnlySpan<byte> bytes)
    {
        if (MatchOffset.HasValue) return true;
        if (_text is not null)
        {
            if (!_text.Feed(bytes)) return false;
            MatchOffset = _text.MatchOffset;
            Length = _text.MatchLength;
            return true;
        }
        if (_literal is not null)
        {
            foreach (var value in bytes)
            {
                while (_literalMatched > 0 && value != _literal[_literalMatched]) _literalMatched = _prefix![_literalMatched - 1];
                if (value == _literal[_literalMatched]) _literalMatched++;
                _position++;
                if (_literalMatched == Length) { MatchOffset = _position - Length; return true; }
            }
            return false;
        }
        foreach (var value in bytes)
        {
            _masks.TryGetValue(value, out var mask);
            ulong carry = 1;
            for (var w = 0; w < _state.Length; w++)
            {
                var previous = _state[w];
                _state[w] = ((previous << 1) | carry) & (_wildcards[w] | (mask?[w] ?? 0));
                carry = previous >> 63;
            }
            _position++;
            if ((_state[(Length - 1) / 64] & (1UL << ((Length - 1) % 64))) != 0)
            {
                MatchOffset = _position - Length;
                return true;
            }
        }
        return false;
    }
}
