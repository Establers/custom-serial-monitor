using System.Text;
using SerialMonitor.Core.Sequences;

namespace SerialMonitor.Core.Tests;

public sealed class SequenceSummaryFileCommitTests
{
    [Fact]
    public async Task ABlockedPublicationCanBeCanceledWithoutChangingThePreviousSummary()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), nameof(SequenceSummaryFileCommitTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var summary = Path.Combine(root, "summary.json");
        var temporary = Path.Combine(root, "summary.tmp");
        try
        {
            await File.WriteAllTextAsync(summary, "old");
            await File.WriteAllTextAsync(temporary, "new");
            using var reader = new FileStream(summary, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var stop = new CancellationTokenSource();
            var publication = SequenceSummaryFileCommit.CommitAsync(temporary, summary, stop.Token);
            Assert.False(publication.IsCompleted);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publication);
            Assert.Equal("old", await File.ReadAllTextAsync(summary));
            Assert.Equal("new", await File.ReadAllTextAsync(temporary));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task APersistentLockStillReportsFailureWithinABoundedTime()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), nameof(SequenceSummaryFileCommitTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var summary = Path.Combine(root, "summary.json");
        var temporary = Path.Combine(root, "summary.tmp");
        try
        {
            await File.WriteAllTextAsync(summary, "old");
            await File.WriteAllTextAsync(temporary, "new");
            using var reader = new FileStream(summary, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var failure = await Record.ExceptionAsync(() => SequenceSummaryFileCommit.CommitAsync(temporary, summary, default)
                .WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
            Assert.Equal("old", await File.ReadAllTextAsync(summary));
            Assert.Equal("new", await File.ReadAllTextAsync(temporary));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AMissingTemporaryFileStillFailsWithoutChangingThePreviousSummary()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(SequenceSummaryFileCommitTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var summary = Path.Combine(root, "summary.json");
        try
        {
            await File.WriteAllTextAsync(summary, "old");
            await Assert.ThrowsAsync<FileNotFoundException>(() => SequenceSummaryFileCommit.CommitAsync(
                Path.Combine(root, "missing.tmp"), summary, default));
            Assert.Equal("old", await File.ReadAllTextAsync(summary));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABriefSummaryReaderCannotFailTheRecording(bool allowDelete)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), nameof(SequenceSummaryFileCommitTests), Guid.NewGuid().ToString("N"));
        SequenceRunSession? session = null;
        try
        {
            var step = new SequenceRunStep(1, "41", "Hex", "None", 0, null, "41");
            session = await SequenceRunSession.StartAsync(new(root), new("Concurrent result reading", 1, "MOCK", "Utf8", [step]), Encoding.UTF8, default);
            using var reader = new FileStream(Path.Combine(session.DirectoryPath, "summary.json"), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | (allowDelete ? FileShare.Delete : FileShare.None));
            var begin = session.BeginAsync(step, 1, 1, 1, default);
            // Keep the actual summary file open while the recorder publishes.
            await Task.Delay(80);
            reader.Dispose();
            await begin.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(session.Fault);
            await session.FinishAsync("completed");
            Assert.Equal("completed", (await new SequenceRunStore(root).ReadSummaryAsync(session.RunId))?.State);
        }
        finally
        {
            if (session is not null)
                try { await session.DisposeAsync(); } catch (Exception) when (session.Fault is not null) { }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
