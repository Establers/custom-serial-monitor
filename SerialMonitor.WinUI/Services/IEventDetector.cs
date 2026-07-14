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

    int EventRuleCount { get; }

    int CompiledTerminalRuleCount { get; }

    int CompiledHexRuleCount { get; }

    LogRuleMatchMode ActiveRuleMode { get; }

    int InvalidCompiledRuleCount { get; }

    long DetectedEventCount { get; }

    long ErrorCount { get; }

    long DroppedInputLineCount { get; }

    int PendingInputLineCount { get; }

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

    Task StartAsync(
        IReadOnlyList<EventRule> rules,
        EventContextSettings contextSettings,
        CancellationToken cancellationToken);

    void UpdateRules(IReadOnlyList<EventRule> rules);

    void UpdateRuleMode(LogRuleMatchMode mode);

    void UpdateContextSettings(EventContextSettings contextSettings);

    bool TryEnqueue(LogLine line);

    Task StopAsync(CancellationToken cancellationToken);
}
