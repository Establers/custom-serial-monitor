using System.Text;
using SerialMonitor.Core.Sequences;

namespace SerialMonitor.Core.Automation;

// Literal evidence over one recorded raw-byte range. A missing literal does not
// establish product failure, especially when device printf output interleaves.
public static class AutomationSequenceMatcher
{
    public static byte[] PatternBytes(AutomationResponseExpectation expectation, Encoding encoding)
    {
        if (expectation.Value is not { Length: > 0 and <= 4096 })
            throw new AutomationException("invalid_argument", "Expect.Value must contain 1..4096 characters.");
        byte[] pattern;
        if (expectation.Kind == "text")
        {
            try
            {
                // Replacement characters could turn an unrepresentable expected
                // literal into a false positive (for example Unicode -> ASCII '?').
                var exactEncoding = (Encoding)encoding.Clone();
                exactEncoding.EncoderFallback = EncoderFallback.ExceptionFallback;
                pattern = exactEncoding.GetBytes(expectation.Value);
            }
            catch (EncoderFallbackException ex) { throw new AutomationException("invalid_argument", ex.Message); }
        }
        else if (expectation.Kind == "hex")
        {
            var digits = new string(expectation.Value.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (digits.Length == 0 || digits.Length % 2 != 0 || digits.Any(c => !Uri.IsHexDigit(c)))
                throw new AutomationException("invalid_argument", "Expect.Value must contain complete hexadecimal bytes, optionally separated by whitespace.");
            pattern = Convert.FromHexString(digits);
        }
        else throw new AutomationException("invalid_argument", "Expect.Kind must be text or hex.");
        if (pattern.Length is < 1 or > 16384)
            throw new AutomationException("invalid_argument", "The expected byte pattern must contain 1..16384 bytes.");
        return pattern;
    }

    public static Task<string> EvaluateAsync(string rawFile, long startOffset, long endOffset,
        AutomationResponseExpectation? expectation, Encoding encoding, bool inconclusive,
        CancellationToken token = default) =>
        EvaluateAsync(rawFile, startOffset, endOffset, expectation, encoding, inconclusive, token, null);

    internal static async Task<string> EvaluateAsync(string rawFile, long startOffset, long endOffset,
        AutomationResponseExpectation? expectation, Encoding encoding, bool inconclusive,
        CancellationToken token, TextRangeContext? context)
    {
        token.ThrowIfCancellationRequested();
        if (expectation is null) return "not_requested";
        var pattern = PatternBytes(expectation, encoding);
        if (inconclusive || startOffset < 0 || endOffset < startOffset) return "inconclusive";
        try
        {
            await using var file = new FileStream(rawFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (endOffset > file.Length) return "inconclusive";
            var buffer = new byte[16384];
            TextResponsePattern? text = null;
            if (expectation.Kind == "text")
            {
                context ??= new TextRangeContext(encoding);
                var decoderPrefix = await context.ReadPrefixAsync(file, rawFile, startOffset, buffer, token).ConfigureAwait(false);
                text = new(expectation.Value, encoding, decoderPrefix);
            }
            file.Position = startOffset;
            // Keep the legacy validation/HEX contract, while sharing the GUI's
            // incremental character matcher for text (including split CP949).
            var prefix = text is null ? new int[pattern.Length] : [];
            for (int i = 1, previous = 0; i < prefix.Length; i++)
            {
                while (previous > 0 && pattern[i] != pattern[previous]) previous = prefix[previous - 1];
                if (pattern[i] == pattern[previous]) previous++;
                prefix[i] = previous;
            }
            var remaining = endOffset - startOffset;
            var matchedLength = 0;
            var matched = false;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var count = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                if (count == 0) return "inconclusive";
                remaining -= count;
                if (text is not null)
                {
                    matched |= text.Feed(buffer.AsSpan(0, count));
                    continue;
                }
                for (var i = 0; i < count; i++)
                {
                    while (matchedLength > 0 && buffer[i] != pattern[matchedLength]) matchedLength = prefix[matchedLength - 1];
                    if (buffer[i] == pattern[matchedLength]) matchedLength++;
                    if (matchedLength != pattern.Length) continue;
                    matched = true;
                    matchedLength = prefix[matchedLength - 1];
                }
            }
            return matched ? "matched" : "not_matched";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return "inconclusive"; }
    }

    // One sequence owns one monotonic decoder checkpoint. This avoids decoding
    // an increasingly large file prefix again for each of its ordered steps.
    internal sealed class TextRangeContext(Encoding encoding)
    {
        private TextDecoderContext _decoder = new(encoding);
        private string? _rawFile;
        private long _offset;

        internal async Task<byte[]> ReadPrefixAsync(FileStream file, string rawFile, long start,
            byte[] buffer, CancellationToken token)
        {
            if (_rawFile != rawFile || _offset > start)
            { _decoder = new(encoding); _offset = 0; _rawFile = rawFile; }
            file.Position = _offset;
            while (_offset < start)
            {
                token.ThrowIfCancellationRequested();
                var count = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, start - _offset)), token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("Response decoder context is incomplete.");
                _decoder.Observe(buffer.AsSpan(0, count));
                _offset += count;
            }
            token.ThrowIfCancellationRequested();
            return _decoder.PendingBytes();
        }
    }
}
