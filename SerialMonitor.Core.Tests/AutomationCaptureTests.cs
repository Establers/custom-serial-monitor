using System.Diagnostics;
using System.Text;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed class AutomationCaptureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SerialMonitorCaptureTests", Guid.NewGuid().ToString("N"));
    private static CaptureConnection Connected() => new(true, 7, 0);

    [Fact]
    public async Task ImmediateInterleavedResponseIsPreservedWithoutMatching()
    {
        await using var captures = new RxCaptureService(_root);
        var result = await captures.CaptureAsync(new(1, "status"), "COM7", Encoding.UTF8, Connected, _ =>
        {
            captures.Offer(7, Encoding.UTF8.GetBytes("abc1d234\r\n> "), Stopwatch.GetTimestamp());
            return Task.CompletedTask;
        }, default);
        Assert.Equal("completed", result.CaptureStatus);
        Assert.Equal("sent", result.SendStatus);
        Assert.False(result.RxGapDetected);
        Assert.Equal("abc1d234\r\n> ", (await captures.ReadAsync(result.CaptureId)).Text);
        Assert.Equal(Encoding.UTF8.GetBytes("abc1d234\r\n> "), await File.ReadAllBytesAsync(result.RawFile));
    }

    [Fact]
    public async Task LargeSplitUtf8CaptureCanBeReadCompletelyWithoutDuplicateOrMissingBytes()
    {
        await using var captures = new RxCaptureService(_root);
        var text = string.Concat(Enumerable.Repeat("가🙂abc1d234\r\n", 4000));
        var bytes = Encoding.UTF8.GetBytes(text);
        var result = await captures.CaptureAsync(new(1), "COM7", Encoding.UTF8, Connected, _ =>
        {
            for (var i = 0; i < bytes.Length; i += 997)
                captures.Offer(7, bytes.AsSpan(i, Math.Min(997, bytes.Length - i)).ToArray(), Stopwatch.GetTimestamp());
            return Task.CompletedTask;
        }, default);
        var collected = new StringBuilder();
        long offset = 0;
        CapturePage page;
        do
        {
            page = await captures.ReadAsync(result.CaptureId, offset, 101);
            collected.Append(page.Text);
            Assert.True(page.NextOffset > offset);
            offset = page.NextOffset;
        } while (page.HasMore);
        Assert.Equal(text, collected.ToString());
        Assert.Equal(bytes.Length, result.StoredBytes);
        Assert.Equal(Convert.ToHexString(bytes), (await captures.ReadAsync(result.CaptureId, 0, 65536, "hex")).Text +
            (await captures.ReadAsync(result.CaptureId, 65536, 65536, "hex")).Text);
    }

    [Fact]
    public async Task CancellationPersistsPartialCaptureAndNextCaptureStillWorks()
    {
        await using var captures = new RxCaptureService(_root);
        using var cancellation = new CancellationTokenSource();
        var first = await captures.CaptureAsync(new(60000), "COM7", Encoding.UTF8, Connected, _ =>
        {
            captures.Offer(7, "partial"u8.ToArray(), Stopwatch.GetTimestamp());
            cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellation.Token);
        Assert.Equal("canceled", first.CaptureStatus);
        Assert.Equal("partial", (await captures.ReadAsync(first.CaptureId)).Text);
        var next = await captures.CaptureAsync(new(0), "COM7", Encoding.UTF8, Connected, null, default);
        Assert.Equal("completed", next.CaptureStatus);
    }

    [Fact]
    public async Task DisconnectStopsCaptureAndRejectsOtherSessionBytes()
    {
        await using var captures = new RxCaptureService(_root);
        var connection = Connected();
        var result = await captures.CaptureAsync(new(60000), "COM7", Encoding.UTF8, () => connection, _ =>
        {
            captures.Offer(6, "OLD"u8.ToArray(), Stopwatch.GetTimestamp());
            captures.Offer(7, "current"u8.ToArray(), Stopwatch.GetTimestamp());
            connection = new(false, 8, 0);
            captures.ConnectionChanged(connection);
            captures.Offer(8, "NEW"u8.ToArray(), Stopwatch.GetTimestamp());
            return Task.CompletedTask;
        }, default);
        Assert.Equal("disconnected", result.CaptureStatus);
        Assert.Equal("current", (await captures.ReadAsync(result.CaptureId)).Text);
    }

    [Fact]
    public async Task ConcurrentCaptureIsBusyAndDoesNotDeleteActiveFiles()
    {
        await using var captures = new RxCaptureService(_root);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = captures.CaptureAsync(new(60000), "COM7", Encoding.UTF8, Connected, _ =>
        {
            captures.Offer(7, "kept"u8.ToArray(), Stopwatch.GetTimestamp());
            started.SetResult();
            return Task.CompletedTask;
        }, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<AutomationException>(() => captures.CaptureAsync(new(0), "COM7", Encoding.UTF8, Connected, null, default));
        Assert.Equal("busy", error.Code);
        Assert.True(captures.Cancel());
        var result = await first;
        Assert.Equal("kept", (await captures.ReadAsync(result.CaptureId)).Text);
    }

    [Fact]
    public async Task OverloadAndHardwareErrorsAreNeverReportedAsCompleteCapture()
    {
        await using var captures = new RxCaptureService(_root);
        var connection = Connected();
        var result = await captures.CaptureAsync(new(60000), "COM7", Encoding.UTF8, () => connection, _ =>
        {
            captures.Offer(7, new byte[9 * 1024 * 1024], Stopwatch.GetTimestamp());
            connection = connection with { SerialErrorCount = 1 };
            return Task.CompletedTask;
        }, default);
        Assert.Equal("rx_gap", result.CaptureStatus);
        Assert.True(result.RxGapDetected);
        Assert.Equal(9 * 1024 * 1024, result.DroppedBytes);
        Assert.Equal(1, result.SerialErrors);
    }

    [Fact]
    public async Task FailedSendIsNotRetriedAndStillReturnsActualRx()
    {
        await using var captures = new RxCaptureService(_root);
        var attempts = 0;
        var result = await captures.CaptureAsync(new(1000, "reset"), "COM7", Encoding.UTF8, Connected, _ =>
        {
            attempts++;
            captures.Offer(7, "maybe sent"u8.ToArray(), Stopwatch.GetTimestamp());
            throw new IOException("TX interrupted");
        }, default);
        Assert.Equal(1, attempts);
        Assert.Equal("unknown", result.SendStatus);
        Assert.Equal("send_error", result.CaptureStatus);
        Assert.Equal("maybe sent", (await captures.ReadAsync(result.CaptureId)).Text);
    }

    [Fact]
    public async Task InvalidCaptureIdCannotReadOutsideStore()
    {
        await using var captures = new RxCaptureService(_root);
        await Assert.ThrowsAsync<AutomationException>(() => captures.ReadAsync("../private"));
    }

    [Fact]
    public async Task WriterFailureReturnsTxOutcomeAndPreservesRawBytes()
    {
        await using var captures = new RxCaptureService(_root);
        var result = await captures.CaptureAsync(new(1000, "status"), "COM7", new UTF8Encoding(false, true), Connected, _ =>
        {
            captures.Offer(7, [0xFF], Stopwatch.GetTimestamp());
            return Task.CompletedTask;
        }, default);
        Assert.Equal("write_error", result.CaptureStatus);
        Assert.Equal("sent", result.SendStatus);
        Assert.True(result.RxGapDetected);
        Assert.NotNull(result.Error);
        Assert.Equal(new byte[] { 0xFF }, await File.ReadAllBytesAsync(result.RawFile));
    }

    [Theory]
    [InlineData(".bin")]
    [InlineData(".txt")]
    [InlineData(".json")]
    public async Task PruneSkipsWholeLockedCaptureAndEvictsAnotherOldCapture(string lockedExtension)
    {
        await using var captures = new RxCaptureService(_root);
        var previous = new List<CaptureResult>();
        for (var i = 0; i < 32; i++)
            previous.Add(await captures.CaptureAsync(new(0), "COM7", Encoding.UTF8, Connected, _ =>
            {
                captures.Offer(7, "preserve all files"u8.ToArray(), Stopwatch.GetTimestamp());
                return Task.CompletedTask;
            }, default));
        var oldest = previous[0];
        File.SetCreationTimeUtc(Path.Combine(_root, oldest.CaptureId + ".json"), DateTime.UtcNow.AddDays(-1));
        var rawBefore = await File.ReadAllBytesAsync(oldest.RawFile);
        var textBefore = await File.ReadAllTextAsync(oldest.TextFile);
        using var held = new FileStream(Path.Combine(_root, oldest.CaptureId + lockedExtension), FileMode.Open, FileAccess.Read, FileShare.Read);
        var next = await captures.CaptureAsync(new(0), "COM7", Encoding.UTF8, Connected, null, default);
        Assert.Equal("completed", next.CaptureStatus);
        Assert.Equal(oldest.CaptureId, captures.GetResult(oldest.CaptureId).CaptureId);
        Assert.Equal(rawBefore, await File.ReadAllBytesAsync(oldest.RawFile));
        Assert.Equal(textBefore, (await captures.ReadAsync(oldest.CaptureId)).Text);
        Assert.Equal(32, Directory.EnumerateFiles(_root, "*.json").Count());
    }

    [Fact]
    public async Task LockedOrphanDoesNotFailCaptureAndIsRemovedAfterRelease()
    {
        await using var captures = new RxCaptureService(_root);
        Directory.CreateDirectory(_root);
        var orphan = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllTextAsync(orphan, "unfinished");
        using (var held = new FileStream(orphan, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var next = await captures.CaptureAsync(new(0), "COM7", Encoding.UTF8, Connected, null, default);
            Assert.Equal("completed", next.CaptureStatus);
            Assert.True(File.Exists(orphan));
        }
        await captures.CaptureAsync(new(0), "COM7", Encoding.UTF8, Connected, null, default);
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task RetentionRecoversAfterAllOldCapturesAreUnpinned()
    {
        await using var captures = new RxCaptureService(_root);
        var held = new List<FileStream>();
        try
        {
            for (var i = 0; i < 32; i++)
            {
                var result = await captures.CaptureAsync(new(0), "COM7", Encoding.UTF8, Connected, null, default);
                held.Add(new FileStream(result.RawFile, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
            Assert.Equal("completed", (await captures.CaptureAsync(new(0), "COM7", Encoding.UTF8, Connected, null, default)).CaptureStatus);
            Assert.Equal(33, Directory.EnumerateFiles(_root, "*.json").Count());
        }
        finally { foreach (var file in held) file.Dispose(); }
        await captures.CaptureAsync(new(0), "COM7", Encoding.UTF8, Connected, null, default);
        Assert.Equal(32, Directory.EnumerateFiles(_root, "*.json").Count());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
