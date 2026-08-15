namespace SerialMonitor.WinUI.Services;

public enum FileLogWriterState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted
}

public enum FileLogWriterFaultCategory
{
    RetryableIo,
    DeterministicConfiguration,
    Unexpected
}

public sealed record FileLogWriterFaultInfo(
    FileLogWriterFaultCategory Category,
    string Message,
    string ExceptionType,
    DateTimeOffset OccurredAt,
    bool CanAutoRecover);
