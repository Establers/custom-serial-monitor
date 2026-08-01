using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

internal static class AutoReconnectPolicy
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    ];

    public static bool ShouldStart(
        bool enabled,
        bool armedAfterSuccessfulConnection,
        bool isMockPort,
        bool isShuttingDown,
        SerialConnectionState connectionState)
    {
        return enabled &&
            armedAfterSuccessfulConnection &&
            !isMockPort &&
            !isShuttingDown &&
            connectionState == SerialConnectionState.Faulted;
    }

    public static TimeSpan GetRetryDelay(int attemptNumber)
    {
        var normalizedAttempt = Math.Max(1, attemptNumber);
        var index = Math.Min(normalizedAttempt - 1, RetryDelays.Length - 1);
        return RetryDelays[index];
    }

    public static bool CanSkipDisconnectCleanup(
        bool isConnected,
        bool hasConnectionLifetime,
        SerialConnectionState connectionState,
        bool hasPreservedSessionServices)
    {
        return !isConnected &&
            !hasConnectionLifetime &&
            connectionState == SerialConnectionState.Disconnected &&
            !hasPreservedSessionServices;
    }
}
