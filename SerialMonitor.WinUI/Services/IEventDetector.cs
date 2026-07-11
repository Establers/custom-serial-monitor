using System.Threading.Channels;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public interface IEventDetector : IAsyncDisposable
{
    event EventHandler<string>? Error;

    event EventHandler? StatusChanged;

    ChannelReader<DetectedEvent> DetectedEvents { get; }

    ChannelReader<DetectedEventContext> CompletedEventContexts { get; }

    bool IsRunning { get; }

    bool EventLogWritingEnabled { get; }

    int EventRuleCount { get; }

    int CompiledTextRuleCount { get; }

    int CompiledHexRuleCount { get; }

    int InvalidCompiledRuleCount { get; }

    long DetectedEventCount { get; }

    long EventLogWrittenCount { get; }

    long ErrorCount { get; }

    long DroppedInputLineCount { get; }

    long DroppedOutputEventCount { get; }

    long ContextCapturesStartedCount { get; }

    long ContextCapturesCompletedCount { get; }

    int ActivePendingContextCount { get; }

    long ContextCaptureDroppedCount { get; }

    long ContextCaptureFailedCount { get; }

    long ContextCaptureScanLineCount { get; }

    long ContextCaptureEntriesVisited { get; }

    int MaxContextCaptureScanCount { get; }

    bool IsContextCaptureOverloadActive { get; }

    int ContextCaptureOverloadHighWatermark { get; }

    int ContextCaptureOverloadLowWatermark { get; }

    long ContextCaptureOverloadSkippedCount { get; }

    long ContextCaptureOverloadTransitionCount { get; }

    DateTimeOffset? LastContextCaptureOverloadTransitionTime { get; }

    long RuleEvaluationErrorCount { get; }

    string? LastRuleEvaluationError { get; }

    string? LastHexRuleMatchName { get; }

    string? LastHexRuleMatchBytesPreview { get; }

    string? LastDetectedEventText { get; }

    string? LastError { get; }

    string? CurrentEventLogFilePath { get; }

    Task StartAsync(
        IReadOnlyList<EventRule> rules,
        EventContextSettings contextSettings,
        string logDirectory,
        bool eventLogWritingEnabled,
        CancellationToken cancellationToken);

    void UpdateRules(IReadOnlyList<EventRule> rules);

    void UpdateContextSettings(EventContextSettings contextSettings);

    void UpdateSessionFileNaming(
        string? sanitizedSessionName,
        bool useSessionNameInFileName,
        DateTimeOffset? sessionStartedAt,
        bool requestNewFile);

    void SetEventLogWritingEnabled(bool enabled, string? logDirectory = null);

    bool TryEnqueue(LogLine line);

    Task StopAsync(CancellationToken cancellationToken);
}
