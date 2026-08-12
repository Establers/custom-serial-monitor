using System.Collections.Concurrent;
using System.Diagnostics;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class FileLogAutoRestartCoordinatorTests
{
    [Fact]
    public async Task RetryableWriterFault_TransitionsThroughFaultedToNewRunningSegment()
    {
        using var directory = new TemporaryDirectory();
        var openCount = 0;
        await using var writer = new FileLogWriter(
            (path, mode) => Interlocked.Increment(ref openCount) == 1
                ? new FailingWriteFileStream(CreateFileStream(path, mode))
                : CreateFileStream(path, mode),
            ioTimeout: TimeSpan.FromMilliseconds(50),
            flushLineInterval: 1,
            recoveryRetryInterval: TimeSpan.FromMilliseconds(1),
            maximumRecoveryAttempts: 1,
            recoveryTimeBudget: TimeSpan.FromMilliseconds(100));
        writer.UpdateLogFileName("capture.log", requestNewFile: false);
        var requested = 1;
        FileLogAutoRestartCoordinator? coordinator = null;
        await using (coordinator = new FileLogAutoRestartCoordinator(
            () => Volatile.Read(ref requested) != 0 &&
                writer.State == FileLogWriterState.Faulted &&
                writer.CanAutoRecover,
            async token =>
            {
                await writer.StartAsync(directory.Path, token);
                return writer.State == FileLogWriterState.Running;
            },
            initialDelay: TimeSpan.FromMilliseconds(10),
            maximumDelay: TimeSpan.FromMilliseconds(40),
            maximumConsecutiveAttempts: 4))
        {
            writer.StatusChanged += (_, _) => coordinator.RequestRetry();
            await writer.StartAsync(directory.Path, CancellationToken.None);
            Assert.True(writer.TryEnqueue(LogLine.System("forces retryable fault")));
            await WaitUntilAsync(() => writer.State == FileLogWriterState.Running && writer.StartCount == 2, TimeSpan.FromSeconds(2));

            Assert.Equal(1, coordinator.LoopStartCount);
            Assert.Equal(1, writer.DroppedLineCount);
            Assert.True(writer.TryEnqueue(LogLine.System("durable after automatic restart")));
            await WaitUntilAsync(() => writer.WrittenLineCount == 1, TimeSpan.FromSeconds(2));
            Volatile.Write(ref requested, 0);
            await coordinator.CancelAsync(resetAttempts: true);
            await writer.StopAsync(CancellationToken.None);
        }

        Assert.Equal(
            ["capture.log", "capture_001.log"],
            Directory.GetFiles(directory.Path, "*.log")
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public async Task LateExplicitCreateNewFault_AutoRestartRunsNumberedSegmentWithoutManualToggle()
    {
        using var directory = new TemporaryDirectory();
        using var releaseLateOpen = new ManualResetEventSlim(initialState: false);
        var lateOpenStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryDelayStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetryDelay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateDisposeCount = 0;
        var openCount = 0;
        await using var writer = new FileLogWriter(
            (path, mode) =>
            {
                if (Interlocked.Increment(ref openCount) == 1)
                {
                    var stream = new CountingDisposeFileStream(
                        CreateFileStream(path, mode),
                        () => Interlocked.Increment(ref lateDisposeCount));
                    lateOpenStarted.TrySetResult(true);
                    releaseLateOpen.Wait();
                    return stream;
                }

                return CreateFileStream(path, mode);
            },
            ioTimeout: TimeSpan.FromMilliseconds(30),
            flushLineInterval: 1,
            recoveryRetryInterval: TimeSpan.FromMilliseconds(1),
            maximumRecoveryAttempts: 1,
            recoveryTimeBudget: TimeSpan.FromMilliseconds(80),
            deleteAbandonedExplicitFiles: true);
        writer.UpdateLogFileName("capture.log", requestNewFile: false);
        var requested = 1;
        await using var coordinator = new FileLogAutoRestartCoordinator(
            () => Volatile.Read(ref requested) != 0 &&
                writer.State == FileLogWriterState.Faulted &&
                writer.CanAutoRecover,
            async token =>
            {
                await writer.StartAsync(directory.Path, token);
                return writer.State == FileLogWriterState.Running;
            },
            initialDelay: TimeSpan.FromMilliseconds(10),
            maximumDelay: TimeSpan.FromMilliseconds(40),
            maximumConsecutiveAttempts: 4,
            delay: (_, token) =>
            {
                retryDelayStarted.TrySetResult(true);
                return releaseRetryDelay.Task.WaitAsync(token);
            });
        EventHandler statusHandler = (_, _) => coordinator.RequestRetry();
        writer.StatusChanged += statusHandler;
        var abandonedPath = Path.Combine(directory.Path, "capture.log");
        var activePath = Path.Combine(directory.Path, "capture_001.log");

        try
        {
            var failedStart = writer.StartAsync(directory.Path, CancellationToken.None);
            await lateOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<IOException>(() => failedStart.WaitAsync(TimeSpan.FromSeconds(2)));
            await retryDelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(FileLogWriterState.Faulted, writer.State);
            Assert.True(writer.CanAutoRecover);
            Assert.True(File.Exists(abandonedPath));
            Assert.Equal(0, Volatile.Read(ref lateDisposeCount));

            releaseRetryDelay.TrySetResult(true);
            await WaitUntilAsync(
                () => writer.State == FileLogWriterState.Running && writer.StartCount == 2,
                TimeSpan.FromSeconds(2));

            Assert.Equal(activePath, writer.CurrentLogFilePath);
            Assert.Equal(1, coordinator.LoopStartCount);
            Assert.True(writer.TryEnqueue(LogLine.System("durable after late-open automatic restart")));
            await WaitUntilAsync(() => writer.WrittenLineCount == 1, TimeSpan.FromSeconds(2));

            releaseLateOpen.Set();
            await WaitUntilAsync(() => writer.DetachedCleanupCount == 0, TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => !File.Exists(abandonedPath), TimeSpan.FromSeconds(2));
            Assert.Equal(1, Volatile.Read(ref lateDisposeCount));
            Assert.True(File.Exists(activePath));

            Volatile.Write(ref requested, 0);
            await coordinator.CancelAsync(resetAttempts: true);
            await writer.StopAsync(CancellationToken.None);

            Assert.Equal([activePath], Directory.GetFiles(directory.Path));
            Assert.Contains(
                "durable after late-open automatic restart",
                await File.ReadAllTextAsync(activePath),
                StringComparison.Ordinal);
        }
        finally
        {
            Volatile.Write(ref requested, 0);
            releaseRetryDelay.TrySetResult(true);
            releaseLateOpen.Set();
            writer.StatusChanged -= statusHandler;
            await coordinator.CancelAsync(resetAttempts: true);
        }
    }

    [Fact]
    public async Task RetryableFault_DuplicateStatusSignalsUseOneLoopAndRecover()
    {
        var shouldRetry = 1;
        var restartCount = 0;
        var delayEntered = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new FileLogAutoRestartCoordinator(
            () => Volatile.Read(ref shouldRetry) != 0,
            _ =>
            {
                Interlocked.Increment(ref restartCount);
                Volatile.Write(ref shouldRetry, 0);
                return Task.FromResult(true);
            },
            initialDelay: TimeSpan.FromMilliseconds(10),
            maximumDelay: TimeSpan.FromMilliseconds(40),
            maximumConsecutiveAttempts: 4,
            delay: (value, token) =>
            {
                delayEntered.TrySetResult(value);
                return releaseDelay.Task.WaitAsync(token);
            });

        Assert.True(coordinator.RequestRetry());
        for (var index = 0; index < 20; index++)
        {
            Assert.False(coordinator.RequestRetry());
        }

        Assert.Equal(TimeSpan.FromMilliseconds(10), await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, coordinator.LoopStartCount);
        releaseDelay.TrySetResult(true);
        await WaitUntilAsync(() => !coordinator.IsRetrying, TimeSpan.FromSeconds(2));

        Assert.Equal(1, restartCount);
        Assert.Equal(1, coordinator.AttemptCount);
        Assert.False(coordinator.IsExhausted);
    }

    [Fact]
    public async Task FaultDuringSuccessfulRestart_IsCoalescedWithoutConcurrentLoops()
    {
        var shouldRetry = 1;
        var restartCount = 0;
        var activeRestartCount = 0;
        var maximumActiveRestartCount = 0;
        FileLogAutoRestartCoordinator? coordinator = null;
        await using (coordinator = new FileLogAutoRestartCoordinator(
            () => Volatile.Read(ref shouldRetry) != 0,
            async _ =>
            {
                var active = Interlocked.Increment(ref activeRestartCount);
                UpdateMaximum(ref maximumActiveRestartCount, active);
                try
                {
                    var attempt = Interlocked.Increment(ref restartCount);
                    await Task.Yield();
                    if (attempt == 1)
                    {
                        Assert.False(coordinator!.RequestRetry());
                    }
                    else
                    {
                        Volatile.Write(ref shouldRetry, 0);
                    }

                    return true;
                }
                finally
                {
                    Interlocked.Decrement(ref activeRestartCount);
                }
            },
            initialDelay: TimeSpan.FromMilliseconds(1),
            maximumDelay: TimeSpan.FromMilliseconds(2),
            maximumConsecutiveAttempts: 4))
        {
            Assert.True(coordinator.RequestRetry());
            await WaitUntilAsync(
                () => Volatile.Read(ref shouldRetry) == 0 && !coordinator.IsRetrying,
                TimeSpan.FromSeconds(2));

            Assert.Equal(2, restartCount);
            Assert.Equal(2, coordinator.LoopStartCount);
            Assert.Equal(1, maximumActiveRestartCount);
        }
    }

    [Theory]
    [InlineData("manual OFF")]
    [InlineData("shutdown")]
    public async Task CancelBeforeBackoffExpires_PreventsRestart(string reason)
    {
        _ = reason;
        var restartCount = 0;
        var delayEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new FileLogAutoRestartCoordinator(
            () => true,
            _ =>
            {
                Interlocked.Increment(ref restartCount);
                return Task.FromResult(true);
            },
            delay: async (_, token) =>
            {
                delayEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });

        Assert.True(coordinator.RequestRetry());
        await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(coordinator.RequestRetry());
        await coordinator.CancelAsync(resetAttempts: true);

        Assert.False(coordinator.IsRetrying);
        Assert.False(coordinator.IsExhausted);
        Assert.Equal(0, coordinator.AttemptCount);
        Assert.Equal(0, restartCount);
    }

    [Fact]
    public async Task DeterministicFault_DoesNotStartRetryLoop()
    {
        var restartCount = 0;
        await using var coordinator = new FileLogAutoRestartCoordinator(
            () => false,
            _ =>
            {
                Interlocked.Increment(ref restartCount);
                return Task.FromResult(true);
            });

        Assert.False(coordinator.RequestRetry());
        Assert.Equal(0, coordinator.LoopStartCount);
        Assert.Equal(0, restartCount);
    }

    [Fact]
    public async Task ConsecutiveRetryBackoff_IsCappedAndEventuallyExhausted()
    {
        var delays = new ConcurrentQueue<TimeSpan>();
        var restartCount = 0;
        await using var coordinator = new FileLogAutoRestartCoordinator(
            () => true,
            _ =>
            {
                Interlocked.Increment(ref restartCount);
                return Task.FromResult(false);
            },
            initialDelay: TimeSpan.FromMilliseconds(10),
            maximumDelay: TimeSpan.FromMilliseconds(25),
            maximumConsecutiveAttempts: 5,
            delay: (value, _) =>
            {
                delays.Enqueue(value);
                return Task.CompletedTask;
            });

        Assert.True(coordinator.RequestRetry());
        await WaitUntilAsync(() => coordinator.IsExhausted, TimeSpan.FromSeconds(2));

        Assert.Equal(5, restartCount);
        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(25),
                TimeSpan.FromMilliseconds(25),
                TimeSpan.FromMilliseconds(25)
            ],
            delays.ToArray());
        Assert.False(coordinator.IsRetrying);
    }

    [Fact]
    public void FaultPresentation_NeverHidesFaultAsWaiting()
    {
        Assert.Equal(
            "Log Save: FAULTED / retrying",
            FileLogStatusPresentation.CreateMainStatus(
                requested: true,
                FileLogWriterState.Faulted,
                isRetrying: true,
                ingressActive: true));
        Assert.Equal(
            "File FAULTED / retrying",
            FileLogStatusPresentation.CreateCompactStatus(
                requested: true,
                FileLogWriterState.Faulted,
                isRetrying: true,
                currentPath: null,
                ingressActive: true));
        Assert.DoesNotContain(
            "waiting",
            FileLogStatusPresentation.CreateCompactStatus(
                requested: true,
                FileLogWriterState.Faulted,
                isRetrying: false,
                currentPath: null,
                ingressActive: false),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequestedButInactivePresentation_DistinguishesArmedAndStarting()
    {
        Assert.Equal(
            "Log Save: ON / armed",
            FileLogStatusPresentation.CreateMainStatus(
                requested: true,
                FileLogWriterState.Stopped,
                isRetrying: false,
                ingressActive: false));
        Assert.Equal(
            "File ON armed",
            FileLogStatusPresentation.CreateCompactStatus(
                requested: true,
                FileLogWriterState.Stopped,
                isRetrying: false,
                currentPath: null,
                ingressActive: false));
        Assert.Equal(
            "Log Save: ON / starting",
            FileLogStatusPresentation.CreateMainStatus(
                requested: true,
                FileLogWriterState.Starting,
                isRetrying: false,
                ingressActive: false));
        Assert.Equal(
            "Log Save: ON / armed",
            FileLogStatusPresentation.CreateMainStatus(
                requested: true,
                FileLogWriterState.Running,
                isRetrying: false,
                ingressActive: false));
        Assert.Equal(
            "Log Save: ON",
            FileLogStatusPresentation.CreateMainStatus(
                requested: true,
                FileLogWriterState.Running,
                isRetrying: false,
                ingressActive: true));
    }

    [Theory]
    [InlineData(true, true, false, FileLogWriterState.Faulted, true, true)]
    [InlineData(false, true, false, FileLogWriterState.Faulted, true, false)]
    [InlineData(true, false, false, FileLogWriterState.Faulted, true, false)]
    [InlineData(true, true, true, FileLogWriterState.Faulted, true, false)]
    [InlineData(true, true, false, FileLogWriterState.Running, true, false)]
    [InlineData(true, true, false, FileLogWriterState.Faulted, false, false)]
    public void RetryPolicy_RequiresRequestedValidSessionRetryableFault(
        bool requested,
        bool validSession,
        bool shutdown,
        FileLogWriterState state,
        bool canAutoRecover,
        bool expected)
    {
        Assert.Equal(
            expected,
            FileLogAutoRestartPolicy.ShouldRetry(
                requested,
                validSession,
                shutdown,
                state,
                canAutoRecover));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException("Condition was not satisfied before the test timeout.");
            }

            await Task.Delay(5);
        }
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        var observed = Volatile.Read(ref target);
        while (observed < value)
        {
            var original = Interlocked.CompareExchange(ref target, value, observed);
            if (original == observed)
            {
                return;
            }

            observed = original;
        }
    }

    private static Stream CreateFileStream(string path, FileMode mode) => new FileStream(
        path,
        mode,
        FileAccess.Write,
        FileShare.Read,
        bufferSize: 4096,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private sealed class FailingWriteFileStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("Injected retryable write failure.");

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Injected retryable write failure."));

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class CountingDisposeFileStream(Stream inner, Action onDispose) : Stream
    {
        private int _disposed;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDispose();
                await inner.DisposeAsync();
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "SerialMonitor.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
