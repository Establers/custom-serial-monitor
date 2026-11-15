using System.Text.RegularExpressions;

namespace SerialMonitor.WinUI.Infrastructure;

// A bounded, linear-time regex shared by event, highlight and filter matching.
internal sealed class RuleRegex
{
    internal const int MaxPatternLength = 1024;
    internal const int MaxInputLength = 16_384;
    private readonly Regex _regex;
    private string? _runtimeError;

    private RuleRegex(Regex regex) => _regex = regex;

    internal static bool TryCreate(string pattern, bool caseSensitive, out RuleRegex? result, out string? error)
    {
        result = null;
        error = null;
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > MaxPatternLength)
        {
            error = $"Regex must contain 1–{MaxPatternLength} characters.";
            return false;
        }
        try
        {
            var options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
            if (!caseSensitive) options |= RegexOptions.IgnoreCase;
            result = new(new Regex(pattern, options, TimeSpan.FromMilliseconds(25)));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            error = "Invalid or unsupported regex: " + ex.Message;
            return false;
        }
    }

    internal bool IsMatch(string text, out string? error)
    {
        error = Volatile.Read(ref _runtimeError);
        if (error is not null || text.Length > MaxInputLength) return false;
        try
        {
            foreach (var match in _regex.EnumerateMatches(text))
                if (match.Length > 0) return true;
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            error = "Regex exceeded its time limit; edit or reapply the rule to retry.";
            Interlocked.CompareExchange(ref _runtimeError, error, null);
            return false;
        }
    }

    internal IReadOnlyList<Models.TextMatchRange> Ranges(string text)
    {
        var ranges = new List<Models.TextMatchRange>();
        if (text.Length > MaxInputLength || Volatile.Read(ref _runtimeError) is not null) return ranges;
        try
        {
            foreach (var match in _regex.EnumerateMatches(text))
            {
                if (match.Length > 0) ranges.Add(new(match.Index, match.Length));
                if (ranges.Count >= 64) break;
            }
        }
        catch (RegexMatchTimeoutException) { ranges.Clear(); }
        return ranges;
    }
}
