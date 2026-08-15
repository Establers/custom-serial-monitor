using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public interface IFileLogWriter : IAsyncDisposable
{
    event EventHandler<string>? Error;

    event EventHandler? StatusChanged;

    bool IsRunning { get; }

    FileLogWriterState State { get; }

    FileLogWriterFaultInfo? LastFault { get; }

    string LogDirectory { get; }

    string? CurrentLogFilePath { get; }

    string? LastLogFilePath { get; }

    string? LastFileError { get; }

    long WrittenLineCount { get; }

    long AcceptedLineCount { get; }

    long DurableLineCount { get; }

    long UncertainLineCount { get; }

    long AbandonedLineCount { get; }

    long WrittenByteCount { get; }

    long FileErrorCount { get; }

    long DroppedLineCount { get; }

    long RecoveryCount { get; }

    int PendingRequestCount { get; }

    long StartCount { get; }

    long StopCount { get; }

    long LifecycleErrorCount { get; }

    string LastLifecycleAction { get; }

    long MaximumFileSizeBytes { get; set; }

    Task StartAsync(string directory, CancellationToken cancellationToken);

    void UpdateLogFileName(string? exactLogFileName, bool requestNewFile);

    bool TryEnqueue(LogLine line);

    Task StopAsync(CancellationToken cancellationToken);
}
