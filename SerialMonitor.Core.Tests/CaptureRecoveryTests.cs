using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed class CaptureRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SerialMonitorRecoveryTests", Guid.NewGuid().ToString("N"));
    private static CaptureConnection Connected() => new(true, 7, 0);

    [Fact]
    public async Task ReopenedCatalogRecoversCorrelatedOutcomesAndByteExactRx()
    {
        string id;
        await using (var captures = new RxCaptureService(_root))
        {
            var result = await captures.CaptureAsync(new(0, "status", RequestId: "request-준비"), "COM7", Encoding.UTF8,
                Connected, _ =>
                {
                    captures.Offer(7, [0, 0xFF, 0x80], Stopwatch.GetTimestamp());
                    throw new IOException("Unknown TX outcome");
                }, default);
            id = result.CaptureId;
        }
        await using var reopened = new RxCaptureService(_root);
        var page = await reopened.ListAsync(requestId: "request-준비");
        var entry = Assert.Single(page.Captures);
        Assert.Equal(id, entry.CaptureId);
        Assert.Equal("request-준비", entry.RequestId);
        Assert.Equal("unknown", entry.SendStatus);
        Assert.Equal("send_error", entry.CaptureStatus);
        Assert.Equal("status", entry.CommandPreview);
        Assert.False(entry.CommandTruncated);
        Assert.Equal(3, entry.StoredBytes);
        Assert.False(page.CaptureInProgress);
        Assert.Equal("00FF80", (await reopened.ReadAsync(entry.CaptureId, format: "hex")).Text);
        Assert.Empty((await reopened.ListAsync(requestId: "another-request")).Captures);
    }

    [Fact]
    public async Task ActiveCaptureIsReportedBeforeItsPartialResultIsPublished()
    {
        await using var captures = new RxCaptureService(_root);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = captures.CaptureAsync(new(60000, "status", RequestId: "pending"), "COM7", Encoding.UTF8, Connected, _ =>
        {
            captures.Offer(7, "partial RX"u8.ToArray(), Stopwatch.GetTimestamp());
            sent.SetResult();
            return Task.CompletedTask;
        }, default);
        try
        {
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = await captures.ListAsync(requestId: "pending");
            Assert.True(pending.CaptureInProgress);
            Assert.Empty(pending.Captures);
        }
        finally { captures.Cancel(); }
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = await captures.ListAsync(requestId: "pending");
        Assert.False(saved.CaptureInProgress);
        var entry = Assert.Single(saved.Captures);
        Assert.Equal(result.CaptureId, entry.CaptureId);
        Assert.Equal("sent", entry.SendStatus);
        Assert.Equal("canceled", entry.CaptureStatus);
        Assert.Equal("partial RX", (await captures.ReadAsync(entry.CaptureId)).Text);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task PagesAreNewestFirstAndCommandPreviewsNeverSplitUnicode()
    {
        await using var captures = new RxCaptureService(_root);
        var results = new List<CaptureResult>();
        for (var i = 0; i < 5; i++)
            results.Add(await captures.CaptureAsync(new(0, new string('a', 159) + "🙂tail", RequestId: "request-" + i),
                "COM7", Encoding.UTF8, Connected, _ => Task.CompletedTask, default));
        var collected = new List<CaptureSummary>();
        var offset = 0;
        CaptureListPage page;
        do
        {
            page = await captures.ListAsync(offset, 2);
            Assert.Equal(5, page.TotalCount);
            Assert.InRange(page.Captures.Count, 1, 2);
            collected.AddRange(page.Captures);
            offset = page.NextOffset;
        } while (page.HasMore);
        Assert.Equal(results.OrderByDescending(result => result.StartedAt).ThenBy(result => result.CaptureId, StringComparer.Ordinal)
            .Select(result => result.CaptureId), collected.Select(entry => entry.CaptureId));
        Assert.All(collected, entry =>
        {
            Assert.Equal(new string('a', 159), entry.CommandPreview);
            Assert.True(entry.CommandTruncated);
        });
        Assert.Empty((await captures.ListAsync(int.MaxValue)).Captures);
        Assert.False((await captures.ListAsync(int.MaxValue)).HasMore);
    }

    [Fact]
    public async Task CorruptAndOversizedMetadataAreReportedWithoutHidingGoodOrLegacyRecords()
    {
        await using var captures = new RxCaptureService(_root);
        var result = await captures.CaptureAsync(new(0), "COM7", Encoding.UTF8, Connected, null, default);
        // Read pre-compact object metadata too; no request ID is required.
        await File.WriteAllTextAsync(Path.Combine(_root, result.CaptureId + ".json"), JsonSerializer.Serialize(result, AutomationJson.Options));
        await File.WriteAllTextAsync(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json"), "[2]");
        await File.WriteAllBytesAsync(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json"), new byte[256 * 1024 + 1]);
        await File.WriteAllTextAsync(Path.Combine(_root, "unrelated.json"), "not a capture");
        await File.WriteAllTextAsync(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tmp"), "unpublished");
        var page = await captures.ListAsync();
        Assert.Equal(result.CaptureId, Assert.Single(page.Captures).CaptureId);
        Assert.Null(page.Captures[0].RequestId);
        Assert.Equal(2, page.SkippedCount);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task MissingStoreIsEmptyAndCancellationDoesNotCreateFiles()
    {
        await using var captures = new RxCaptureService(_root);
        Assert.Empty((await captures.ListAsync()).Captures);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => captures.ListAsync(cancellationToken: stop.Token));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task MalformedMetadataCannotExpandAListReplyBeyondItsSummaryLimits()
    {
        await using var captures = new RxCaptureService(_root);
        var original = await captures.CaptureAsync(new(0), "COM7", Encoding.UTF8, Connected, null, default);
        var oversized = new string('x', 10000);
        foreach (var corrupt in new[]
        {
            original with { CaptureStatus = oversized }, original with { SendStatus = oversized },
            original with { Port = oversized }, original with { RequestId = oversized },
            original with { TxMode = oversized }, original with { LineEnding = oversized }
        })
        {
            var id = Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(Path.Combine(_root, id + ".json"),
                JsonSerializer.Serialize(corrupt with { CaptureId = id }, AutomationJson.Options));
        }
        var page = await captures.ListAsync(limit: 100);
        Assert.Equal(original.CaptureId, Assert.Single(page.Captures).CaptureId);
        Assert.Equal(6, page.SkippedCount);
    }

    [Theory]
    [InlineData(-1, 10, null)]
    [InlineData(0, 0, null)]
    [InlineData(0, 101, null)]
    [InlineData(0, 10, " ")]
    public async Task InvalidQueriesFailBeforeReadingDisk(int offset, int limit, string? requestId)
    {
        await using var captures = new RxCaptureService(_root);
        var error = await Assert.ThrowsAsync<AutomationException>(() => captures.ListAsync(offset, limit, requestId));
        Assert.Equal("invalid_argument", error.Code);
        Assert.False(Directory.Exists(_root));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
