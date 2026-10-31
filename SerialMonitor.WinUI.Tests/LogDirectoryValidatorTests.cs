using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogDirectoryValidatorTests
{
    [Fact]
    public async Task WritableDirectory_IsCreatedAndLeavesNoProbeFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SerialMonitor.DirectoryTests", Guid.NewGuid().ToString("N"));
        try
        {
            var result = await new LogDirectoryValidator().ValidateAsync("  " + directory + "  ", default);

            Assert.True(result.IsValid, result.Error);
            Assert.Equal(directory, result.Directory);
            Assert.True(Directory.Exists(directory));
            Assert.Empty(Directory.GetFileSystemEntries(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid\0path")]
    public async Task InvalidDirectory_ReturnsAVisibleError(string? directory)
    {
        var result = await new LogDirectoryValidator().ValidateAsync(directory, default);

        Assert.False(result.IsValid);
        Assert.Empty(result.Directory);
        Assert.NotEmpty(result.Error);
    }

    [Fact]
    public async Task FilesystemFailure_IsReportedWithoutFaultingTheBackgroundTask()
    {
        var validator = new LogDirectoryValidator(_ => throw new UnauthorizedAccessException("Access denied"));

        var result = await validator.ValidateAsync("folder", default);

        Assert.False(result.IsValid);
        Assert.Contains("Access denied", result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowFilesystem_DoesNotBlockCallerOrAccumulateWorkers(bool cancel)
    {
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        // Cancellation is a separate completion path. A busy test scheduler
        // may resume this test after a short timeout has already completed.
        // The timeout case below still verifies the 200 ms deadline.
        var validator = new LogDirectoryValidator(path =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            try
            {
                release.Wait(TimeSpan.FromSeconds(5));
                return path;
            }
            finally { finished.TrySetResult(); }
        }, timeout: cancel ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(200));

        try
        {
            var check = validator.ValidateAsync("slow folder", cancellation.Token);
            Assert.False(check.IsCompleted);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.WaitAsync(TimeSpan.FromSeconds(3)));
            }
            else
            {
                var timeout = await check.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.False(timeout.IsValid);
                Assert.Contains("timed out", timeout.Error);
            }

            for (var index = 0; index < 20; index++)
            {
                var retry = await validator.ValidateAsync("another folder", default);
                Assert.False(retry.IsValid);
                Assert.Contains("still running", retry.Error);
            }
            Assert.Equal(1, calls);
        }
        finally
        {
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task CancelledBeforeStart_DoesNotTouchTheFilesystem()
    {
        var calls = 0;
        var validator = new LogDirectoryValidator(path => { calls++; return path; });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => validator.ValidateAsync("folder", cancellation.Token));

        Assert.Equal(0, calls);
    }
}
