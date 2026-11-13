using System.Diagnostics;
using System.Text.Json;
using SerialMonitor.WinUI.Models;

internal sealed record WireEntry(string Case, string Direction, long Generation, long Timestamp, bool NativeBoundary, byte[] Bytes);
internal sealed record CaseResult(string Name, string Status, string? Error = null);

internal sealed class Evidence(string directory)
{
    private readonly object _gate = new();
    private readonly List<WireEntry> _entries = [];
    private readonly List<string> _errors = [];
    private string _case = "preflight";
    private int _bytes;
    private bool _overflow;
    public string Directory { get; } = directory;
    public List<CaseResult> Results { get; } = [];
    public void Select(string name) { lock (_gate) _case = name; }
    public void Rx(long generation, ReceivedByteChunk chunk) => Add("RX", generation, chunk.ReceivedTimestamp, chunk.EndsAtNativeIdleBoundary, chunk.Bytes);
    public void Tx(byte[] bytes, long generation) => Add("TX", generation, Stopwatch.GetTimestamp(), false, bytes);
    public void Error(object? sender, string message)
    {
        lock (_gate) { if (_errors.Count < 128) _errors.Add(message); else _overflow = true; }
    }
    private void Add(string direction, long generation, long timestamp, bool nativeBoundary, byte[] bytes)
    {
        lock (_gate)
        {
            if (_entries.Count >= 32768 || bytes.Length > 16 * 1024 * 1024 - _bytes) { _overflow = true; return; }
            _entries.Add(new(_case, direction, generation, timestamp, nativeBoundary, bytes)); _bytes += bytes.Length;
        }
    }
    public WireEntry[] Entries(string name) { lock (_gate) return _entries.Where(entry => entry.Case == name).ToArray(); }
    public byte[] RxBytes(string name) => Entries(name).Where(entry => entry.Direction == "RX").SelectMany(entry => entry.Bytes).ToArray();
    public void RequireComplete()
    {
        lock (_gate) Oracle.Require(!_overflow && _errors.Count == 0, "Incomplete evidence or serial error: " + string.Join("; ", _errors));
    }
    public void CheckExchanges(string name)
    {
        RequireComplete();
        var tx = Entries(name).Where(entry => entry.Direction == "TX").Select(entry => entry.Bytes).ToArray();
        var rx = Oracle.Frames(RxBytes(name));
        Oracle.Require(tx.Length > 0 && tx.Length == rx.Count, "TX/RX frame count mismatch: " + name);
        for (var index = 0; index < tx.Length; index++)
            if (tx[index][1] is >= 1 and <= 4) _ = Oracle.Values(tx[index], rx[index]);
            else Oracle.Require(Oracle.Crc(tx[index]) == 0 && rx[index].AsSpan().SequenceEqual(Oracle.Ack(tx[index])), "Write ACK mismatch: " + name);
    }
    public async Task SaveAsync(object report, bool passed)
    {
        WireEntry[] entries; string[] errors; bool overflow;
        lock (_gate) { entries = _entries.ToArray(); errors = _errors.ToArray(); overflow = _overflow; }
        await using (var stream = new FileStream(Path.Combine(Directory, "wire.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 8192, true))
        await using (var writer = new StreamWriter(stream))
            foreach (var entry in entries) await writer.WriteLineAsync(JsonSerializer.Serialize(new
            { entry.Case, entry.Direction, entry.Generation, entry.Timestamp, entry.NativeBoundary, hex = Convert.ToHexString(entry.Bytes) }));
        foreach (var name in entries.Select(entry => entry.Case).Distinct())
            foreach (var direction in new[] { "TX", "RX" })
                await File.WriteAllBytesAsync(Path.Combine(Directory, name + "." + direction.ToLowerInvariant() + ".bin"),
                    entries.Where(entry => entry.Case == name && entry.Direction == direction).SelectMany(entry => entry.Bytes).ToArray());
        await File.WriteAllTextAsync(Path.Combine(Directory, "report.json"), JsonSerializer.Serialize(new
        { passed, report, evidenceOverflow = overflow, serialErrors = errors, retainedBytes = entries.Sum(entry => entry.Bytes.Length), retainedChunks = entries.Length,
            stopwatchFrequency = Stopwatch.Frequency, txTimeMeaning = "Host send attempt for raw/LAB commands; completed SerialService send for engine traffic. No physical per-byte timestamps.",
            results = Results }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
