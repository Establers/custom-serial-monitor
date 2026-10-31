using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace SerialMonitor.Core.Sequences;

public sealed class SequenceRunStore(string root) : ISequenceRunReader
{
    public const int PageSize = 200;
    public const int RxPageSize = 16 * 1024;
    internal const int RxTimeEntryBytes = 40;
    public const long SegmentBytes = 64L * 1024 * 1024;
    public string Root { get; } = Path.GetFullPath(root);

    public string RunPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid sequence run ID.");
        return Path.Combine(Root, id);
    }

    internal static FileStream Lease(string directory) => new(Path.Combine(directory, "run.lock"),
        FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    public IDisposable Pin(string id) => Lease(RunPath(id));

    public Task<IReadOnlyList<SequenceRunSummary>> ListAsync(CancellationToken token = default) => Task.Run<IReadOnlyList<SequenceRunSummary>>(() =>
    {
        if (!Directory.Exists(Root)) return Array.Empty<SequenceRunSummary>();
        var found = new List<SequenceRunSummary>();
        foreach (var path in Directory.EnumerateDirectories(Root))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var lease = Lease(path);
                var item = ReadSummaryFile(path);
                if (item is not null) found.Add(item);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            if (found.Count > 200) found = found.OrderByDescending(r => r.StartedAt).Take(100).ToList();
        }
        return found.OrderByDescending(r => r.StartedAt).Take(100).ToArray();
    }, token);

    private static SequenceRunSummary? ReadSummaryFile(string path)
    {
        using var file = new FileStream(Path.Combine(path, "summary.json"), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var summary = JsonSerializer.Deserialize<SequenceRunSummary>(file);
        if (summary?.State == "running")
        {
            try
            {
                using var probe = new FileStream(Path.Combine(path, "run.lock"), FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                return summary with { State = "interrupted", Error = "The recorder exited before finalizing this run. Results may be incomplete." };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return summary;
    }

    public Task<SequenceRunSummary?> ReadSummaryAsync(string id, CancellationToken token = default) =>
        Task.Run(() => { token.ThrowIfCancellationRequested(); using var lease = Lease(RunPath(id)); return ReadSummaryFile(RunPath(id)); }, token);

    public async Task<SequenceResultPage> ReadPageAsync(string id, long cursor = 0, string? filter = null,
        int? repeat = null, CancellationToken token = default)
    {
        var path = RunPath(id);
        using var lease = Lease(path);
        await using var index = new FileStream(Path.Combine(path, "steps.idx"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var data = new FileStream(Path.Combine(path, "steps.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
        var rows = new List<SequenceStepResult>(PageSize);
        SequenceStepStorage? storage = null;
        var count = index.Length / 24;
        cursor = Math.Clamp(cursor, 0, count);
        index.Position = cursor * 24;
        var entry = new byte[24];
        while (cursor < count && rows.Count < PageSize)
        {
            token.ThrowIfCancellationRequested();
            await index.ReadExactlyAsync(entry, token).ConfigureAwait(false);
            cursor++;
            var number = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(12));
            var status = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(16));
            if (repeat.HasValue && number != repeat.Value) continue;
            if (filter is not null && StatusCode(filter) != status) continue;
            var position = BinaryPrimitives.ReadInt64LittleEndian(entry);
            var length = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(8));
            if (length is < 1 or > 256 * 1024 || position < 0 || position + length > data.Length)
                throw new InvalidDataException("Sequence result index is incomplete or corrupt.");
            var json = new byte[length];
            data.Position = position;
            await data.ReadExactlyAsync(json, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                if (storage is null)
                {
                    await using var definitionFile = new FileStream(Path.Combine(path, "definition.json"), FileMode.Open,
                        FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                    var definition = await JsonSerializer.DeserializeAsync<SequenceRunDefinition>(definitionFile, cancellationToken: token).ConfigureAwait(false)
                        ?? throw new InvalidDataException("Sequence definition is missing.");
                    var summary = ReadSummaryFile(path) ?? throw new InvalidDataException("Sequence summary is missing.");
                    storage = new(definition, summary.StartedAt);
                }
                rows.Add(storage.Deserialize(document.RootElement, cursor, number, BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(20))));
            }
            else rows.Add(document.RootElement.Deserialize<SequenceStepResult>() ?? throw new InvalidDataException("Invalid sequence result."));
        }
        return new(rows, cursor, cursor < count);
    }

    public async Task<byte[]> ReadRxAsync(string id, long start, long end, CancellationToken token = default)
    {
        if (start < 0 || end < start) throw new ArgumentOutOfRangeException(nameof(start));
        using var lease = Lease(RunPath(id));
        var result = new byte[(int)Math.Min(RxPageSize, end - start)];
        var read = 0;
        while (read < result.Length)
        {
            var position = start + read;
            var segment = position / SegmentBytes;
            await using var file = new FileStream(Path.Combine(RunPath(id), $"rx-{segment:D6}.bin"), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            file.Position = position % SegmentBytes;
            var count = await file.ReadAsync(result.AsMemory(read, (int)Math.Min(result.Length - read, SegmentBytes - file.Position)), token).ConfigureAwait(false);
            if (count == 0) break;
            read += count;
        }
        return result.AsSpan(0, read).ToArray();
    }

    // Read a bounded byte page and its original receive timestamps. The index
    // refers to the binary RX log; payload bytes are never duplicated in JSON.
    public async Task<IReadOnlyList<SequenceRxSlice>> ReadRxTimelineAsync(string id, long start, long end,
        CancellationToken token = default)
    {
        var bytes = await ReadRxAsync(id, start, end, token).ConfigureAwait(false);
        if (bytes.Length == 0) return [];
        var compactPath = Path.Combine(RunPath(id), SequenceRxTimeIndex.FileName);
        if (File.Exists(compactPath))
        {
            using var compactLease = Pin(id);
            await using var compact = new FileStream(compactPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
            return await SequenceRxTimeIndex.ReadAsync(compact, start, bytes, token).ConfigureAwait(false);
        }
        var path = Path.Combine(RunPath(id), "rx-times.idx");
        if (!File.Exists(path)) return [new(start, bytes, null)]; // Older recordings have no RX clock.
        using var lease = Pin(id);
        await using var index = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
        var count = index.Length / RxTimeEntryBytes;
        var entry = new byte[RxTimeEntryBytes];
        long low = 0, high = count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            index.Position = middle * RxTimeEntryBytes;
            await index.ReadExactlyAsync(entry, token).ConfigureAwait(false);
            var offset = BinaryPrimitives.ReadInt64LittleEndian(entry);
            var length = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(8));
            if (offset + length <= start) low = middle + 1; else high = middle;
        }
        var rows = new List<SequenceRxSlice>();
        index.Position = low * RxTimeEntryBytes;
        while (low++ < count && rows.Count < PageSize)
        {
            await index.ReadExactlyAsync(entry, token).ConfigureAwait(false);
            var offset = BinaryPrimitives.ReadInt64LittleEndian(entry);
            var length = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(8));
            if (offset >= start + bytes.Length) break;
            if (offset < 0 || length <= 0) throw new InvalidDataException("Invalid RX timestamp index.");
            var from = Math.Max(start, offset);
            var to = Math.Min(start + bytes.Length, offset + length);
            if (from >= to) continue;
            rows.Add(new(from, bytes.AsSpan((int)(from - start), (int)(to - from)).ToArray(),
                new DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(entry.AsSpan(16)), TimeSpan.Zero),
                BinaryPrimitives.ReadInt64LittleEndian(entry.AsSpan(24)),
                BinaryPrimitives.ReadInt64LittleEndian(entry.AsSpan(32))));
        }
        return rows;
    }

    internal static int StatusCode(string status) => status switch
    { "matched" => 1, "not_matched" => 2, "not_requested" => 3, "canceled" => 4, "inconclusive" => 5, _ => 6 };

    // Never prune an active run or a run currently held by a results reader.
    internal void Prune()
    {
        if (!Directory.Exists(Root)) return;
        var folders = new DirectoryInfo(Root).EnumerateDirectories().OrderByDescending(d => d.CreationTimeUtc).ToArray();
        long retained = 0;
        for (var i = 0; i < folders.Length; i++)
        {
            var folder = folders[i];
            if ((folder.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            if (!Guid.TryParseExact(folder.Name, "N", out _)) continue;
            var files = folder.EnumerateFiles().ToArray();
            retained += files.Sum(f => f.Length);
            if (i < 100 && retained <= 4L * 1024 * 1024 * 1024) continue;
            try
            {
                using (var exclusive = new FileStream(Path.Combine(folder.FullName, "run.lock"), FileMode.Open,
                    FileAccess.ReadWrite, FileShare.Delete))
                    foreach (var file in files.Where(f => f.Name != "run.lock")) file.Delete();
                File.Delete(Path.Combine(folder.FullName, "run.lock"));
                Directory.Delete(folder.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
