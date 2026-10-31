namespace SerialMonitor.Core.Sequences;

// The results UI reads bounded pages without owning recording or retention.
public interface ISequenceRunReader
{
    string RunPath(string id);
    IDisposable Pin(string id);
    Task<IReadOnlyList<SequenceRunSummary>> ListAsync(CancellationToken token = default);
    Task<SequenceRunSummary?> ReadSummaryAsync(string id, CancellationToken token = default);
    Task<SequenceResultPage> ReadPageAsync(string id, long cursor = 0, string? filter = null,
        int? repeat = null, CancellationToken token = default);
    Task<IReadOnlyList<SequenceRxSlice>> ReadRxTimelineAsync(string id, long start, long end,
        CancellationToken token = default);
}
