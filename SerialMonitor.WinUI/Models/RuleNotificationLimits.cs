namespace SerialMonitor.WinUI.Models;

internal static class RuleNotificationLimits
{
    public const int MinCooldownSeconds = 5;
    public const int MaxCooldownSeconds = 86_400;

    public static int ClampCooldown(int seconds) =>
        Math.Clamp(seconds, MinCooldownSeconds, MaxCooldownSeconds);
}
