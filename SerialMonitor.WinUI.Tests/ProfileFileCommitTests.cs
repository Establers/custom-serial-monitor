using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using Xunit.Abstractions;

namespace SerialMonitor.WinUI.Tests;

public sealed class ProfileFileCommitTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(ProfileFileCommitTests), Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "profile.operation.tmp");
    private string Destination => Path.Combine(_root, "profile.json");
    private string Backup => Path.Combine(_root, "profile.operation.bak");
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("{\"state\":\"original\"}");
    private static readonly byte[] Updated = Encoding.UTF8.GetBytes("{\"state\":\"updated\",\"payload\":\"  PING \\t \"}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceHandleDenyingDeleteReproducesNativeFailureAndCommitSucceedsAfterRelease(bool replace)
    {
        await Stage(replace);
        using var handle = HoldWithoutDelete(Source);
        var native = Assert.Throws<IOException>(() =>
        {
            if (replace) File.Replace(Source, Destination, Backup, true);
            else File.Move(Source, Destination);
        });
        AssertSharingViolation(native);

        var committing = ProfileFileCommit.CommitAsync(Source, Destination, Backup, default);
        // Commit has no earlier await: an incomplete task proves a native commit
        // failed and entered the retry delay while the OS handle remains held.
        Assert.False(committing.IsCompleted);
        AssertOriginalDestination(replace);
        Assert.Equal(Updated, await File.ReadAllBytesAsync(Source));
        handle.Dispose();
        await committing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(File.Exists(Source));
        Assert.Equal(Updated, await File.ReadAllBytesAsync(Destination));
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(Destination));
        Assert.Equal("updated", json.RootElement.GetProperty("state").GetString());
        if (replace) Assert.Equal(Original, await File.ReadAllBytesAsync(Backup));
        else Assert.False(File.Exists(Backup));
    }

    [Fact]
    public async Task ExistingDestinationWithoutDeleteSharingStaysWholeUntilAtomicReplacementSucceeds()
    {
        await Stage(true);
        using var handle = HoldWithoutDelete(Destination);
        var native = Assert.Throws<IOException>(() => File.Replace(Source, Destination, Backup, true));
        AssertSharingViolation(native);
        var committing = ProfileFileCommit.CommitAsync(Source, Destination, Backup, default);
        Assert.False(committing.IsCompleted);
        Assert.Equal(Original, await File.ReadAllBytesAsync(Destination));
        Assert.False(File.Exists(Backup));
        handle.Dispose();
        await committing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Updated, await File.ReadAllBytesAsync(Destination));
        Assert.Equal(Original, await File.ReadAllBytesAsync(Backup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationInterruptsAnAlreadyStartedCommitRetryWithoutChangingFiles(bool replace)
    {
        await Stage(replace);
        using var handle = HoldWithoutDelete(Source);
        using var canceled = new CancellationTokenSource();
        var committing = ProfileFileCommit.CommitAsync(Source, Destination, Backup, canceled.Token);
        Assert.False(committing.IsCompleted);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => committing.WaitAsync(TimeSpan.FromSeconds(1)));
        AssertOriginalDestination(replace);
        Assert.Equal(Updated, await File.ReadAllBytesAsync(Source));
        Assert.False(File.Exists(Backup));
    }

    [Fact]
    public async Task PersistentSharingViolationExhaustsFiniteRetriesAndKeepsOriginalErrorAndFiles()
    {
        await Stage(true);
        using var handle = HoldWithoutDelete(Source);
        var timer = Stopwatch.StartNew();
        var committing = ProfileFileCommit.CommitAsync(Source, Destination, Backup, default);
        Assert.False(committing.IsCompleted);
        var error = await Assert.ThrowsAsync<IOException>(() => committing.WaitAsync(TimeSpan.FromSeconds(6)));
        AssertSharingViolation(error);
        output.WriteLine($"Persistent sharing violation surfaced after {timer.Elapsed.TotalMilliseconds:F0} ms.");
        Assert.Equal(Original, await File.ReadAllBytesAsync(Destination));
        Assert.Equal(Updated, await File.ReadAllBytesAsync(Source));
        Assert.False(File.Exists(Backup));
    }

    [Fact]
    public async Task FirstCreationNeverOverwritesAFileThatAppearsDuringTheRetry()
    {
        await Stage(false);
        using var handle = HoldWithoutDelete(Source);
        var committing = ProfileFileCommit.CommitAsync(Source, Destination, Backup, default);
        Assert.False(committing.IsCompleted);
        await File.WriteAllBytesAsync(Destination, Original);
        handle.Dispose();
        var error = await Assert.ThrowsAsync<IOException>(() => committing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain(error.HResult & 0xFFFF, new[] { 32, 33 });
        Assert.Equal(Original, await File.ReadAllBytesAsync(Destination));
        Assert.Equal(Updated, await File.ReadAllBytesAsync(Source));
        Assert.False(File.Exists(Backup));
    }

    [Fact]
    public async Task MissingSourceErrorIsSurfacedWithoutRetrying()
    {
        Directory.CreateDirectory(_root);
        var committing = ProfileFileCommit.CommitAsync(Source, Destination, Backup, default);
        Assert.True(committing.IsCompleted); // No asynchronous retry delay for non-sharing errors.
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => committing);
        Assert.Equal(2, error.HResult & 0xFFFF);
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task ReadOnlyDestinationPermissionErrorIsNotRetriedOrHidden()
    {
        await Stage(true);
        File.SetAttributes(Destination, FileAttributes.ReadOnly);
        try
        {
            var committing = ProfileFileCommit.CommitAsync(Source, Destination, Backup, default);
            Assert.True(committing.IsCompleted);
            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => committing);
            Assert.Equal(5, error.HResult & 0xFFFF);
            Assert.Equal(Original, await File.ReadAllBytesAsync(Destination));
            Assert.Equal(Updated, await File.ReadAllBytesAsync(Source));
        }
        finally { File.SetAttributes(Destination, FileAttributes.Normal); }
    }

    [Fact]
    public async Task MigrationBackupMoveRetriesSharingButNeverOverwritesAnExistingBackup()
    {
        await Stage(false);
        using var handle = HoldWithoutDelete(Source);
        var committing = ProfileFileCommit.MoveNewAsync(Source, Backup, default);
        Assert.False(committing.IsCompleted);
        handle.Dispose();
        await committing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Updated, await File.ReadAllBytesAsync(Backup));
        await File.WriteAllBytesAsync(Source, Original);
        var second = ProfileFileCommit.MoveNewAsync(Source, Backup, default);
        Assert.True(second.IsCompleted);
        await Assert.ThrowsAsync<IOException>(() => second);
        Assert.Equal(Updated, await File.ReadAllBytesAsync(Backup));
        Assert.Equal(Original, await File.ReadAllBytesAsync(Source));
    }

    [Fact]
    public async Task ServiceFailureKeepsCommittedProfileAndExposesThePermissionErrorToUiDiagnostics()
    {
        var service = new ProfileService();
        var profile = await service.LoadAsync(Destination, default);
        var committed = await File.ReadAllBytesAsync(Destination);
        profile.UiSettings.MaxVisibleLogLines = 200_000;
        File.SetAttributes(Destination, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(Destination, profile, default));
            Assert.NotNull(service.LastError);
            Assert.Equal(1, service.SaveErrorCount);
            Assert.Equal(committed, await File.ReadAllBytesAsync(Destination));
            Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
            Assert.Empty(Directory.GetFiles(_root, "*.bak"));
        }
        finally { File.SetAttributes(Destination, FileAttributes.Normal); }
    }

    private async Task Stage(bool replace)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(Source, Updated);
        if (replace) await File.WriteAllBytesAsync(Destination, Original);
    }

    private static FileStream HoldWithoutDelete(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    private void AssertSharingViolation(IOException exception)
    {
        output.WriteLine($"Injected native failure: HResult=0x{exception.HResult:X8}; {exception.Message}");
        Assert.Contains(exception.HResult & 0xFFFF, new[] { 32, 33 });
    }

    private void AssertOriginalDestination(bool exists)
    {
        Assert.Equal(exists, File.Exists(Destination));
        if (exists) Assert.Equal(Original, File.ReadAllBytes(Destination));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
