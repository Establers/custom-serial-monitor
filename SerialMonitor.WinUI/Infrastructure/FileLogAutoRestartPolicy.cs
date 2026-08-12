using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Infrastructure;

internal static class FileLogAutoRestartPolicy
{
    public static bool ShouldRetry(
        bool loggingRequested,
        bool hasValidConnectionOrReconnectSession,
        bool shutdownStarted,
        FileLogWriterState writerState,
        bool canAutoRecover) =>
        loggingRequested &&
        hasValidConnectionOrReconnectSession &&
        !shutdownStarted &&
        writerState == FileLogWriterState.Faulted &&
        canAutoRecover;
}
