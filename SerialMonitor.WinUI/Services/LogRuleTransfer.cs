using System.Text.Json;
using System.Text.Json.Serialization;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

internal static class LogRuleTransfer
{
    internal const string Prefix = "SMRULES:1:";
    internal const int MaxTextLength = 1_000_000;
    internal const int MaxRules = 200;
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    internal static string Encode(IEnumerable<LogRule> rules, CancellationToken cancellationToken = default)
    {
        var snapshot = rules.Take(MaxRules + 1).Select(r => r.Clone()).ToArray();
        Validate(snapshot, cancellationToken);
        var text = Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(snapshot, Options));
        if (text.Length > MaxTextLength) throw new FormatException("Rule text is too large. Export fewer rules.");
        return text;
    }

    internal static IReadOnlyList<LogRule> Decode(string text, CancellationToken cancellationToken = default)
    {
        if (text.Length > MaxTextLength) throw new FormatException("Rule text is too large.");
        text = text.Trim();
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
            throw new FormatException("Paste the complete SMRULES:1: text from Export rules.");
        try
        {
            var bytes = Convert.FromBase64String(text[Prefix.Length..]);
            var rules = JsonSerializer.Deserialize<LogRule[]>(bytes, Options)
                ?? throw new FormatException("Rule text contains no rules.");
            Validate(rules, cancellationToken);
            return rules;
        }
        catch (JsonException ex) { throw new FormatException("Rule text is damaged or uses unsupported settings.", ex); }
    }

    private static void Validate(IReadOnlyList<LogRule> rules, CancellationToken cancellationToken = default)
    {
        if (rules.Count is < 1 or > MaxRules) throw new FormatException($"Share 1–{MaxRules} rules at a time.");
        for (var i = 0; i < rules.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rule = rules[i];
            if (rule is null || rule.Name is null || rule.Name.Length > 512 ||
                rule.Keyword is null || rule.Keyword.Length > RuleRegex.MaxInputLength ||
                (rule.TriggerSequenceName?.Length ?? 0) > 512)
                throw new FormatException($"Rule {i + 1}: invalid or oversized name/pattern.");
            if (!LogRuleValidation.TryValidatePattern(rule, out var error)) throw new FormatException($"Rule {i + 1}: {error}");
            if (rule.NotificationCooldownSeconds is < RuleNotificationLimits.MinCooldownSeconds or > RuleNotificationLimits.MaxCooldownSeconds)
                throw new FormatException($"Rule {i + 1}: notification cooldown must be 5–86400 seconds.");
            if (!ValidColor(rule.ForegroundColor, false) || !ValidColor(rule.BackgroundColor, true))
                throw new FormatException($"Rule {i + 1}: unsupported color.");
        }
    }

    private static bool ValidColor(string? color, bool optional) =>
        optional && color is null || color is not null &&
        new[] { "Default", "Red", "Orange", "Yellow", "Green", "Cyan", "Blue", "Magenta", "White", "Gray", "Grey" }
            .Contains(color, StringComparer.OrdinalIgnoreCase);

    internal static (IReadOnlyList<LogRule> Added, int Duplicates, int DisabledTriggers) PrepareImport(
        IEnumerable<LogRule> existing, IReadOnlyList<LogRule> incoming)
    {
        Validate(incoming);
        var keys = existing.Select(Key).ToHashSet(StringComparer.Ordinal);
        var added = new List<LogRule>();
        var duplicates = 0;
        var disabledTriggers = 0;
        foreach (var source in incoming)
        {
            var rule = source.Clone();
            if (keys.Contains(Key(rule))) { duplicates++; continue; }
            if (!string.IsNullOrWhiteSpace(rule.TriggerSequenceName))
            {
                rule.Enabled = false;
            }
            if (!keys.Add(Key(rule))) { duplicates++; continue; }
            if (!string.IsNullOrWhiteSpace(rule.TriggerSequenceName)) disabledTriggers++;
            added.Add(rule);
        }
        return (added, duplicates, disabledTriggers);
    }

    private static string Key(LogRule rule) => JsonSerializer.Serialize(rule, Options);
}
