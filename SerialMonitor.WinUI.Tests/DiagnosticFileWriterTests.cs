using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class DiagnosticFileWriterTests
{
    [Fact]
    public async Task StalledDisk_DoesNotBlockCaller_AndKeepsOnlyLatestPendingValues()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<(DiagnosticFile File, string? Text)>();
        var writer = new DiagnosticFileWriter(async (file, text) =>
        {
            writes.Add((file, text));
            if (writes.Count == 1)
            {
                entered.SetResult();
                await release.Task;
            }
        });
        writer.Enqueue(DiagnosticFile.Startup, "start");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Task.Run(() =>
            {
                for (var i = 0; i < 10_000; i++)
                    writer.Enqueue(DiagnosticFile.Error, i.ToString());
                writer.Enqueue(DiagnosticFile.Shutdown, "shutdown");
            }).WaitAsync(TimeSpan.FromSeconds(5));
            // A stuck write must not make bounded shutdown flushing hang.
            await writer.FlushAsync(TimeSpan.FromMilliseconds(20)).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.TrySetResult(); }
        await writer.FlushAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, writes.Count);
        Assert.Contains((DiagnosticFile.Error, "9999"), writes);
        Assert.Contains((DiagnosticFile.Shutdown, "shutdown"), writes);
    }

    [Fact]
    public async Task FailedWrite_DoesNotStopLaterWrites_AndWorkerCanRestart()
    {
        var calls = 0;
        string? saved = null;
        var writer = new DiagnosticFileWriter((_, text) =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("disk failed");
            saved = text;
            return Task.CompletedTask;
        });
        writer.Enqueue(DiagnosticFile.Error, "first");
        await writer.FlushAsync(TimeSpan.FromSeconds(5));
        writer.Enqueue(DiagnosticFile.Error, "second");
        await writer.FlushAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("second", saved);
        writer.Enqueue(DiagnosticFile.Error, null);
        await writer.FlushAsync(TimeSpan.FromSeconds(5));
        Assert.Null(saved);
        Assert.Equal(3, calls);
    }
}
