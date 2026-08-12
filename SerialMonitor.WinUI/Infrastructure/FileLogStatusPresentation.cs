using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Infrastructure;

internal static class FileLogStatusPresentation
{
    public static string CreateMainStatus(
        bool requested,
        FileLogWriterState state,
        bool isRetrying,
        bool ingressActive)
    {
        if (!requested)
        {
            return "Log Save: OFF";
        }

        if (state == FileLogWriterState.Faulted)
        {
            return isRetrying ? "Log Save: FAULTED / retrying" : "Log Save: FAULTED";
        }

        if (state == FileLogWriterState.Stopping)
        {
            return "Log Save: ON / stopping";
        }

        if (state == FileLogWriterState.Starting)
        {
            return "Log Save: ON / starting";
        }

        if (!ingressActive)
        {
            return "Log Save: ON / armed";
        }

        return state == FileLogWriterState.Stopped
            ? "Log Save: ON / stopped"
            : "Log Save: ON";
    }

    public static string CreateCompactStatus(
        bool requested,
        FileLogWriterState state,
        bool isRetrying,
        string? currentPath,
        bool ingressActive)
    {
        if (!requested)
        {
            return "File OFF";
        }

        if (state == FileLogWriterState.Faulted)
        {
            return isRetrying ? "File FAULTED / retrying" : "File FAULTED";
        }

        if (state == FileLogWriterState.Stopping)
        {
            return "File ON stopping";
        }

        if (state == FileLogWriterState.Starting)
        {
            return "File ON starting";
        }

        if (!ingressActive)
        {
            return "File ON armed";
        }

        if (state == FileLogWriterState.Stopped)
        {
            return "File ON stopped";
        }

        return !string.IsNullOrWhiteSpace(currentPath)
            ? $"File ON {Path.GetFileName(currentPath)}"
            : "File ON waiting";
    }
}
