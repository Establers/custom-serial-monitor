using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public interface IFileLogWriter : IAsyncDisposable
{
    event EventHandler<string>? Error;

    event EventHandler? StatusChanged;

    bool IsRunning { get; }

    string LogDirectory { get; }

    string? CurrentLogFilePath { get; }

    string? LastFileError { get; }

    long WrittenLineCount { get; }

    long WrittenByteCount { get; }

    long FileErrorCount { get; }

    long DroppedLineCount { get; }

    int PendingRequestCount { get; }

    long StartCount { get; }

    long StopCount { get; }

    long LifecycleErrorCount { get; }

    string LastLifecycleAction { get; }

    long MaximumFileSizeBytes { get; set; }

    Task StartAsync(string directory, CancellationToken cancellationToken);

    void UpdateSessionFileNaming(
        string? sanitizedSessionName,
        bool useSessionNameInFileName,
        DateTimeOffset? sessionStartedAt,
        bool requestNewFile);

    bool TryEnqueue(LogLine line);

    Task StopAsync(CancellationToken cancellationToken);
}
