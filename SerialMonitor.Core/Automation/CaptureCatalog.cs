using System.Text.Json;

namespace SerialMonitor.Core.Automation;

public sealed record CaptureSummary(string CaptureId, string? RequestId, string CaptureStatus, string SendStatus,
    string Port, long SessionGeneration, DateTimeOffset StartedAt, DateTimeOffset EndedAt,
    long ReceivedBytes, long StoredBytes, long DroppedBytes, bool RxGapDetected,
    string TxMode, string LineEnding, string? CommandPreview, bool CommandTruncated);

public sealed record CaptureListPage(IReadOnlyList<CaptureSummary> Captures, int Offset, int NextOffset,
    bool HasMore, int TotalCount, bool CaptureInProgress, int SkippedCount);

// Only persisted metadata is enumerated. RX files are never loaded by a catalog
// query, and malformed/oversized records cannot grow a reply or hide good records.
internal static class CaptureCatalog
{
    private const int MaximumMetadataBytes = 256 * 1024;
    private const int CommandPreviewCharacters = 160;

    public static CaptureListPage Read(string directory, int offset, int limit, string? requestId,
        bool inProgress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var entries = new List<CaptureSummary>();
        var skipped = 0;
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                token.ThrowIfCancellationRequested();
                var id = Path.GetFileNameWithoutExtension(path);
                if (!Guid.TryParseExact(id, "N", out _)) continue;
                try
                {
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (file.Length > MaximumMetadataBytes) { skipped++; continue; }
                    // Read exactly the observed bounded length, even if another
                    // process changes the file while this query runs.
                    var bytes = new byte[(int)file.Length];
                    file.ReadExactly(bytes);
                    using var json = JsonDocument.Parse(bytes);
                    AutomationJsonValidation.ValidateStrings(json.RootElement);
                    var result = CaptureResultStorage.Deserialize(json.RootElement, directory, id);
                    if (result.CaptureId != id || !Bounded(result.CaptureStatus, 128) ||
                        !Bounded(result.SendStatus, 128) || !Bounded(result.Port, 128) ||
                        !Bounded(result.TxMode, 16) || !Bounded(result.LineEnding, 16) ||
                        result.RequestId is not null && (!Bounded(result.RequestId, 128) || string.IsNullOrWhiteSpace(result.RequestId)))
                        throw new JsonException("Invalid capture metadata.");
                    if (requestId is not null && result.RequestId != requestId) continue;
                    var length = Math.Min(result.Command?.Length ?? 0, CommandPreviewCharacters);
                    if (length > 0 && char.IsHighSurrogate(result.Command![length - 1])) length--;
                    entries.Add(new(id, result.RequestId, result.CaptureStatus, result.SendStatus, result.Port,
                        result.SessionGeneration, result.StartedAt, result.EndedAt, result.ReceivedBytes,
                        result.StoredBytes, result.DroppedBytes, result.RxGapDetected, result.TxMode, result.LineEnding,
                        result.Command is null ? null : result.Command[..length], length < (result.Command?.Length ?? 0)));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
                    InvalidOperationException or ArgumentException or FormatException or OverflowException or KeyNotFoundException)
                { skipped++; }
            }
        }
        token.ThrowIfCancellationRequested();
        var ordered = entries.OrderByDescending(entry => entry.StartedAt)
            .ThenBy(entry => entry.CaptureId, StringComparer.Ordinal).ToArray();
        // Retention can remove earlier pages between queries. Returning an empty
        // last page remains useful; callers need not invent a retry of device TX.
        var page = ordered.Skip(offset).Take(limit).ToArray();
        var next = offset + page.Length;
        return new(page, offset, next, next < ordered.Length, ordered.Length, inProgress, skipped);
    }

    private static bool Bounded(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum;
}
