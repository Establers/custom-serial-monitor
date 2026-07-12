namespace SerialMonitor.WinUI.Models;

public enum EventMatchDirection
{
    RxOnly,
    TxOnly,
    Both
}

public sealed class EventRule
{
    public string Name { get; set; } = string.Empty;

    public string Keyword { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public bool CaseSensitive { get; set; }

    public LogRuleMatchMode MatchMode { get; set; } = LogRuleMatchMode.Text;

    public EventMatchDirection MatchDirection { get; set; } = EventMatchDirection.RxOnly;

    public string? HighlightColor { get; set; }

    public bool TrayNotificationEnabled { get; set; }

    public bool SoundNotificationEnabled { get; set; }

    public bool PopupNotificationEnabled { get; set; }

    public int NotificationCooldownSeconds { get; set; } = 30;

    public bool IsEnabled
    {
        get => Enabled;
        set => Enabled = value;
    }

    public bool IsCaseSensitive
    {
        get => CaseSensitive;
        set => CaseSensitive = value;
    }
}
