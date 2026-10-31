using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SerialMonitor.Core.Automation;
using SerialMonitor.Core.Sequences;

namespace SerialMonitor.Core.Tests;

public sealed class CompactResultStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CompactResultTests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Origin = new(2026, 10, 31, 13, 42, 30, TimeSpan.FromHours(9));
    private static readonly SequenceRunDefinition Definition = new("Test", 100, "COM3", "Utf8",
        [new(1, "PING 준비", "Terminal", "Crlf", 0, null, "50494E47"),
         new(2, "PONG 준비", "Terminal", "None", 700, new("text", "PONG 준비"), "", "RxCheck")]);

    [Theory]
    [InlineData("matched", "completed", "not_requested", 1)]
    [InlineData("not_matched", "completed", "not_requested", 1)]
    [InlineData("inconclusive", "inconclusive", "unknown", 3)]
    [InlineData("canceled", "canceled", "not_requested", 1)]
    [InlineData("not_requested", "completed", "sent", 2)]
    [InlineData("future_status", "future_state", "not_sent", 1)]
    public void StepsRoundTripAllEvidenceAndDefinitionOverrides(string status, string state, string send, int attempt)
    {
        var codec = new SequenceStepStorage(Definition, Origin);
        var start = Origin.ToUniversalTime().AddTicks(123456789);
        var item = new SequenceStepResult(23, 12, 2, attempt, "runtime command", "ABCD", send, status, null, "hex", 17,
            start, start.AddTicks(87654321).ToOffset(TimeSpan.FromHours(9)), 0.0, 21, 23, 29, 22, 1, "중단 <원인>",
            state, "Tx", start.AddTicks(-7).ToOffset(TimeSpan.FromHours(-4)), 1, start.AddTicks(-12),
            null, "different previous command", null, [0xF0, 0x9F]);
        using var document = JsonDocument.Parse(codec.Serialize(item));
        var decoded = codec.Deserialize(document.RootElement, item.Id, item.Repeat, item.Step);
        Assert.Equal(JsonSerializer.Serialize(item), JsonSerializer.Serialize(decoded));
        Assert.Equal(item.DisplayText, decoded.DisplayText);
        Assert.Equal(item.TimeText, decoded.TimeText);
    }

    [Fact]
    public void RepeatedRowsContainOnlyChangingValuesAndAreAtLeastEightyPercentSmaller()
    {
        var codec = new SequenceStepStorage(Definition, Origin);
        var start = Origin.ToUniversalTime().AddTicks(123456789);
        var item = new SequenceStepResult(1, 1, 2, 1, "PONG 준비", "", "not_requested", "matched", "PONG 준비", "text", 700,
            start, start.AddMilliseconds(700).ToOffset(Origin.Offset), 1.25, 100, 111, 111, 100, 11, null,
            StepKind: "RxCheck", MatchedAt: start.AddMilliseconds(1.25), RelatedTxStep: 1,
            RelatedTxAt: start.AddMilliseconds(-10), RelatedTxHex: "50494E47", RelatedTxCommand: "PING 준비", TxMode: "Terminal");
        var compact = codec.Serialize(item);
        var verbose = JsonSerializer.SerializeToUtf8Bytes(item);
        Assert.True(compact.Length < verbose.Length * 0.2, $"{compact.Length} compact / {verbose.Length} verbose bytes");
        var json = Encoding.UTF8.GetString(compact);
        Assert.DoesNotContain("PING", json); Assert.DoesNotContain("PONG", json);
        Assert.DoesNotContain("Pattern", json); Assert.DoesNotContain("null", json);
        using var document = JsonDocument.Parse(compact);
        Assert.Equal(JsonSerializer.Serialize(item), JsonSerializer.Serialize(codec.Deserialize(document.RootElement, 1, 1, 2)));
    }

    [Fact]
    public async Task LegacyRowsAndCompactRowsCanBeReadWithTheSamePagingAndFilters()
    {
        var store = new SequenceRunStore(_root);
        var id = Guid.NewGuid().ToString("N");
        var path = store.RunPath(id); Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "run.lock"), "");
        await File.WriteAllTextAsync(Path.Combine(path, "definition.json"), JsonSerializer.Serialize(Definition));
        await File.WriteAllTextAsync(Path.Combine(path, "summary.json"), JsonSerializer.Serialize(
            new SequenceRunSummary(id, "Test", Origin, Origin.AddMinutes(1), "completed", 205, 0, 0, 205, 0, 205, 0)));
        var codec = new SequenceStepStorage(Definition, Origin);
        await using (var data = File.Create(Path.Combine(path, "steps.jsonl")))
        await using (var index = File.Create(Path.Combine(path, "steps.idx")))
        {
            for (var i = 1; i <= 205; i++)
            {
                var item = new SequenceStepResult(i, i, 1, 1, "PING 준비", "50494E47", "sent", "not_requested", null, null, 0,
                    Origin.AddTicks(i), Origin.AddTicks(i + 1), null, 0, 0, 0, null, 0, null, TxMode: "Terminal");
                // Legacy records may have properties used only by old UI bindings.
                var json = i % 2 == 0 ? codec.Serialize(item) : Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(item)[..^1] + ",\"DisplayText\":\"old UI\"}");
                var entry = new byte[24];
                BinaryPrimitives.WriteInt64LittleEndian(entry, data.Position);
                BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(8), json.Length);
                BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(12), i);
                BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(16), 3);
                BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(20), 1);
                await data.WriteAsync(json); await data.WriteAsync(new byte[] { 10 }); await index.WriteAsync(entry);
            }
        }
        var first = await store.ReadPageAsync(id, filter: "not_requested");
        Assert.Equal(200, first.Rows.Count); Assert.True(first.HasMore);
        var second = await store.ReadPageAsync(id, first.NextCursor);
        Assert.Equal(5, second.Rows.Count); Assert.False(second.HasMore);
        var selected = Assert.Single((await store.ReadPageAsync(id, repeat: 204)).Rows);
        Assert.Equal(204, selected.Id); Assert.Equal(204, selected.Repeat); Assert.Equal("PING 준비", selected.Command);
        Assert.Equal(Origin.AddTicks(204).Offset, selected.StartedAt.Offset);
    }

    [Fact]
    public async Task ActualRecorderWritesCompactFilesAndPreservesLiveAndReopenedResults()
    {
        // Non-contiguous numbers model disabled editor rows. Long TX values
        // exercise the same preview bounds as the actual recorder.
        var tx = new SequenceRunStep(3, new string('가', 4200), "Terminal", "None", 0, null, new string('A', 34000));
        var rx = new SequenceRunStep(7, "준비", "Terminal", "None", 0, new("text", "준비"), "", "RxCheck");
        const int repeats = 101;
        var definition = new SequenceRunDefinition("Recorder integration", repeats, "MOCK", "Utf8", [tx, rx]);
        var store = new SequenceRunStore(_root);
        var payload = Encoding.UTF8.GetBytes("준비");
        var clocks = new List<(long Timestamp, long Generation)>();
        string id;
        string liveResults;
        long verboseBytes = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        await using (var session = await SequenceRunSession.StartAsync(store, definition, Encoding.UTF8, token))
        {
            id = session.RunId;
            for (var repeat = 1; repeat <= repeats; repeat++)
            {
                var generation = (long)repeat;
                await session.BeginAsync(tx, repeat, 1, generation, token);
                session.ArmForSend();
                // Split a multibyte character across receive callbacks.
                foreach (var part in new[] { payload[..1], payload[1..] })
                {
                    var timestamp = Stopwatch.GetTimestamp();
                    clocks.Add((timestamp, generation));
                    session.Offer(generation, part, timestamp);
                }
                await session.SentAsync(true, Stopwatch.GetTimestamp(), token);
                await session.EndStepAsync(token);
                await session.BeginAsync(rx, repeat, 1, generation, token);
                await session.CheckFullWindowAsync(token);
                await session.EndStepAsync(token);
            }
            Assert.Null(session.Fault);
            Assert.Equal(repeats, session.Summary.MatchedRepeats);
            Assert.Equal(repeats * payload.Length, session.Summary.RxBytes);
            var first = await store.ReadPageAsync(id, token: token);
            Assert.Equal(200, first.Rows.Count);
            Assert.True(first.HasMore);
            var last = await store.ReadPageAsync(id, first.NextCursor, token: token);
            Assert.Equal(2, last.Rows.Count);
            Assert.False(last.HasMore);
            var rows = first.Rows.Concat(last.Rows).ToArray();
            Assert.Equal(Enumerable.Range(1, repeats * 2).Select(i => (long)i), rows.Select(r => r.Id));
            Assert.All(rows.Where(r => r.StepKind == "Tx"), row =>
            {
                Assert.Equal(SequenceStepStorage.Preview(tx.Command, 4096), row.Command);
                Assert.Equal(SequenceStepStorage.Preview(tx.TxHex, 32768), row.TxHex);
            });
            Assert.All(rows.Where(r => r.StepKind == "RxCheck"), row =>
            {
                Assert.Equal("matched", row.CheckStatus);
                Assert.Equal(3, row.RelatedTxStep);
                Assert.Equal(payload.Length, row.MatchLength);
                Assert.NotNull(row.MatchedAt);
                Assert.Equal(SequenceStepStorage.Preview(tx.Command, 4096), row.RelatedTxCommand);
            });
            var filtered = await store.ReadPageAsync(id, filter: "matched", repeat: repeats, token: token);
            Assert.Equal(repeats * 2, Assert.Single(filtered.Rows).Id);
            liveResults = JsonSerializer.Serialize(rows);
            verboseBytes = rows.Sum(row => (long)JsonSerializer.SerializeToUtf8Bytes(row).Length + 1);
            await session.FinishAsync("completed").WaitAsync(TimeSpan.FromSeconds(5));
        }
        var reopened = new SequenceRunStore(_root);
        var page = await reopened.ReadPageAsync(id, token: token);
        var tail = await reopened.ReadPageAsync(id, page.NextCursor, token: token);
        Assert.Equal(liveResults, JsonSerializer.Serialize(page.Rows.Concat(tail.Rows).ToArray()));
        Assert.Equal("completed", (await reopened.ReadSummaryAsync(id, token))?.State);
        var path = reopened.RunPath(id);
        var jsonl = await File.ReadAllLinesAsync(Path.Combine(path, "steps.jsonl"), token);
        Assert.Equal(repeats * 2, jsonl.Length);
        Assert.All(jsonl, line => Assert.StartsWith("[2,", line));
        Assert.True(new FileInfo(Path.Combine(path, "steps.jsonl")).Length < verboseBytes / 5);
        Assert.False(File.Exists(Path.Combine(path, "rx-times.idx")));
        var header = await File.ReadAllBytesAsync(Path.Combine(path, SequenceRxTimeIndex.FileName), token);
        Assert.Equal(SequenceRxTimeIndex.HeaderBytes + clocks.Count * SequenceRxTimeIndex.EntryBytes, header.Length);
        var anchor = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
        var wall = new DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16)), TimeSpan.Zero);
        var slices = new List<SequenceRxSlice>();
        for (long offset = 0; offset < repeats * payload.Length;)
        {
            var timeline = await reopened.ReadRxTimelineAsync(id, offset, repeats * payload.Length, token);
            Assert.NotEmpty(timeline);
            Assert.InRange(timeline.Count, 1, SequenceRunStore.PageSize);
            slices.AddRange(timeline);
            offset = timeline[^1].Offset + timeline[^1].Bytes.Length;
        }
        Assert.Equal(clocks.Count, slices.Count);
        Assert.Equal(Enumerable.Repeat(payload, repeats).SelectMany(p => p), slices.SelectMany(s => s.Bytes));
        for (var i = 0; i < clocks.Count; i++)
        {
            Assert.Equal(clocks[i].Timestamp, slices[i].Timestamp);
            Assert.Equal(clocks[i].Generation, slices[i].Generation);
            Assert.Equal(wall + Stopwatch.GetElapsedTime(anchor, clocks[i].Timestamp), slices[i].ReceivedAt);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(199)]
    [InlineData(399)]
    public async Task CompactRxIndexPreservesLegacyChunkBoundariesClocksAndLongGenerations(int start)
    {
        var store = new SequenceRunStore(_root);
        var id = Guid.NewGuid().ToString("N");
        var path = store.RunPath(id); Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "run.lock"), "");
        // Two raw tail bytes have no committed timestamp entry (crash/live append).
        var raw = Enumerable.Range(0, 412).Select(i => (byte)i).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(path, "rx-000000.bin"), raw);
        var anchor = Stopwatch.GetTimestamp();
        var wall = Origin.ToUniversalTime();
        await using (var legacy = File.Create(Path.Combine(path, "rx-times.idx")))
        await using (var compact = File.Create(Path.Combine(path, SequenceRxTimeIndex.FileName)))
        {
            await compact.WriteAsync(SequenceRxTimeIndex.Header(anchor, wall, Stopwatch.Frequency));
            for (var i = 0; i < 205; i++)
            {
                var timestamp = anchor + i * 1357;
                var generation = long.MaxValue - i / 100;
                var entry = new byte[40];
                BinaryPrimitives.WriteInt64LittleEndian(entry, i * 2);
                BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(8), 2);
                BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(16), (wall + Stopwatch.GetElapsedTime(anchor, timestamp)).UtcTicks);
                BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(24), timestamp);
                BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(32), generation);
                await legacy.WriteAsync(entry);
                await compact.WriteAsync(SequenceRxTimeIndex.Entry(i * 2, 2, timestamp, generation));
            }
            await compact.WriteAsync(new byte[7]); // An incomplete final entry is ignored.
        }
        var compactRows = await store.ReadRxTimelineAsync(id, start, raw.Length);
        File.Move(Path.Combine(path, SequenceRxTimeIndex.FileName), Path.Combine(path, "compact.saved"));
        var oldRows = await store.ReadRxTimelineAsync(id, start, raw.Length);
        Assert.Equal(JsonSerializer.Serialize(oldRows), JsonSerializer.Serialize(compactRows));
        Assert.Equal(32 + 205 * 24 + 7, new FileInfo(Path.Combine(path, "compact.saved")).Length);
        Assert.Equal(start, compactRows[0].Offset);
        Assert.True(compactRows[^1].Offset + compactRows[^1].Bytes.Length <= 410);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureMetadataRoundTripsEveryPublicFieldAndReadsTheOldFormat(bool failure)
    {
        var id = Guid.NewGuid().ToString("N");
        var item = new CaptureResult(id, failure ? "write_error" : "completed", "sent", "PING 준비", "COM3", long.MaxValue,
            Origin, Origin.AddTicks(123456789).ToUniversalTime(), 700, 1234, failure ? 123 : 1234,
            failure ? 1111 : 0, failure ? 2 : 0, failure, failure ? "쓰기에 실패" : null,
            Path.Combine(_root, id + ".bin"), Path.Combine(_root, id + ".txt"), "hex", "none", "hex");
        var compact = CaptureResultStorage.Serialize(item, _root);
        var verbose = JsonSerializer.SerializeToUtf8Bytes(item, AutomationJson.Options);
        using var compactJson = JsonDocument.Parse(compact);
        using var oldJson = JsonDocument.Parse(verbose);
        Assert.Equal(item, CaptureResultStorage.Deserialize(compactJson.RootElement, _root, id));
        Assert.Equal(item, CaptureResultStorage.Deserialize(oldJson.RootElement, _root, id));
        Assert.True(compact.Length < verbose.Length / 2);
    }

    [Fact]
    public async Task CaptureFilesRemainReadableFromANewServiceInstance()
    {
        CaptureResult original;
        await using (var service = new RxCaptureService(_root))
        {
            original = await service.CaptureAsync(new(0, "PING", "hex", "none", "hex"), "COM3", Encoding.UTF8,
                () => new(true, 1, 0), _ => { service.Offer(1, [0, 0xFF, 0xEA], Stopwatch.GetTimestamp()); return Task.CompletedTask; }, default);
        }
        await using var reopened = new RxCaptureService(_root);
        Assert.Equal(original, reopened.GetResult(original.CaptureId));
        Assert.Equal("00FFEA", (await reopened.ReadAsync(original.CaptureId, format: "hex")).Text);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
