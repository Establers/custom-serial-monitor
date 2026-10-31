namespace SerialMonitor.WinUI.Models;

// Small shared handle. The archive tracks it weakly and can reclaim files once
// no visible event, notification, pending update or selection refers to them.
public sealed class EventEvidenceReference
{
    // Reserve the maximum 1000 before + matched + 1000 after references at
    // admission, so completing a context cannot grow a previously queued item.
    internal const int MaximumPins = 2001;
    internal const long ReservedMemoryBytes = 1024L + MaximumPins * 64L;
    internal IReadOnlyList<IArchivedLogLine> ContextPins { get; set; } = Array.Empty<IArchivedLogLine>();
    internal EventEvidenceReference(Guid id, string path) { EventId = id; Path = path; }
    public Guid EventId { get; }
    internal string Path { get; }
    public int BeforeCount { get; internal set; }
    public int AfterCount { get; internal set; }
    public bool IsComplete { get; internal set; }
    public int Version { get; internal set; }
    public string? Error { get; internal set; }
    public string? CaptureError { get; internal set; }
}
