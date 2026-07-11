using System.Globalization;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

public static class LogRuleMatcher
{
    public sealed class CompiledHighlightRule
    {
        internal CompiledHighlightRule(
            HighlightRule rule,
            byte[]? hexPattern,
            string? compileError,
            StringComparison textComparison)
        {
            Rule = rule;
            HexPattern = hexPattern;
            CompileError = compileError;
            TextComparison = textComparison;
        }

        public HighlightRule Rule { get; }

        public bool IsTextRule => Rule.MatchMode == LogRuleMatchMode.Text;

        public bool IsHexRule => Rule.MatchMode == LogRuleMatchMode.Hex && CompileError is null;

        public bool IsInvalid => CompileError is not null;

        internal byte[]? HexPattern { get; }

        internal string? CompileError { get; }

        internal StringComparison TextComparison { get; }
    }

    public sealed class CompiledEventRule
    {
        internal CompiledEventRule(
            EventRule rule,
            byte[]? hexPattern,
            string? compileError,
            StringComparison textComparison)
        {
            Rule = rule;
            HexPattern = hexPattern;
            CompileError = compileError;
            TextComparison = textComparison;
        }

        public EventRule Rule { get; }

        public bool IsTextRule => Rule.MatchMode == LogRuleMatchMode.Text;

        public bool IsHexRule => Rule.MatchMode == LogRuleMatchMode.Hex && CompileError is null;

        public bool IsInvalid => CompileError is not null;

        internal byte[]? HexPattern { get; }

        internal string? CompileError { get; }

        internal StringComparison TextComparison { get; }
    }

    public static CompiledHighlightRule Compile(HighlightRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var snapshot = CloneHighlightRule(rule);
        var comparison = snapshot.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        if (snapshot.MatchMode != LogRuleMatchMode.Hex)
        {
            return new CompiledHighlightRule(snapshot, null, null, comparison);
        }

        if (!TryParseHexPattern(snapshot.Keyword, out var pattern, out var parseError))
        {
            return new CompiledHighlightRule(
                snapshot,
                null,
                $"Invalid HEX rule pattern: {FormatRuleName(snapshot.Name, snapshot.Keyword)} ({parseError})",
                comparison);
        }

        return new CompiledHighlightRule(snapshot, pattern, null, comparison);
    }

    public static CompiledEventRule Compile(EventRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var snapshot = CloneEventRule(rule);
        var comparison = snapshot.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        if (snapshot.MatchMode != LogRuleMatchMode.Hex)
        {
            return new CompiledEventRule(snapshot, null, null, comparison);
        }

        if (!TryParseHexPattern(snapshot.Keyword, out var pattern, out var parseError))
        {
            return new CompiledEventRule(
                snapshot,
                null,
                $"Invalid HEX rule pattern: {FormatRuleName(snapshot.Name, snapshot.Keyword)} ({parseError})",
                comparison);
        }

        return new CompiledEventRule(snapshot, pattern, null, comparison);
    }

    public static bool IsMatch(LogLine line, HighlightRule rule, out string? error)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(rule);

        error = null;
        if (!rule.Enabled ||
            string.IsNullOrWhiteSpace(rule.Keyword) ||
            !IsDirectionMatch(line.Direction, rule.MatchDirection))
        {
            return false;
        }

        return IsMatch(line, rule.Keyword, rule.MatchMode, rule.CaseSensitive, rule.Name, out error);
    }

    public static bool IsMatch(LogLine line, CompiledHighlightRule rule, out string? error)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(rule);

        error = null;
        var source = rule.Rule;
        if (!source.Enabled ||
            string.IsNullOrWhiteSpace(source.Keyword) ||
            !IsDirectionMatch(line.Direction, source.MatchDirection))
        {
            return false;
        }

        if (rule.CompileError is not null)
        {
            return false;
        }

        if (source.MatchMode == LogRuleMatchMode.Hex)
        {
            return rule.HexPattern is not null && ContainsBytes(line.RawBytes, rule.HexPattern);
        }

        return line.Text.Contains(source.Keyword, rule.TextComparison);
    }

    public static bool IsMatch(LogLine line, EventRule rule, out string? error)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(rule);

        error = null;
        if (!rule.Enabled ||
            string.IsNullOrWhiteSpace(rule.Keyword) ||
            !IsDirectionMatch(line.Direction, rule.MatchDirection))
        {
            return false;
        }

        return IsMatch(line, rule.Keyword, rule.MatchMode, rule.CaseSensitive, rule.Name, out error);
    }

    public static bool IsMatch(LogLine line, CompiledEventRule rule, out string? error)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(rule);

        error = null;
        var source = rule.Rule;
        if (!source.Enabled ||
            string.IsNullOrWhiteSpace(source.Keyword) ||
            !IsDirectionMatch(line.Direction, source.MatchDirection))
        {
            return false;
        }

        if (rule.CompileError is not null)
        {
            return false;
        }

        if (source.MatchMode == LogRuleMatchMode.Hex)
        {
            return rule.HexPattern is not null && ContainsBytes(line.RawBytes, rule.HexPattern);
        }

        return line.Text.Contains(source.Keyword, rule.TextComparison);
    }

    public static bool TryParseHexPattern(string? keyword, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(keyword))
        {
            error = "HEX keyword is empty.";
            return false;
        }

        var tokens = keyword
            .Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var result = new List<byte>();
        if (tokens.Length > 1)
        {
            foreach (var rawToken in tokens)
            {
                var token = StripHexPrefix(rawToken);
                if (!TryAppendHexToken(token, result, out error))
                {
                    return false;
                }
            }
        }
        else
        {
            var compact = StripHexPrefix(tokens.Length == 0 ? keyword.Trim() : tokens[0]);
            if (!TryAppendHexToken(compact, result, out error))
            {
                return false;
            }
        }

        bytes = result.ToArray();
        return bytes.Length > 0;
    }

    public static string FormatBytesPreview(byte[]? bytes, int maxBytes = 16)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return "(none)";
        }

        var count = Math.Min(bytes.Length, Math.Max(1, maxBytes));
        var parts = new string[count];
        for (var index = 0; index < count; index++)
        {
            parts[index] = bytes[index].ToString("X2", CultureInfo.InvariantCulture);
        }

        return bytes.Length > count
            ? string.Join(' ', parts) + " ..."
            : string.Join(' ', parts);
    }

    private static bool IsMatch(
        LogLine line,
        string keyword,
        LogRuleMatchMode matchMode,
        bool caseSensitive,
        string? ruleName,
        out string? error)
    {
        error = null;
        if (matchMode == LogRuleMatchMode.Hex)
        {
            if (!TryParseHexPattern(keyword, out var pattern, out var parseError))
            {
                error = $"Invalid HEX rule pattern: {FormatRuleName(ruleName, keyword)} ({parseError})";
                return false;
            }

            return ContainsBytes(line.RawBytes, pattern);
        }

        var comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        return line.Text.Contains(keyword, comparison);
    }

    private static bool IsDirectionMatch(LogDirection direction, HighlightMatchDirection matchDirection)
    {
        return matchDirection switch
        {
            HighlightMatchDirection.RxOnly => direction == LogDirection.Rx,
            HighlightMatchDirection.TxOnly => direction == LogDirection.Tx,
            _ => true
        };
    }

    private static bool IsDirectionMatch(LogDirection direction, EventMatchDirection matchDirection)
    {
        return matchDirection switch
        {
            EventMatchDirection.RxOnly => direction == LogDirection.Rx,
            EventMatchDirection.TxOnly => direction == LogDirection.Tx,
            _ => direction is LogDirection.Rx or LogDirection.Tx
        };
    }

    private static bool ContainsBytes(byte[]? source, byte[] pattern)
    {
        if (source is null ||
            pattern.Length == 0 ||
            source.Length < pattern.Length)
        {
            return false;
        }

        for (var start = 0; start <= source.Length - pattern.Length; start++)
        {
            var matched = true;
            for (var offset = 0; offset < pattern.Length; offset++)
            {
                if (source[start + offset] != pattern[offset])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryAppendHexToken(string token, ICollection<byte> bytes, out string error)
    {
        error = string.Empty;
        if (token.Length == 0)
        {
            error = "empty token";
            return false;
        }

        if (token.Length % 2 != 0)
        {
            error = "odd number of hex digits";
            return false;
        }

        for (var index = 0; index < token.Length; index += 2)
        {
            if (!byte.TryParse(token.AsSpan(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                error = "non-hex digit found";
                return false;
            }

            bytes.Add(value);
        }

        return true;
    }

    private static string StripHexPrefix(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? trimmed[2..]
            : trimmed;
    }

    private static string FormatRuleName(string? ruleName, string keyword)
    {
        if (!string.IsNullOrWhiteSpace(ruleName))
        {
            return ruleName.Trim();
        }

        return string.IsNullOrWhiteSpace(keyword)
            ? "(unnamed)"
            : keyword.Trim();
    }

    private static HighlightRule CloneHighlightRule(HighlightRule rule)
    {
        return new HighlightRule
        {
            Name = rule.Name,
            Keyword = rule.Keyword,
            Enabled = rule.Enabled,
            CaseSensitive = rule.CaseSensitive,
            MatchMode = rule.MatchMode,
            UseAsViewFilter = rule.UseAsViewFilter,
            ForegroundColor = rule.ForegroundColor,
            BackgroundColor = rule.BackgroundColor,
            Priority = rule.Priority,
            MatchDirection = rule.MatchDirection
        };
    }

    private static EventRule CloneEventRule(EventRule rule)
    {
        return new EventRule
        {
            Name = rule.Name,
            Keyword = rule.Keyword,
            Enabled = rule.Enabled,
            CaseSensitive = rule.CaseSensitive,
            MatchMode = rule.MatchMode,
            MatchDirection = rule.MatchDirection,
            HighlightColor = rule.HighlightColor
        };
    }
}
