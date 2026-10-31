using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public interface IEventEvidenceStore : IAsyncDisposable
{
    event EventHandler<string>? Error;
    Task<DetectedEvent> StoreEventAsync(DetectedEvent item, CancellationToken cancellationToken);
    Task<EventEvidenceReference> StoreContextAsync(DetectedEventContext context, CancellationToken cancellationToken);
    Task<EventEvidencePage> ReadPageAsync(EventEvidenceReference reference, long offset,
        Func<LogLine, string> format, CancellationToken cancellationToken, int? expectedVersion = null);
    Task ExportAsync(EventEvidenceReference reference, string destination,
        Func<LogLine, string> format, CancellationToken cancellationToken);
}

public sealed record EventEvidencePage(string Text, long StartOffset, long NextOffset, bool HasMore, bool IsComplete, int Version);
