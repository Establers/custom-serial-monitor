using System.Text.Json.Serialization;

namespace SerialMonitor.WinUI.Models;

public enum LogRuleMatchMode
{
    Terminal = 0,
    Hex = 1
}

public sealed class LogRule
{
    public string Name { get; set; } = string.Empty;

    public string Keyword { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public bool UseForEvent { get; set; }

    public bool UseForHighlight { get; set; } = true;

    public bool UseAsViewFilter { get; set; }

    public bool CaseSensitive { get; set; }

    public LogRuleMatchMode Mode { get; set; } = LogRuleMatchMode.Terminal;

    [JsonPropertyName("MatchMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyMatchMode
    {
        get => null;
        set => Mode = string.Equals(value, "Hex", StringComparison.OrdinalIgnoreCase)
            ? LogRuleMatchMode.Hex
            : LogRuleMatchMode.Terminal;
    }

    public HighlightMatchDirection MatchDirection { get; set; } = HighlightMatchDirection.Both;

    public string ForegroundColor { get; set; } = "Default";

    public string? BackgroundColor { get; set; }

    public int Priority { get; set; }

    public bool TrayNotificationEnabled { get; set; }

    public bool SoundNotificationEnabled { get; set; }

    public bool PopupNotificationEnabled { get; set; }

    public int NotificationCooldownSeconds { get; set; } = 30;
}
