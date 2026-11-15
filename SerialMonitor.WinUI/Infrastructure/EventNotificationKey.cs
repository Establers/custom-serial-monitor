using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

// Rules may share a name and pattern while requesting different notifications.
// Only identical notification policies can safely share a batch and cooldown.
internal readonly record struct EventNotificationKey(
    string RuleName,
    string Keyword,
    bool ShowTray,
    bool PlaySound,
    bool ShowPopup,
    int CooldownSeconds)
{
    internal static EventNotificationKey From(DetectedEvent item) => new(
        item.RuleName, item.Keyword, item.TrayNotificationEnabled,
        item.SoundNotificationEnabled, item.PopupNotificationEnabled,
        RuleNotificationLimits.ClampCooldown(item.NotificationCooldownSeconds));
}
