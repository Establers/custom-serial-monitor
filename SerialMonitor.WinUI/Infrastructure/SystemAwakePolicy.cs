namespace SerialMonitor.WinUI.Infrastructure;

internal static class SystemAwakePolicy
{
    public static bool ShouldKeepSystemAwake(
        bool isSerialConnected,
        bool autoReconnectEnabled,
        bool autoReconnectArmed,
        bool isAutoReconnectRunning,
        bool shutdownStarted) =>
        !shutdownStarted &&
        (isSerialConnected ||
            isAutoReconnectRunning ||
            (autoReconnectEnabled && autoReconnectArmed));
}
