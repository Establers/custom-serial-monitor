namespace SerialMonitor.WinUI.Models;

public enum LogRuleMatchMode
{
    Text,
    Hex
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

    public LogRuleMatchMode MatchMode { get; set; } = LogRuleMatchMode.Text;

    public HighlightMatchDirection MatchDirection { get; set; } = HighlightMatchDirection.Both;

    public string ForegroundColor { get; set; } = "Default";

    public string? BackgroundColor { get; set; }

    public int Priority { get; set; }

    public bool TrayNotificationEnabled { get; set; }

    public bool SoundNotificationEnabled { get; set; }

    public bool PopupNotificationEnabled { get; set; }

    public int NotificationCooldownSeconds { get; set; } = 30;
}
