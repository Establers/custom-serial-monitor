using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

internal static class LogRuleValidation
{
    internal static bool TryValidatePattern(LogRule rule, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(rule.Keyword)) error = "Keyword is required.";
        else if (!Enum.IsDefined(rule.Mode) || !Enum.IsDefined(rule.MatchDirection)) error = "Invalid rule mode or direction.";
        else if (rule.UseRegex && rule.Mode != LogRuleMatchMode.Terminal) error = "Regex is available only in Terminal mode.";
        else if (rule.UseRegex && !RuleRegex.TryCreate(rule.Keyword, rule.CaseSensitive, out _, out var regexError)) error = regexError!;
        else if (rule.Mode == LogRuleMatchMode.Hex && !LogRuleMatcher.TryParseHexPattern(rule.Keyword, out _, out var hexError)) error = "Invalid HEX pattern: " + hexError;
        return error.Length == 0;
    }
}
