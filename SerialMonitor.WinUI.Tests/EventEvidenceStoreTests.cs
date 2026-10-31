using System.Collections;
using System.Text;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class EventEvidenceStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeSizePayloadsRemainByteExactThroughPagingAndFullExport(bool hex)
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(EventEvidenceStoreTests), Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new EventEvidenceStore(root, pageCharacterLimit: 200_000);
            var before = new NativeLines(64, 0); var after = new NativeLines(64, 65);
            var middle = new NativeLines(1, 64)[0];
            var item = new DetectedEvent(DateTimeOffset.Now, "native", "x", LogDirection.Rx, middle.Text, middle);
            var evidence = await store.StoreContextAsync(new(item, before, after, 64, 64, DateTimeOffset.Now), default);
            string Format(LogLine line) => hex ? Convert.ToHexString(line.RawBytes!) : line.Text;
            using var expected = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            foreach (var line in before.Append(middle).Concat(after)) expected.AppendData(line.RawBytes!);
            var hash = expected.GetHashAndReset();
            using var read = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            long offset = 0; var pages = 0;
            while (true)
            {
                var page = await store.ReadPageAsync(evidence, offset, Format, default);
                Assert.InRange(page.Text.Length, 1, 200_000);
                foreach (var row in PayloadRows(page.Text)) read.AppendData(hex ? Convert.FromHexString(row) : Encoding.UTF8.GetBytes(row));
                pages++;
                if (!page.HasMore) break;
                offset = page.NextOffset;
            }
            Assert.True(pages > 1);
            Assert.Equal(hash, read.GetHashAndReset());
            var destination = Path.Combine(root, "native.log");
            await store.ExportAsync(evidence, destination, Format, default);
            foreach (var row in File.ReadLines(destination).Where(row => row.Length > 0 && !row.StartsWith("BEFORE (") && row is not ("BEFORE" or "AFTER" or "MATCHED")))
                read.AppendData(hex ? Convert.FromHexString(row) : Encoding.UTF8.GetBytes(row));
            Assert.Equal(hash, read.GetHashAndReset());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CompletingAPartialContextInvalidatesOldPageOffsets()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new EventEvidenceStore(root, pageCharacterLimit: 4096);
            var before = new GeneratedLines(10, "before", false);
            var item = new DetectedEvent(DateTimeOffset.Now, "r", "END", LogDirection.Rx, "END", LogLine.Rx("END"), before);
            var projection = await store.StoreEventAsync(item, default);
            var first = await store.ReadPageAsync(projection.Evidence!, 0, line => line.Text, default);
            Assert.True(first.HasMore);
            await store.StoreContextAsync(new(item, before, new GeneratedLines(10, "after", false), 10, 10, DateTimeOffset.Now), default);
            var refreshed = await store.ReadPageAsync(projection.Evidence!, first.NextOffset, line => line.Text, default, first.Version);
            Assert.Equal(0, refreshed.StartOffset);
            Assert.True(refreshed.IsComplete);
            Assert.True(refreshed.Version > first.Version);
            Assert.Contains("before_0000_", refreshed.Text);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ContextStorageFailureDoesNotStopEventMatching()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(path, "file");
        try
        {
            await using var detector = new EventDetector();
            await detector.StartAsync([new EventRule { Keyword = "FAULT" }], new(), default);
            typeof(EventDetector).GetField("_contextArchive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(detector, new ContextLineArchive(path));
            Assert.True(detector.TryEnqueue(LogLine.Rx("FAULT")));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal("FAULT", (await detector.DetectedEvents.ReadAsync(stop.Token)).Message);
            Assert.True(detector.IsRunning);
            Assert.True(detector.ContextCaptureFailedCount > 0);
            Assert.Contains("archival failed", detector.LastError);
            await detector.StopAsync(stop.Token);
        }
        finally { File.Delete(path); }
    }

    private sealed class NativeLines(int count, int start) : IReadOnlyList<LogLine>
    {
        public int Count => count;
        public LogLine this[int index]
        {
            get
            {
                var bytes = new byte[65536]; Array.Fill(bytes, (byte)('A' + (start + index) % 26));
                return LogLine.Rx(Encoding.UTF8.GetString(bytes), bytes);
            }
        }
        public IEnumerator<LogLine> GetEnumerator() { for (var i = 0; i < count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeBeforeAndAfterContextIsPagedAndFullyExportableWithoutTruncatingRecords(bool hex)
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(EventEvidenceStoreTests), Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new EventEvidenceStore(root, pageCharacterLimit: 4096);
            var before = new GeneratedLines(1000, "before", hex);
            var after = new GeneratedLines(1000, "after", hex);
            var source = LogLine.Rx("MATCHED_END", "MATCHED_END"u8.ToArray());
            var item = new DetectedEvent(DateTimeOffset.Now, "rule", "END", LogDirection.Rx, source.Text, source, before);
            var projected = await store.StoreEventAsync(item, default);
            Assert.Equal(item.Message, projected.Message);
            Assert.Same(source, projected.SourceLogLine);
            Assert.Empty(projected.BeforeContextLines);
            var reference = await store.StoreContextAsync(new(item, before, after, 1000, 1000, DateTimeOffset.Now), default);
            Assert.Same(projected.Evidence, reference);
            Assert.True(reference.IsComplete);
            var seen = new List<string>(); long offset = 0; var pages = 0;
            string Format(LogLine line) => hex && line.ContentMode == LogRuleMatchMode.Hex ? HexFormatter.Format(line.RawBytes!) : line.Text;
            do
            {
                var page = await store.ReadPageAsync(reference, offset, Format, default);
                Assert.InRange(page.Text.Length, 1, 4096);
                Assert.True(page.NextOffset > offset);
                seen.AddRange(PayloadRows(page.Text)); pages++;
                if (!page.HasMore) break;
                offset = page.NextOffset;
            } while (pages < 3000);
            Assert.True(pages > 1);
            var expected = Enumerable.Range(0, 1000).Select(i => Format(before[i])).Append(source.Text)
                .Concat(Enumerable.Range(0, 1000).Select(i => Format(after[i]))).ToArray();
            Assert.Equal(expected, seen);
            var destination = Path.Combine(root, "full.log");
            await store.ExportAsync(reference, destination, Format, default);
            Assert.Equal(expected, PayloadRows(await File.ReadAllTextAsync(destination)));
            // Late initial delivery cannot replace a completed context.
            await store.StoreEventAsync(item, default);
            Assert.True(reference.IsComplete);
            Assert.Equal(1000, reference.AfterCount);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StorageFailureIsExposedAndDoesNotFallBackToHeavyInMemoryContext()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(path, "not a directory");
        try
        {
            await using var store = new EventEvidenceStore(path);
            var errors = new List<string>(); store.Error += (_, error) => errors.Add(error);
            var item = new DetectedEvent(DateTimeOffset.Now, "test", "x", LogDirection.Rx, "x",
                beforeContextLines: new GeneratedLines(1000, "before", false));
            var projected = await store.StoreEventAsync(item, default);
            Assert.NotNull(projected.Evidence!.Error);
            Assert.Empty(projected.BeforeContextLines);
            Assert.NotEmpty(errors);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EventHistoryHasAByteLimitAndPreservesEachRetainedMessageInFull()
    {
        var view = new EventViewModel(100);
        for (var i = 0; i < 500; i++)
        {
            var text = new string('x', 65536) + $"END_{i}";
            var line = LogLine.Rx(text, Encoding.UTF8.GetBytes(text));
            view.AddRange([new(DateTimeOffset.Now, "rule", "END", LogDirection.Rx, text, line)]);
            Assert.InRange(view.RetainedMemoryBytes, 1, EventViewMemoryPolicy.VisibleBytes);
        }
        Assert.True(view.DroppedVisibleEventCount > 0);
        Assert.EndsWith("END_499", view.Events[^1].Message);
        Assert.Equal(65536 + "END_499".Length, view.Events[^1].Message.Length);
        view.Clear(); Assert.Equal(0, view.RetainedMemoryBytes);
    }

    [Fact]
    public void CleanupDoesNotTraverseNestedReparsePointsOrEscapingEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "archive-test");
        var instance = Path.Combine(root, "instance-test");
        var link = Path.Combine(instance, "link");
        var visited = new List<string>();
        IEnumerable<RuntimeArchiveCleanup.Entry> Enumerate(string path)
        {
            visited.Add(path);
            return [new(link, FileAttributes.Directory | FileAttributes.ReparsePoint)];
        }
        Assert.False(RuntimeArchiveCleanup.TryCollect(root, instance, Enumerate, out _));
        Assert.Equal([instance], visited);
        Assert.False(RuntimeArchiveCleanup.TryCollect(root, instance,
            _ => [new(Path.Combine(root, "outside", "sentinel"), FileAttributes.Normal)], out _));
    }

    private static IEnumerable<string> PayloadRows(string text) => text.Split('\n').Select(row => row.TrimEnd('\r'))
        .Where(row => row.Length > 0 && row is not ("BEFORE" or "AFTER" or "MATCHED") && !row.StartsWith("BEFORE ("));
    private sealed class GeneratedLines(int count, string prefix, bool hex) : IReadOnlyList<LogLine>
    {
        public int Count => count;
        public LogLine this[int index]
        {
            get
            {
                var text = $"{prefix}_{index:D4}_" + new string('x', 600) + "_END";
                var bytes = Encoding.UTF8.GetBytes(text);
                return LogLine.Rx(text, bytes, contentMode: hex ? LogRuleMatchMode.Hex : LogRuleMatchMode.Terminal);
            }
        }
        public IEnumerator<LogLine> GetEnumerator() { for (var i = 0; i < count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
