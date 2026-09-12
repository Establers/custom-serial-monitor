namespace SerialMonitor.WinUI.Services;

public static class FileLogStatus
{
    public static string Describe(bool enabled, FileLogWriterState state, bool hasFile, bool hasError, bool paused = false) => state switch
    {
        FileLogWriterState.Faulted => "FAULT",
        FileLogWriterState.Stopping => "STOPPING",
        FileLogWriterState.Starting => "STARTING",
        FileLogWriterState.Running when enabled && hasFile => hasError ? "WARN" : paused ? "PAUSED" : "ON",
        _ => enabled ? "WAITING" : "OFF"
    };
}
