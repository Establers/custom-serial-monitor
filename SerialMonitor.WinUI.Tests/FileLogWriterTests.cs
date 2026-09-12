using System.Diagnostics;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class FileLogWriterTests
{
    [Fact]
    public async Task FaultedCapture_CountsEveryRejectionWithoutPerLineNotifications()
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter((_, _) => new DiskFullStream());
        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("force disk failure")));
        await writer.StopAsync(CancellationToken.None);
        var notifications = 0;
        writer.StatusChanged += (_, _) => Interlocked.Increment(ref notifications);
        var line = LogLine.System("still receiving");
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < 50_000; index++)
        {
            Assert.False(writer.TryEnqueue(line));
        }
        Assert.Equal(50_000, writer.DroppedLineCount);
        Assert.InRange(notifications, 1, (int)Stopwatch.GetElapsedTime(started).TotalSeconds + 2);
    }

    [Fact]
    public async Task BlockedStorage_DoesNotBlockProducerAndDrainsAfterRelease()
    {
        using var directory = new TemporaryDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var writer = new FileLogWriter((_, _) => new SignaledFlushStream(entered, release.Task));
        try
        {
            await writer.StartAsync(directory.Path, CancellationToken.None);
            var line = LogLine.System("burst");
            for (var index = 0; index < 100; index++)
            {
                Assert.True(writer.TryEnqueue(line));
            }
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // Completion while storage is still blocked proves RX admission is
            // independent of the disk, without a machine-specific latency limit.
            await Task.Run(() =>
            {
                for (var index = 0; index < 5_000; index++)
                {
                    Assert.True(writer.TryEnqueue(line));
                }
            }).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(5_000, writer.PendingRequestCount);
        }
        finally
        {
            release.TrySetResult();
            await writer.StopAsync(CancellationToken.None);
        }
        Assert.Equal(5_100, writer.DurableLineCount);
        Assert.Equal(0, writer.AbandonedLineCount);
    }

    [Fact]
    public async Task DiskFull_FaultsWithoutCreatingRecoveryFiles_AndCanRestart()
    {
        using var directory = new TemporaryDirectory();
        var opens = 0;
        await using var writer = new FileLogWriter((path, mode) => Interlocked.Increment(ref opens) == 1
            ? new DiskFullStream()
            : new FileStream(path, mode, FileAccess.Write, FileShare.Read));
        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("disk full")));
        await writer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(FileLogWriterFaultCategory.StorageFull, writer.LastFault?.Category);
        Assert.False(writer.LastFault!.CanAutoRecover);
        Assert.Equal(1, opens);
        Assert.Equal(1, writer.AbandonedLineCount);
        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("space restored")));
        await writer.StopAsync(CancellationToken.None);
        Assert.Contains("space restored", await File.ReadAllTextAsync(writer.LastLogFilePath!));
    }

    [Fact]
    public async Task StartTokenCancellation_AfterSuccess_DoesNotInterruptCapture()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        await using var writer = new FileLogWriter();
        await writer.StartAsync(directory.Path, cancellation.Token);
        cancellation.Cancel();
        Assert.True(writer.TryEnqueue(LogLine.System("still capturing")));
        await writer.StopAsync(CancellationToken.None);
        Assert.Equal(1, writer.DurableLineCount);
        Assert.Contains("still capturing", await File.ReadAllTextAsync(writer.LastLogFilePath!));
    }

    [Fact]
    public async Task PersistentStorageFailure_StopsSavingAndAccountsForEveryAcceptedLine()
    {
        using var directory = new TemporaryDirectory();
        var opens = 0;
        await using var writer = new FileLogWriter((_, _) =>
        {
            Interlocked.Increment(ref opens);
            return new FailingWriteStream();
        });
        await writer.StartAsync(directory.Path, CancellationToken.None);
        for (var index = 0; index < 500; index++)
        {
            writer.TryEnqueue(LogLine.System($"packet-{index}"));
        }
        await writer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FileLogWriterState.Faulted, writer.State);
        Assert.False(writer.IsRunning);
        Assert.Null(writer.CurrentLogFilePath);
        Assert.Equal(writer.AcceptedLineCount, writer.DurableLineCount + writer.AbandonedLineCount);
        Assert.Equal(0, writer.PendingRequestCount);
        Assert.InRange(opens, 1, 4);
        Assert.False(writer.TryEnqueue(LogLine.System("after failure")));
    }

    [Fact]
    public async Task QueueByteLimit_FaultsInsteadOfSilentlyContinuingWithGaps()
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter(queueByteCapacity: 1024);
        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("before overflow")));
        Assert.False(writer.TryEnqueue(LogLine.System(new string('x', 1024))));
        await writer.StopAsync(CancellationToken.None);
        Assert.Equal(FileLogWriterState.Faulted, writer.State);
        Assert.Equal(1, writer.DroppedLineCount);
        Assert.Equal(1, writer.DurableLineCount);
        Assert.Contains("before overflow", await File.ReadAllTextAsync(writer.LastLogFilePath!));
    }

    [Fact]
    public async Task SustainedCapture_WithRotation_PreservesOrderAndEveryLine()
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter { MaximumFileSizeBytes = 128 * 1024 };
        writer.UpdateLogFileName("capture.log", false);
        await writer.StartAsync(directory.Path, CancellationToken.None);
        var lines = Enumerable.Range(0, 20_000).Select(i => LogLine.System($"packet-{i:D6} 한글" )).ToArray();
        foreach (var line in lines)
        {
            Assert.True(writer.TryEnqueue(line));
        }
        await writer.StopAsync(CancellationToken.None);
        var files = Directory.GetFiles(directory.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Assert.True(files.Length > 1);
        var actual = new List<string>();
        foreach (var file in files)
        {
            actual.AddRange(await File.ReadAllLinesAsync(file));
        }
        Assert.Equal(lines.Select(line => line.Formatted), actual);
        Assert.Equal(lines.Length, writer.DurableLineCount);
        Assert.Equal(0, writer.AbandonedLineCount);
        Assert.Equal(0, writer.DroppedLineCount);
    }

    [Fact]
    public async Task SynchronouslyBlockedAsyncWrite_IsStillBoundedByTimeout()
    {
        using var directory = new TemporaryDirectory();
        using var release = new ManualResetEventSlim();
        await using var writer = new FileLogWriter((_, _) => new BlockingWriteStream(release),
            TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(200));
        try
        {
            await writer.StartAsync(directory.Path, CancellationToken.None);
            Assert.True(writer.TryEnqueue(LogLine.System(new string('x', 100_000))));
            await WaitUntilAsync(() => writer.State == FileLogWriterState.Faulted, TimeSpan.FromSeconds(3));
            await writer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, writer.FileIoTimeoutCount);
        }
        finally
        {
            release.Set();
            await WaitUntilAsync(() => writer.PendingLateOperationCount == 0, TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task StartAsync_WithoutAnyLines_StillCreatesANewTimestampedFile()
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter();

        await writer.StartAsync(directory.Path, CancellationToken.None);
        await writer.StopAsync(CancellationToken.None);

        var logFile = Assert.Single(Directory.GetFiles(directory.Path, "*.log"));
        Assert.Matches(
            @"^\d{4}-\d{2}-\d{2}_\d{6}_serial\.log$",
            Path.GetFileName(logFile));
        Assert.Equal(0, new FileInfo(logFile).Length);
    }

    [Fact]
    public async Task OnOffOnCycle_DrainsAcceptedLines_RejectsOffWrites_AndRestartsCleanly()
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter();
        await writer.StartAsync(directory.Path, CancellationToken.None);

        Assert.True(writer.TryEnqueue(new LogLine(
            DateTimeOffset.Now,
            LogDirection.Rx,
            "READY",
            "READY"u8.ToArray(),
            displayText: "READY",
            contentMode: LogRuleMatchMode.Terminal)));
        Assert.True(writer.TryEnqueue(new LogLine(
            DateTimeOffset.Now,
            LogDirection.Rx,
            "\0?",
            new byte[] { 0x00, 0xFF },
            displayText: "00 FF",
            contentMode: LogRuleMatchMode.Hex)));

        await writer.StopAsync(CancellationToken.None);

        Assert.False(writer.IsRunning);
        Assert.Null(writer.CurrentLogFilePath);
        Assert.NotNull(writer.LastLogFilePath);
        Assert.True(File.Exists(writer.LastLogFilePath));
        Assert.False(writer.TryEnqueue(LogLine.System("after OFF")));
        Assert.Equal(2, writer.WrittenLineCount);

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("after ON again")));
        await writer.StopAsync(CancellationToken.None);
        Assert.Equal(3, writer.WrittenLineCount);

        var logFiles = Directory.GetFiles(directory.Path, "*.log");
        Assert.Equal(2, logFiles.Length);
        Assert.Equal(2, logFiles.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(logFiles, path => Assert.Matches(
            @"^\d{4}-\d{2}-\d{2}_\d{6}_serial(?:_dup\d{3})?\.log$",
            Path.GetFileName(path)));
        var contents = string.Join(
            Environment.NewLine,
            await Task.WhenAll(logFiles.Select(path => File.ReadAllTextAsync(path))));
        var firstRunFile = Assert.Single(logFiles, path => File.ReadAllText(path).Contains("RX < READY", StringComparison.Ordinal));
        var secondRunFile = Assert.Single(logFiles, path => File.ReadAllText(path).Contains("after ON again", StringComparison.Ordinal));
        Assert.NotEqual(firstRunFile, secondRunFile);
        Assert.DoesNotContain("after ON again", await File.ReadAllTextAsync(firstRunFile), StringComparison.Ordinal);
        Assert.DoesNotContain("RX < READY", await File.ReadAllTextAsync(secondRunFile), StringComparison.Ordinal);
        Assert.Contains("RX < READY", contents, StringComparison.Ordinal);
        Assert.Contains("RX < 00 FF", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("after OFF", contents, StringComparison.Ordinal);
        Assert.Contains("after ON again", contents, StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.GetFiles(directory.Path), path =>
            path.EndsWith("_events.log", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExplicitLogFileName_IsUsedExactlyWithoutAutomaticPrefixOrSuffix()
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter();
        writer.UpdateLogFileName("bench (A) #1.txt", requestNewFile: false);
        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.Mark("exact name")));
        await writer.StopAsync(CancellationToken.None);

        var file = Assert.Single(Directory.GetFiles(directory.Path));
        Assert.Equal("bench (A) #1.txt", Path.GetFileName(file));
        Assert.Contains("exact name", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("folder/capture.log")]
    [InlineData("folder\\capture.log")]
    [InlineData("CON.log")]
    [InlineData("capture.")]
    public async Task InvalidExplicitLogFileName_IsRejectedWithoutCreatingAFile(string fileName)
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter();

        Assert.Throws<ArgumentException>(() => writer.UpdateLogFileName(fileName, requestNewFile: false));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task ExplicitLogFileName_RefusesToReuseAnExistingFile()
    {
        using var directory = new TemporaryDirectory();
        var existingPath = Path.Combine(directory.Path, "capture.log");
        await File.WriteAllTextAsync(existingPath, "keep me");
        await using var writer = new FileLogWriter();
        writer.UpdateLogFileName("capture.log", requestNewFile: false);

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            writer.StartAsync(directory.Path, CancellationToken.None));

        Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("keep me", await File.ReadAllTextAsync(existingPath));
        Assert.False(writer.IsRunning);
    }

    [Fact]
    public async Task ExplicitLogFileName_DoesNotSplitWhenLogDateChanges()
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter();
        writer.UpdateLogFileName("long-run.log", requestNewFile: false);
        await writer.StartAsync(directory.Path, CancellationToken.None);
        var firstDate = new DateTimeOffset(2026, 7, 14, 23, 59, 59, TimeSpan.Zero);
        var secondDate = firstDate.AddDays(1);

        Assert.True(writer.TryEnqueue(new LogLine(firstDate, LogDirection.System, "day one")));
        Assert.True(writer.TryEnqueue(new LogLine(secondDate, LogDirection.System, "day two")));
        await writer.StopAsync(CancellationToken.None);

        var file = Assert.Single(Directory.GetFiles(directory.Path));
        Assert.Equal("long-run.log", Path.GetFileName(file));
        var contents = await File.ReadAllTextAsync(file);
        Assert.Contains("day one", contents, StringComparison.Ordinal);
        Assert.Contains("day two", contents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitLogFileName_SizeRotationAvoidsExistingSegmentNames()
    {
        using var directory = new TemporaryDirectory();
        var existingRotationPath = Path.Combine(directory.Path, "capture_001.log");
        await File.WriteAllTextAsync(existingRotationPath, "keep existing segment");
        await using var writer = new FileLogWriter
        {
            MaximumFileSizeBytes = 1
        };
        writer.UpdateLogFileName("capture.log", requestNewFile: false);
        await writer.StartAsync(directory.Path, CancellationToken.None);

        Assert.True(writer.TryEnqueue(LogLine.System("first")));
        Assert.True(writer.TryEnqueue(LogLine.System("second")));
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal("keep existing segment", await File.ReadAllTextAsync(existingRotationPath));
        Assert.True(File.Exists(Path.Combine(directory.Path, "capture.log")));
        var duplicateRotationPath = Path.Combine(directory.Path, "capture_001_dup001.log");
        Assert.True(File.Exists(duplicateRotationPath));
        Assert.Contains("second", await File.ReadAllTextAsync(duplicateRotationPath), StringComparison.Ordinal);
        Assert.Equal(2, writer.WrittenLineCount);
    }

    [Fact]
    public async Task StopAsync_RetainsLastCompletedLogPath()
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter();
        await writer.StartAsync(directory.Path, CancellationToken.None);
        var activePath = writer.CurrentLogFilePath;

        await writer.StopAsync(CancellationToken.None);

        Assert.NotNull(activePath);
        Assert.Null(writer.CurrentLogFilePath);
        Assert.Equal(activePath, writer.LastLogFilePath);
        Assert.True(File.Exists(writer.LastLogFilePath));
    }

    [Fact]
    public async Task DifferentLogDates_StayInTheSameAutomaticFile()
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter();
        await writer.StartAsync(directory.Path, CancellationToken.None);
        var firstDate = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var secondDate = firstDate.AddDays(1);

        Assert.True(writer.TryEnqueue(new LogLine(firstDate, LogDirection.System, "day one")));
        Assert.True(writer.TryEnqueue(new LogLine(secondDate, LogDirection.System, "day two")));
        await writer.StopAsync(CancellationToken.None);

        var file = Assert.Single(Directory.GetFiles(directory.Path, "*.log"));
        var contents = await File.ReadAllTextAsync(file);
        Assert.Contains("day one", contents, StringComparison.Ordinal);
        Assert.Contains("day two", contents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeBasedFlush_CommitsOneLineWithoutAdditionalInput()
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter();
        await writer.StartAsync(directory.Path, CancellationToken.None);

        Assert.True(writer.TryEnqueue(LogLine.System("deadline")));
        await WaitUntilAsync(() => writer.DurableLineCount == 1, TimeSpan.FromSeconds(4));

        var activePath = writer.CurrentLogFilePath!;
        await writer.StopAsync(CancellationToken.None);
        var contents = await File.ReadAllTextAsync(activePath);
        Assert.Contains("deadline", contents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteFailure_ReplaysAcceptedBatchInRecoverySegment()
    {
        using var directory = new TemporaryDirectory();
        var streamNumber = 0;
        await using var writer = new FileLogWriter((path, mode) =>
            Interlocked.Increment(ref streamNumber) == 1
                ? new FailingWriteStream()
                : new FileStream(path, mode, FileAccess.Write, FileShare.Read));

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("recover me")));
        await WaitUntilAsync(() => writer.RecoveryCount == 1, TimeSpan.FromSeconds(4));
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(1, writer.AcceptedLineCount);
        Assert.Equal(1, writer.DurableLineCount);
        Assert.Equal(1, writer.UncertainLineCount);
        Assert.Equal(0, writer.DroppedLineCount);
        Assert.Contains(
            "recover me",
            string.Join(Environment.NewLine, await Task.WhenAll(
                Directory.GetFiles(directory.Path).Select(path => File.ReadAllTextAsync(path)))),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamingRotation_FlushFailureReplaysBatchBeforeChangingFiles()
    {
        using var directory = new TemporaryDirectory();
        var streamNumber = 0;
        await using var writer = new FileLogWriter((path, mode) =>
        {
            var stream = new FileStream(path, mode, FileAccess.Write, FileShare.Read);
            return new FlushOnceFailsStream(stream, failFlush: Interlocked.Increment(ref streamNumber) == 1);
        });
        writer.UpdateLogFileName("first.log", requestNewFile: false);

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("rotate safely")));
        writer.UpdateLogFileName("second.log", requestNewFile: true);

        await WaitUntilAsync(() => writer.RecoveryCount == 1, TimeSpan.FromSeconds(4));
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(1, writer.DurableLineCount);
        Assert.Contains(
            "rotate safely",
            string.Join(Environment.NewLine, await Task.WhenAll(
                Directory.GetFiles(directory.Path).Select(path => File.ReadAllTextAsync(path)))),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task HangingOpen_FaultsWithinIoTimeoutAndTracksLateCreation()
    {
        using var directory = new TemporaryDirectory();
        var openRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var writer = new FileLogWriter(
            (_, _) =>
            {
                SpinWait.SpinUntil(() => openRelease.Task.IsCompleted);
                return new MemoryStream();
            },
            fileIoTimeout: TimeSpan.FromMilliseconds(100),
            shutdownTimeout: TimeSpan.FromMilliseconds(200));

        try
        {
            var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
                writer.StartAsync(directory.Path, CancellationToken.None));

            Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, writer.FileIoTimeoutCount);
            Assert.Equal(1, writer.PendingLateOperationCount);
        }
        finally
        {
            openRelease.TrySetResult();
            await WaitUntilAsync(() => writer.PendingLateOperationCount == 0, TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task HangingWrite_FaultsWithinIoTimeoutAndQuarantinesLateStream()
    {
        using var directory = new TemporaryDirectory();
        var writeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var writer = new FileLogWriter(
            (_, _) => new HangingWriteStream(writeRelease.Task),
            fileIoTimeout: TimeSpan.FromMilliseconds(100),
            shutdownTimeout: TimeSpan.FromMilliseconds(200));

        try
        {
            await writer.StartAsync(directory.Path, CancellationToken.None);
            Assert.True(writer.TryEnqueue(LogLine.System(new string('x', 100_000))));
            await WaitUntilAsync(() => writer.State == FileLogWriterState.Faulted, TimeSpan.FromSeconds(3));

            Assert.Equal(1, writer.FileIoTimeoutCount);
            Assert.Equal(1, writer.PendingLateOperationCount);
            Assert.Contains("timed out", writer.LastFault?.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            writeRelease.TrySetResult();
            await writer.StopAsync(CancellationToken.None);
            await WaitUntilAsync(() => writer.PendingLateOperationCount == 0, TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task HangingFlush_FaultsWithinIoTimeoutWithoutBlockingStop()
    {
        using var directory = new TemporaryDirectory();
        var flushRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var writer = new FileLogWriter(
            (_, _) => new HangingFlushStream(flushRelease.Task),
            fileIoTimeout: TimeSpan.FromMilliseconds(100),
            shutdownTimeout: TimeSpan.FromMilliseconds(200));

        try
        {
            await writer.StartAsync(directory.Path, CancellationToken.None);
            for (var index = 0; index < 100; index++)
            {
                Assert.True(writer.TryEnqueue(LogLine.System($"flush-{index}")));
            }

            await WaitUntilAsync(() => writer.State == FileLogWriterState.Faulted, TimeSpan.FromSeconds(3));
            Assert.Equal(1, writer.FileIoTimeoutCount);
            Assert.Equal(1, writer.PendingLateOperationCount);
        }
        finally
        {
            flushRelease.TrySetResult();
            await writer.StopAsync(CancellationToken.None);
            await WaitUntilAsync(() => writer.PendingLateOperationCount == 0, TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task HangingFlush_StopAsyncCancelsWorkerWithinShutdownTimeout()
    {
        using var directory = new TemporaryDirectory();
        var flushRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var writer = new FileLogWriter(
            (_, _) => new HangingFlushStream(flushRelease.Task),
            fileIoTimeout: TimeSpan.FromSeconds(30),
            shutdownTimeout: TimeSpan.FromMilliseconds(200));

        try
        {
            await writer.StartAsync(directory.Path, CancellationToken.None);
            for (var index = 0; index < 100; index++)
            {
                Assert.True(writer.TryEnqueue(LogLine.System($"stop-{index}")));
            }

            await WaitUntilAsync(() => writer.PendingLateOperationCount == 0, TimeSpan.FromMilliseconds(100));
            var startedAt = Stopwatch.GetTimestamp();
            await writer.StopAsync(CancellationToken.None);
            var elapsed = Stopwatch.GetElapsedTime(startedAt);

            Assert.True(elapsed < TimeSpan.FromSeconds(2), $"Stop took {elapsed}.");
            Assert.Equal(FileLogWriterState.Faulted, writer.State);
            Assert.True(writer.AbandonedLineCount > 0);
        }
        finally
        {
            flushRelease.TrySetResult();
            await WaitUntilAsync(() => writer.PendingLateOperationCount == 0, TimeSpan.FromSeconds(2));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), "Condition was not met before the timeout.");
    }

    private sealed class BlockingWriteStream(ManualResetEventSlim release) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            release.Wait();
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class DiskFullStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Disk full", unchecked((int)0x80070070)));
    }

    private sealed class SignaledFlushStream(TaskCompletionSource entered, Task release) : MemoryStream
    {
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            return release;
        }
    }

    private sealed class FailingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("simulated write failure");
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("simulated write failure"));
    }

    private sealed class HangingWriteStream : Stream
    {
        private readonly Task _release;

        public HangingWriteStream(Task release)
        {
            _release = release;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(_release);
    }

    private sealed class HangingFlushStream : Stream
    {
        private readonly Task _release;

        public HangingFlushStream(Task release)
        {
            _release = release;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => _release;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class FlushOnceFailsStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _failFlush;
        private int _flushCount;

        public FlushOnceFailsStream(Stream inner, bool failFlush)
        {
            _inner = inner;
            _failFlush = failFlush;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush()
        {
            if (_failFlush && Interlocked.Increment(ref _flushCount) == 1)
            {
                throw new IOException("simulated flush failure");
            }

            _inner.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_failFlush && Interlocked.Increment(ref _flushCount) == 1)
            {
                return Task.FromException(new IOException("simulated flush failure"));
            }

            return _inner.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override ValueTask DisposeAsync() => _inner.DisposeAsync();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(buffer, cancellationToken);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"SerialMonitorTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
