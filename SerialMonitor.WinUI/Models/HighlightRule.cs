using System.Text.Json.Serialization;

namespace SerialMonitor.WinUI.Models;

public enum HighlightMatchDirection
{
    RxOnly,
    TxOnly,
    Both
}

public sealed class HighlightRule
{
    public string Name { get; set; } = string.Empty;

    public string Keyword { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

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

    public bool UseAsViewFilter { get; set; }

    public string ForegroundColor { get; set; } = "Default";

    public string? BackgroundColor { get; set; }

    public int Priority { get; set; }

    public HighlightMatchDirection MatchDirection { get; set; } = HighlightMatchDirection.Both;
}
