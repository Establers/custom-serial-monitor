using System.Text;

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

    public static async Task<string> EvaluateAsync(string rawFile, long startOffset, long endOffset,
        AutomationResponseExpectation? expectation, Encoding encoding, bool inconclusive,
        CancellationToken token = default)
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
            file.Position = startOffset;
            var prefix = new int[pattern.Length];
            for (int i = 1, previous = 0; i < pattern.Length; i++)
            {
                while (previous > 0 && pattern[i] != pattern[previous]) previous = prefix[previous - 1];
                if (pattern[i] == pattern[previous]) previous++;
                prefix[i] = previous;
            }
            var buffer = new byte[16384];
            var remaining = endOffset - startOffset;
            var matchedLength = 0;
            var matched = false;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var count = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                if (count == 0) return "inconclusive";
                remaining -= count;
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
}
