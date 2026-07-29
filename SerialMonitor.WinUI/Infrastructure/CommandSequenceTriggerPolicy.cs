using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

public static class CommandSequenceTriggerPolicy
{
    public static bool CanUseAsTrigger(CommandSequence? sequence)
    {
        return sequence is { Steps.Count: > 0 };
    }

    public static bool SupportsRxTrigger(HighlightMatchDirection direction)
    {
        return direction != HighlightMatchDirection.TxOnly;
    }

    public static IReadOnlyList<string> FindReferencingRuleNames(
        string? sequenceName,
        IEnumerable<LogRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        if (string.IsNullOrWhiteSpace(sequenceName))
        {
            return Array.Empty<string>();
        }

        return rules
            .Where(rule => string.Equals(
                rule.TriggerSequenceName,
                sequenceName,
                StringComparison.OrdinalIgnoreCase))
            .Select(rule => string.IsNullOrWhiteSpace(rule.Name) ? "(unnamed)" : rule.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
