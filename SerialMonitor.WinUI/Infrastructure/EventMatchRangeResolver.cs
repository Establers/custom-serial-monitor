using System.Globalization;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

public static class EventMatchRangeResolver
{
    private const int MaxRanges = 64;

    public static IReadOnlyList<TextMatchRange> Resolve(LogLine line, EventRule rule)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(rule);

        return rule.Mode == LogRuleMatchMode.Hex
            ? ResolveHex(line, rule.Keyword)
            : ResolveTerminal(line.DisplayText, rule.Keyword, rule.CaseSensitive);
    }

    private static IReadOnlyList<TextMatchRange> ResolveTerminal(
        string displayText,
        string keyword,
        bool caseSensitive)
    {
        if (string.IsNullOrEmpty(displayText) || string.IsNullOrEmpty(keyword))
        {
            return Array.Empty<TextMatchRange>();
        }

        var comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var ranges = new List<TextMatchRange>();
        var searchStart = 0;
        while (searchStart <= displayText.Length - keyword.Length && ranges.Count < MaxRanges)
        {
            var matchStart = displayText.IndexOf(keyword, searchStart, comparison);
            if (matchStart < 0)
            {
                break;
            }

            ranges.Add(new TextMatchRange(matchStart, keyword.Length));
            searchStart = matchStart + keyword.Length;
        }

        return ranges;
    }

    private static IReadOnlyList<TextMatchRange> ResolveHex(LogLine line, string keyword)
    {
        if (line.RawBytes is not { Length: > 0 } bytes ||
            !LogRuleMatcher.TryParseHexPattern(keyword, out var pattern, out _) ||
            pattern.Length == 0)
        {
            return Array.Empty<TextMatchRange>();
        }

        var ranges = new List<TextMatchRange>();
        var expectedDisplay = string.Join(
            ' ',
            pattern.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
        for (var byteIndex = 0;
             byteIndex <= bytes.Length - pattern.Length && ranges.Count < MaxRanges;)
        {
            var matchIndex = FindBytes(bytes, pattern, byteIndex);
            if (matchIndex < 0)
            {
                break;
            }

            var textStart = matchIndex * 3;
            var textLength = (pattern.Length * 3) - 1;
            if (textStart >= 0 &&
                textStart + textLength <= line.DisplayText.Length &&
                string.Equals(
                    line.DisplayText.Substring(textStart, textLength),
                    expectedDisplay,
                    StringComparison.OrdinalIgnoreCase))
            {
                ranges.Add(new TextMatchRange(textStart, textLength));
            }

            byteIndex = matchIndex + pattern.Length;
        }

        return ranges;
    }


    private static int FindBytes(byte[] source, byte[] pattern, int startIndex)
    {
        for (var index = Math.Max(0, startIndex); index <= source.Length - pattern.Length; index++)
        {
            var matched = true;
            for (var patternIndex = 0; patternIndex < pattern.Length; patternIndex++)
            {
                if (source[index + patternIndex] == pattern[patternIndex])
                {
                    continue;
                }

                matched = false;
                break;
            }

            if (matched)
            {
                return index;
            }
        }

        return -1;
    }
}
