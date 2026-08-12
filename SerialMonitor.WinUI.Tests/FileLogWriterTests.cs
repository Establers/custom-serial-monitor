using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class FileLogWriterTests
{
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

        var exception = await Assert.ThrowsAnyAsync<IOException>(() =>
            writer.StartAsync(directory.Path, CancellationToken.None));

        Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("keep me", await File.ReadAllTextAsync(existingPath));
        Assert.False(writer.IsRunning);
        Assert.Equal(FileLogWriterState.Faulted, writer.State);
        Assert.Equal(FileLogWriterFaultCategory.DeterministicConfiguration, writer.LastFault?.Category);
        Assert.False(writer.CanAutoRecover);
        Assert.Equal([existingPath], Directory.GetFiles(directory.Path));
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
        await using var writer = new FileLogWriter(
            CreateTestFileStream,
            ioTimeout: TimeSpan.FromSeconds(1),
            flushLineInterval: 100,
            flushTimeInterval: TimeSpan.FromMilliseconds(50));

        await writer.StartAsync(directory.Path, CancellationToken.None);
        var logPath = Assert.IsType<string>(writer.CurrentLogFilePath);
        Assert.True(writer.TryEnqueue(LogLine.System("timer committed line")));

        await WaitUntilAsync(() => writer.WrittenLineCount == 1, TimeSpan.FromSeconds(2));

        Assert.Contains(
            "timer committed line",
            await ReadAllTextWhileOpenAsync(logPath),
            StringComparison.Ordinal);
        Assert.Equal(1, writer.BatchDeadlineCreationCount);
        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ContinuousBacklog_DoesNotCreateWaitOperationsPerLine()
    {
        using var directory = new TemporaryDirectory();
        var stream = new FirstWriteGateStream();
        await using var writer = new FileLogWriter(
            (_, _) => stream,
            ioTimeout: TimeSpan.FromSeconds(5),
            flushLineInterval: 1,
            flushTimeInterval: TimeSpan.FromSeconds(30));

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("gate first write")));
        await stream.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            const int backlogLineCount = 5_000;
            for (var index = 0; index < backlogLineCount; index++)
            {
                Assert.True(writer.TryEnqueue(LogLine.System($"backlog {index}")));
            }

            stream.ReleaseFirstWrite();
            await WaitUntilAsync(
                () => writer.WrittenLineCount == backlogLineCount + 1,
                TimeSpan.FromSeconds(5));
            await writer.StopAsync(CancellationToken.None);

            Assert.InRange(writer.WaitOperationCount, 1, 3);
            Assert.Equal(0, writer.BatchDeadlineCreationCount);
        }
        finally
        {
            stream.ReleaseFirstWrite();
        }
    }

    [Fact]
    public async Task TrickleInputWithinOneBatch_ReusesSingleDeadlineWait()
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter(
            (_, _) => new WriteOnlyTestStreamImpl(),
            ioTimeout: TimeSpan.FromSeconds(1),
            flushLineInterval: 100,
            flushTimeInterval: TimeSpan.FromMilliseconds(200));

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("trickle one")));
        await WaitUntilAsync(() => writer.BatchDeadlineCreationCount == 1, TimeSpan.FromSeconds(2));

        Assert.True(writer.TryEnqueue(LogLine.System("trickle two")));
        await WaitUntilAsync(() => writer.PendingRequestCount == 0, TimeSpan.FromSeconds(2));
        await Task.Delay(20);

        Assert.Equal(1, writer.BatchDeadlineCreationCount);
        await WaitUntilAsync(() => writer.WrittenLineCount == 2, TimeSpan.FromSeconds(2));
        Assert.Equal(1, writer.BatchDeadlineCreationCount);
        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ForcedStop_IsolatesBlockedOpenAndDisposesLateStreamExactlyOnce()
    {
        using var directory = new TemporaryDirectory();
        using var releaseBlockedOpen = new ManualResetEventSlim(initialState: false);
        var blockedOpenStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateStream = new CountingDisposeStream();
        var streamOpenCount = 0;
        await using var writer = new FileLogWriter(
            (path, mode) =>
            {
                var attempt = Interlocked.Increment(ref streamOpenCount);
                if (attempt == 2)
                {
                    blockedOpenStarted.TrySetResult(true);
                    releaseBlockedOpen.Wait();
                    return lateStream;
                }

                return CreateTestFileStream(path, mode);
            },
            ioTimeout: TimeSpan.FromMilliseconds(50),
            flushLineInterval: 1,
            shutdownDrainTimeout: TimeSpan.FromMilliseconds(50),
            forcedShutdownTimeout: TimeSpan.FromMilliseconds(50),
            recoveryRetryInterval: TimeSpan.FromMilliseconds(10))
        {
            MaximumFileSizeBytes = 1
        };

        try
        {
            await writer.StartAsync(directory.Path, CancellationToken.None);
            Assert.True(writer.TryEnqueue(LogLine.System("first segment")));
            await WaitUntilAsync(() => writer.WrittenLineCount == 1, TimeSpan.FromSeconds(2));
            Assert.True(writer.TryEnqueue(LogLine.System("blocked rotation line")));
            await blockedOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await writer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(writer.IsRunning);
            Assert.Equal(1, writer.StopCount);
            Assert.Equal(0, lateStream.DisposeCallCount);
            Assert.Equal(1, writer.DetachedCleanupCount);
            Assert.Equal(1, writer.DroppedLineCount);
            Assert.Equal(2, Volatile.Read(ref streamOpenCount));
            Assert.Equal(1, writer.StartCount);

            releaseBlockedOpen.Set();
            await WaitUntilAsync(
                () => writer.DetachedCleanupCount == 0,
                TimeSpan.FromSeconds(2));
            Assert.Equal(1, lateStream.DisposeCallCount);

            await writer.StartAsync(directory.Path, CancellationToken.None);
            Assert.True(writer.IsRunning);
            Assert.NotNull(writer.CurrentLogFilePath);
            Assert.Equal(2, writer.StartCount);
            Assert.Equal(1, writer.StopCount);

            await writer.StopAsync(CancellationToken.None);
            Assert.Equal(2, writer.StopCount);
        }
        finally
        {
            releaseBlockedOpen.Set();
        }
    }

    [Fact]
    public void AcceptedLineTracker_RollbackAfterAbandonDoesNotDoubleCountLine()
    {
        var tracker = new FileLogWriter.AcceptedLineTracker();
        Assert.True(tracker.TryAccept());

        var abandoned = tracker.Abandon();
        var rolledBack = tracker.RollBackAcceptance();
        var resultingDropCount = abandoned + (rolledBack ? 1 : 0);

        Assert.False(rolledBack);
        Assert.Equal(1, resultingDropCount);
    }

    [Fact]
    public async Task CancellationDuringHangingClose_CallsDisposeAsyncOnlyOnce()
    {
        using var directory = new TemporaryDirectory();
        var stream = new CountingHangingDisposeStream();
        await using var writer = new FileLogWriter(
            (_, _) => stream,
            ioTimeout: TimeSpan.FromSeconds(5),
            flushLineInterval: 1,
            shutdownDrainTimeout: TimeSpan.FromMilliseconds(50),
            forcedShutdownTimeout: TimeSpan.FromMilliseconds(500));

        try
        {
            await writer.StartAsync(directory.Path, CancellationToken.None);
            Assert.True(writer.TryEnqueue(LogLine.System("durable before hanging close")));
            await WaitUntilAsync(() => writer.WrittenLineCount == 1, TimeSpan.FromSeconds(2));

            await writer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, stream.DisposeCallCount);
        }
        finally
        {
            stream.ReleaseDispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForcedStop_InFlightIoIgnoringCancellation_DisposesOnlyAfterOperationCompletes(
        bool blockFlush)
    {
        using var directory = new TemporaryDirectory();
        var blockedStream = new ControlledIgnoringCancellationIoStream(blockFlush);
        var streamOpenCount = 0;
        await using var writer = new FileLogWriter(
            (path, mode) => Interlocked.Increment(ref streamOpenCount) == 1
                ? blockedStream
                : CreateTestFileStream(path, mode),
            ioTimeout: TimeSpan.FromSeconds(30),
            flushLineInterval: 1,
            shutdownDrainTimeout: TimeSpan.FromMilliseconds(50),
            forcedShutdownTimeout: TimeSpan.FromMilliseconds(500),
            recoveryRetryInterval: TimeSpan.FromMilliseconds(10));

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System($"blocked {(blockFlush ? "flush" : "write")}")));
        await blockedStream.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            await writer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(0, blockedStream.DisposeCallCount);
            Assert.Equal(1, writer.DetachedCleanupCount);
            Assert.Equal(0, writer.WrittenLineCount);
            Assert.Equal(1, writer.DroppedLineCount);
            Assert.Equal(1, writer.StopCount);
            Assert.False(writer.IsRunning);
        }
        finally
        {
            blockedStream.ReleaseOperation();
        }

        await blockedStream.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => writer.DetachedCleanupCount == 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, blockedStream.DisposeCallCount);

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("normal line after canceled I/O cleanup")));
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(1, blockedStream.DisposeCallCount);
        Assert.Equal(1, writer.WrittenLineCount);
        Assert.Equal(1, writer.DroppedLineCount);
        Assert.Equal(2, writer.StartCount);
        Assert.Equal(2, writer.StopCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronouslyBlockingIoEntry_TimesOutAndRecoversWithoutBlockingWriter(
        bool blockFlush)
    {
        using var directory = new TemporaryDirectory();
        var blockedStream = new SynchronouslyBlockingIoEntryStream(blockFlush);
        var streamOpenCount = 0;
        await using var writer = new FileLogWriter(
            (path, mode) => Interlocked.Increment(ref streamOpenCount) == 1
                ? blockedStream
                : CreateTestFileStream(path, mode),
            ioTimeout: TimeSpan.FromMilliseconds(50),
            flushLineInterval: 1,
            recoveryRetryInterval: TimeSpan.FromMilliseconds(10));

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System($"sync-blocked {(blockFlush ? "flush" : "write")}")));
        await blockedStream.OperationEntryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            await WaitUntilAsync(() => writer.WrittenLineCount == 1, TimeSpan.FromSeconds(2));
            await writer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(0, blockedStream.DisposeCallCount);
            Assert.Equal(1, writer.DetachedCleanupCount);
            Assert.Equal(1, writer.WriteTimeoutCount);
            Assert.Equal(1, writer.RecoveryCount);
            Assert.Equal(0, writer.DroppedLineCount);
        }
        finally
        {
            blockedStream.ReleaseOperationEntry();
        }

        await blockedStream.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => writer.DetachedCleanupCount == 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, blockedStream.DisposeCallCount);
    }

    [Fact]
    public async Task SynchronouslyBlockingRetiredDispose_DoesNotBlockCleanupGateOrRecovery()
    {
        using var directory = new TemporaryDirectory();
        var failedStream = new WriteFailureWithSynchronouslyBlockingDisposeStream();
        var streamOpenCount = 0;
        await using var writer = new FileLogWriter(
            (path, mode) => Interlocked.Increment(ref streamOpenCount) == 1
                ? failedStream
                : CreateTestFileStream(path, mode),
            ioTimeout: TimeSpan.FromSeconds(1),
            flushLineInterval: 1,
            recoveryRetryInterval: TimeSpan.FromMilliseconds(10));

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("recover while retired dispose entry blocks")));
        await failedStream.DisposeEntryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            await WaitUntilAsync(() => writer.WrittenLineCount == 1, TimeSpan.FromSeconds(2));
            var cleanupCount = await Task.Run(() => writer.DetachedCleanupCount)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, cleanupCount);
            await writer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, writer.DroppedLineCount);
            Assert.Equal(1, writer.RecoveryCount);
        }
        finally
        {
            failedStream.ReleaseDisposeEntry();
        }

        await WaitUntilAsync(() => writer.DetachedCleanupCount == 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, failedStream.DisposeCallCount);
    }

    [Fact]
    public async Task SynchronouslyBlockingActiveClose_IsCoveredByTimeoutAndDisposedOnce()
    {
        using var directory = new TemporaryDirectory();
        var stream = new SynchronouslyBlockingDisposeStream();
        await using var writer = new FileLogWriter(
            (_, _) => stream,
            ioTimeout: TimeSpan.FromMilliseconds(50),
            flushLineInterval: 1,
            shutdownDrainTimeout: TimeSpan.FromMilliseconds(500),
            forcedShutdownTimeout: TimeSpan.FromMilliseconds(500));

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("durable before sync-blocked close")));
        await WaitUntilAsync(() => writer.WrittenLineCount == 1, TimeSpan.FromSeconds(2));

        try
        {
            await writer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, stream.DisposeCallCount);
            Assert.Equal(1, writer.DetachedCleanupCount);
            Assert.Equal(0, writer.DroppedLineCount);
        }
        finally
        {
            stream.ReleaseDisposeEntry();
        }

        await WaitUntilAsync(() => writer.DetachedCleanupCount == 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, stream.DisposeCallCount);
    }

    [Fact]
    public async Task RepeatedHangingRotationClose_StopsAtFixedCleanupOperationLimit()
    {
        using var directory = new TemporaryDirectory();
        var disposeController = new HangingDisposeController();
        await using var writer = new FileLogWriter(
            (_, _) => new ControlledHangingDisposeStream(disposeController),
            ioTimeout: TimeSpan.FromMilliseconds(10),
            flushLineInterval: 1,
            shutdownDrainTimeout: TimeSpan.FromMilliseconds(100),
            forcedShutdownTimeout: TimeSpan.FromMilliseconds(100))
        {
            MaximumFileSizeBytes = 1
        };

        try
        {
            const int acceptedLineCount = 20;
            await writer.StartAsync(directory.Path, CancellationToken.None);
            for (var index = 0; index < acceptedLineCount; index++)
            {
                Assert.True(writer.TryEnqueue(LogLine.System($"cleanup limit {index}")));
            }

            await WaitUntilAsync(() => !writer.IsRunning, TimeSpan.FromSeconds(5));
            await writer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => disposeController.DisposeCallCount == FileLogWriter.MaximumOutstandingCleanupOperationCount,
                TimeSpan.FromSeconds(2));

            Assert.Equal(FileLogWriter.MaximumOutstandingCleanupOperationCount, disposeController.DisposeCallCount);
            Assert.Equal(FileLogWriter.MaximumOutstandingCleanupOperationCount, writer.DetachedCleanupCount);
            Assert.Equal(FileLogWriter.MaximumOutstandingCleanupOperationCount, disposeController.ActiveDisposeCount);
            Assert.Equal(acceptedLineCount, writer.WrittenLineCount + writer.DroppedLineCount);
            Assert.True(writer.DroppedLineCount > 0);
            Assert.Equal(0, writer.PendingRequestCount);
            Assert.Equal(1, writer.StopCount);
            Assert.False(writer.IsRunning);
            Assert.Equal(FileLogWriterState.Faulted, writer.State);
            Assert.Equal(FileLogWriterFaultCategory.CleanupLimit, writer.LastFault?.Category);
            Assert.False(writer.CanAutoRecover);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var restartError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    writer.StartAsync(directory.Path, CancellationToken.None));
                Assert.Contains("cleanup operations are still pending", restartError.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(FileLogWriter.MaximumOutstandingCleanupOperationCount, writer.DetachedCleanupCount);
                Assert.Equal(FileLogWriter.MaximumOutstandingCleanupOperationCount, disposeController.DisposeCallCount);
            }

            disposeController.ReleaseAll();
            await WaitUntilAsync(() => writer.DetachedCleanupCount == 0, TimeSpan.FromSeconds(2));

            await writer.StartAsync(directory.Path, CancellationToken.None);
            await writer.StopAsync(CancellationToken.None);
            Assert.Equal(2, writer.StartCount);
            Assert.Equal(2, writer.StopCount);
            Assert.InRange(
                disposeController.MaximumActiveDisposeCount,
                1,
                FileLogWriter.MaximumOutstandingCleanupOperationCount);
        }
        finally
        {
            disposeController.ReleaseAll();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermanentWriteOrFlushFailure_StopOrDisposeIsBoundedAndDropsAcceptedLine(bool failFlush)
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter(
            (_, _) => new PermanentFileFailureStream(failFlush),
            ioTimeout: TimeSpan.FromMilliseconds(50),
            flushLineInterval: 1,
            shutdownDrainTimeout: TimeSpan.FromMilliseconds(100),
            forcedShutdownTimeout: TimeSpan.FromMilliseconds(500),
            recoveryRetryInterval: TimeSpan.FromMilliseconds(10));

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("cannot become durable")));
        await WaitUntilAsync(() => writer.RecoveryCount >= 1, TimeSpan.FromSeconds(2));

        var stopTask = failFlush
            ? writer.DisposeAsync().AsTask()
            : writer.StopAsync(CancellationToken.None);
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, writer.WrittenLineCount);
        Assert.Equal(1, writer.DroppedLineCount);
        Assert.Contains("not saved", writer.LastFileError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SizeRotation_TransientUnauthorizedOpenFailure_RetriesCurrentLine()
    {
        using var directory = new TemporaryDirectory();
        var streamOpenCount = 0;
        await using var writer = new FileLogWriter(
            (path, mode) =>
            {
                var attempt = Interlocked.Increment(ref streamOpenCount);
                if (attempt == 2)
                {
                    throw new UnauthorizedAccessException("Injected transient rotation failure.");
                }

                return CreateTestFileStream(path, mode);
            },
            ioTimeout: TimeSpan.FromSeconds(1),
            flushLineInterval: 1,
            recoveryRetryInterval: TimeSpan.FromMilliseconds(10))
        {
            MaximumFileSizeBytes = 1
        };

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("first segment")));
        await WaitUntilAsync(() => writer.WrittenLineCount == 1, TimeSpan.FromSeconds(2));
        Assert.True(writer.TryEnqueue(LogLine.System("survives transient open failure")));
        await WaitUntilAsync(() => writer.WrittenLineCount == 2, TimeSpan.FromSeconds(2));
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(3, streamOpenCount);
        Assert.Equal(0, writer.DroppedLineCount);
        var rotatedFile = Assert.Single(Directory.GetFiles(directory.Path, "*_001.log"));
        Assert.Contains(
            "survives transient open failure",
            await File.ReadAllTextAsync(rotatedFile),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SizeRotation_PermanentDirectoryOpenFailure_DropsCurrentLineOnBoundedStop()
    {
        using var directory = new TemporaryDirectory();
        var streamOpenCount = 0;
        await using var writer = new FileLogWriter(
            (path, mode) =>
            {
                if (Interlocked.Increment(ref streamOpenCount) > 1)
                {
                    throw new DirectoryNotFoundException("Injected permanent rotation failure.");
                }

                return CreateTestFileStream(path, mode);
            },
            ioTimeout: TimeSpan.FromSeconds(1),
            flushLineInterval: 1,
            shutdownDrainTimeout: TimeSpan.FromMilliseconds(100),
            forcedShutdownTimeout: TimeSpan.FromMilliseconds(500),
            recoveryRetryInterval: TimeSpan.FromMilliseconds(10))
        {
            MaximumFileSizeBytes = 1
        };

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("durable first line")));
        await WaitUntilAsync(() => writer.WrittenLineCount == 1, TimeSpan.FromSeconds(2));
        Assert.True(writer.TryEnqueue(LogLine.System("owned during failed rotation")));
        await WaitUntilAsync(() => Volatile.Read(ref streamOpenCount) >= 2, TimeSpan.FromSeconds(2));

        await writer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, writer.WrittenLineCount);
        Assert.Equal(1, writer.DroppedLineCount);
        Assert.Contains("not saved", writer.LastFileError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedStreamWithHangingDispose_DoesNotBlockRecovery()
    {
        using var directory = new TemporaryDirectory();
        var failedStream = new WriteFailureWithHangingDisposeStream();
        var streamOpenCount = 0;
        await using var writer = new FileLogWriter(
            (path, mode) => Interlocked.Increment(ref streamOpenCount) == 1
                ? failedStream
                : CreateTestFileStream(path, mode),
            ioTimeout: TimeSpan.FromMilliseconds(100),
            flushLineInterval: 1,
            recoveryRetryInterval: TimeSpan.FromMilliseconds(10));

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("recovered despite hanging dispose")));

        await failedStream.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => writer.WrittenLineCount == 1, TimeSpan.FromSeconds(2));
        await writer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, writer.RecoveryCount);
        Assert.Equal(0, writer.DroppedLineCount);
        var recoveredFile = Assert.Single(Directory.GetFiles(directory.Path, "*_001.log"));
        Assert.Contains(
            "recovered despite hanging dispose",
            await File.ReadAllTextAsync(recoveredFile),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StalledWrite_OpensRecoverySegment_RetriesUncommittedLine_AndDoesNotDropIt()
    {
        using var directory = new TemporaryDirectory();
        var streamOpenCount = 0;
        await using var writer = new FileLogWriter(
            (path, mode) =>
            {
                if (Interlocked.Increment(ref streamOpenCount) == 1)
                {
                    return new CancellationBlockedWriteStream();
                }

                return new FileStream(
                    path,
                    mode,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            },
            ioTimeout: TimeSpan.FromMilliseconds(50),
            flushLineInterval: 1,
            recoveryRetryInterval: TimeSpan.FromMilliseconds(10));

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("must survive stalled write")));

        await WaitUntilAsync(() => writer.WrittenLineCount == 1, TimeSpan.FromSeconds(5));
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(1, writer.WriteTimeoutCount);
        Assert.Equal(1, writer.RecoveryCount);
        Assert.Equal(0, writer.DroppedLineCount);
        var recoveredFile = Assert.Single(Directory.GetFiles(directory.Path, "*_001.log"));
        Assert.Contains("must survive stalled write", await File.ReadAllTextAsync(recoveredFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StalledFlush_RetriesTheCompleteUncommittedBatchInRecoverySegment()
    {
        using var directory = new TemporaryDirectory();
        var streamOpenCount = 0;
        await using var writer = new FileLogWriter(
            (path, mode) =>
            {
                if (Interlocked.Increment(ref streamOpenCount) == 1)
                {
                    return new CancellationBlockedFlushStream();
                }

                return new FileStream(
                    path,
                    mode,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            },
            ioTimeout: TimeSpan.FromMilliseconds(50),
            flushLineInterval: 3,
            recoveryRetryInterval: TimeSpan.FromMilliseconds(10));

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("batch one")));
        Assert.True(writer.TryEnqueue(LogLine.System("batch two")));
        Assert.True(writer.TryEnqueue(LogLine.System("batch three")));

        await WaitUntilAsync(() => writer.WrittenLineCount == 3, TimeSpan.FromSeconds(5));
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(1, writer.WriteTimeoutCount);
        Assert.Equal(1, writer.RecoveryCount);
        Assert.Equal(0, writer.DroppedLineCount);
        var recoveredFile = Assert.Single(Directory.GetFiles(directory.Path, "*_001.log"));
        var contents = await File.ReadAllTextAsync(recoveredFile);
        Assert.Contains("batch one", contents, StringComparison.Ordinal);
        Assert.Contains("batch two", contents, StringComparison.Ordinal);
        Assert.Contains("batch three", contents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PermanentlyBlockingOpen_StopsAtInstanceOutstandingCapUntilLateStreamsAreDisposed()
    {
        using var directory = new TemporaryDirectory();
        var controller = new BlockingOpenController();
        await using var writer = new FileLogWriter(
            (_, _) => controller.Open(),
            ioTimeout: TimeSpan.FromMilliseconds(10),
            flushLineInterval: 1,
            recoveryRetryInterval: TimeSpan.FromMilliseconds(1),
            maximumRecoveryAttempts: 20,
            recoveryTimeBudget: TimeSpan.FromSeconds(5));
        writer.UpdateLogFileName("capture.log", requestNewFile: false);

        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                writer.StartAsync(directory.Path, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
            await WaitUntilAsync(
                () => controller.OpenCallCount == FileLogWriter.MaximumOutstandingCleanupOperationCount,
                TimeSpan.FromSeconds(5));

            Assert.Equal(FileLogWriter.MaximumOutstandingCleanupOperationCount, writer.DetachedCleanupCount);
            Assert.Equal(0, controller.DisposeCallCount);
            Assert.Equal(FileLogWriterState.Faulted, writer.State);
            Assert.Equal(FileLogWriterFaultCategory.CleanupLimit, writer.LastFault?.Category);
            Assert.False(writer.CanAutoRecover);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    writer.StartAsync(directory.Path, CancellationToken.None));
                Assert.Contains("cleanup operations are still pending", error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(FileLogWriter.MaximumOutstandingCleanupOperationCount, controller.OpenCallCount);
            }

            controller.ReleaseAll();
            await WaitUntilAsync(() => writer.DetachedCleanupCount == 0, TimeSpan.FromSeconds(5));
            Assert.Equal(FileLogWriter.MaximumOutstandingCleanupOperationCount, controller.DisposeCallCount);
            Assert.Equal(FileLogWriter.MaximumOutstandingCleanupOperationCount, controller.MaximumActiveOpenCount);

            await writer.StartAsync(directory.Path, CancellationToken.None);
            await writer.StopAsync(CancellationToken.None);
            Assert.Equal(FileLogWriter.MaximumOutstandingCleanupOperationCount + 1, controller.DisposeCallCount);
        }
        finally
        {
            controller.ReleaseAll();
        }
    }

    [Fact]
    public async Task LateExplicitCreateNewOpen_ContinuesOnNumberedSegmentAndCleansOwnedFile()
    {
        using var directory = new TemporaryDirectory();
        using var releaseOpen = new ManualResetEventSlim(initialState: false);
        var openStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeCallCount = 0;
        var factoryCallCount = 0;
        await using var writer = new FileLogWriter(
            (path, mode) =>
            {
                if (Interlocked.Increment(ref factoryCallCount) == 1)
                {
                    var stream = new CountingDelegatingStream(
                        CreateTestFileStream(path, mode),
                        () => Interlocked.Increment(ref disposeCallCount));
                    openStarted.TrySetResult(true);
                    releaseOpen.Wait();
                    return stream;
                }

                return CreateTestFileStream(path, mode);
            },
            ioTimeout: TimeSpan.FromMilliseconds(30),
            flushLineInterval: 1,
            recoveryRetryInterval: TimeSpan.FromMilliseconds(1),
            maximumRecoveryAttempts: 2,
            recoveryTimeBudget: TimeSpan.FromMilliseconds(80),
            deleteAbandonedExplicitFiles: true);
        writer.UpdateLogFileName("capture.log", requestNewFile: false);
        var abandonedPath = Path.Combine(directory.Path, "capture.log");
        var activePath = Path.Combine(directory.Path, "capture_001.log");

        try
        {
            var startTask = writer.StartAsync(directory.Path, CancellationToken.None);
            await openStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await startTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(FileLogWriterState.Running, writer.State);
            Assert.Null(writer.LastFault);
            Assert.False(writer.CanAutoRecover);
            Assert.Equal(activePath, writer.CurrentLogFilePath);
            Assert.Equal(2, Volatile.Read(ref factoryCallCount));
            Assert.Equal(1, writer.WriteTimeoutCount);
            Assert.Equal(1, writer.RecoveryCount);
            Assert.True(File.Exists(abandonedPath));
            Assert.True(File.Exists(activePath));
            Assert.Equal(0, Volatile.Read(ref disposeCallCount));
            Assert.True(writer.TryEnqueue(LogLine.System("durable numbered recovery segment")));
            await WaitUntilAsync(() => writer.WrittenLineCount == 1, TimeSpan.FromSeconds(2));

            releaseOpen.Set();
            await WaitUntilAsync(() => writer.DetachedCleanupCount == 0, TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => !File.Exists(abandonedPath), TimeSpan.FromSeconds(2));
            Assert.Equal(1, Volatile.Read(ref disposeCallCount));
            Assert.True(File.Exists(activePath));

            await writer.StopAsync(CancellationToken.None);
            Assert.Contains(
                "durable numbered recovery segment",
                await File.ReadAllTextAsync(activePath),
                StringComparison.Ordinal);
            Assert.Equal(1, writer.WrittenLineCount);
            Assert.Equal(0, writer.DroppedLineCount);
        }
        finally
        {
            releaseOpen.Set();
        }
    }

    [Fact]
    public async Task LateExplicitCleanup_DoesNotDeleteNewActiveFileAtReusedPath()
    {
        using var directory = new TemporaryDirectory();
        using var releaseFirstOpen = new ManualResetEventSlim(initialState: false);
        var firstOpenStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldDisposeCallCount = 0;
        var factoryCallCount = 0;
        await using var writer = new FileLogWriter(
            (path, mode) =>
            {
                if (Interlocked.Increment(ref factoryCallCount) == 1)
                {
                    var oldStream = new CountingDelegatingStream(
                        new FileStream(
                            path,
                            mode,
                            FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete,
                            bufferSize: 4096,
                            FileOptions.Asynchronous | FileOptions.SequentialScan),
                        () => Interlocked.Increment(ref oldDisposeCallCount));
                    firstOpenStarted.TrySetResult(true);
                    releaseFirstOpen.Wait();
                    return oldStream;
                }

                return CreateTestFileStream(path, mode);
            },
            ioTimeout: TimeSpan.FromMilliseconds(30),
            flushLineInterval: 1,
            recoveryRetryInterval: TimeSpan.FromMilliseconds(1),
            maximumRecoveryAttempts: 1,
            recoveryTimeBudget: TimeSpan.FromMilliseconds(80),
            deleteAbandonedExplicitFiles: true);
        writer.UpdateLogFileName("capture.log", requestNewFile: false);
        var path = Path.Combine(directory.Path, "capture.log");

        try
        {
            var failedStart = writer.StartAsync(directory.Path, CancellationToken.None);
            await firstOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<IOException>(() => failedStart.WaitAsync(TimeSpan.FromSeconds(2)));
            File.Delete(path);

            await writer.StartAsync(directory.Path, CancellationToken.None);
            Assert.Equal(FileLogWriterState.Running, writer.State);
            Assert.True(File.Exists(path));

            releaseFirstOpen.Set();
            await WaitUntilAsync(() => writer.DetachedCleanupCount == 0, TimeSpan.FromSeconds(2));
            Assert.Equal(1, Volatile.Read(ref oldDisposeCallCount));
            Assert.True(File.Exists(path));
            Assert.True(writer.TryEnqueue(LogLine.System("active replacement survives old cleanup")));
            await WaitUntilAsync(() => writer.WrittenLineCount == 1, TimeSpan.FromSeconds(2));
            await writer.StopAsync(CancellationToken.None);
            Assert.Contains(
                "active replacement survives old cleanup",
                await File.ReadAllTextAsync(path),
                StringComparison.Ordinal);
        }
        finally
        {
            releaseFirstOpen.Set();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstWriteOrFlushFailure_AttemptsRecoveryOpenWithoutConfiguredBackoffDelay(
        bool failFlush)
    {
        using var directory = new TemporaryDirectory();
        var failedWriteAt = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveryOpenAt = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamOpenCount = 0;
        await using var writer = new FileLogWriter(
            (_, _) =>
            {
                if (Interlocked.Increment(ref streamOpenCount) == 1)
                {
                    return new SignalingIoFailureStream(failedWriteAt, failFlush);
                }

                recoveryOpenAt.TrySetResult(Stopwatch.GetTimestamp());
                return new DiscardingWriteStream();
            },
            ioTimeout: TimeSpan.FromSeconds(1),
            flushLineInterval: 1,
            recoveryRetryInterval: TimeSpan.FromSeconds(5),
            maximumRecoveryAttempts: 3,
            recoveryTimeBudget: TimeSpan.FromSeconds(10));

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("immediate failover")));

        var failedTimestamp = await failedWriteAt.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var recoveryTimestamp = await recoveryOpenAt.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => writer.WrittenLineCount == 1, TimeSpan.FromSeconds(2));
        await writer.StopAsync(CancellationToken.None);

        Assert.True(
            Stopwatch.GetElapsedTime(failedTimestamp, recoveryTimestamp) < TimeSpan.FromSeconds(1),
            "The first recovery open incorrectly waited for the configured five-second backoff.");
        Assert.Equal(0, writer.DroppedLineCount);
    }

    [Fact]
    public void ProductionPolicy_ProtectsMaximum921600Baud5N1IngressWithExplicitReserves()
    {
        Assert.Equal(921_600, SerialPortPolicy.MaximumSupportedBaudRate);
        Assert.Equal(SerialPortPolicy.MaximumSupportedBaudRate, SerialPortPolicy.SupportedBaudRates[^1]);
        Assert.Equal([5, 6, 7, 8], SerialPortPolicy.SupportedDataBits);
        Assert.Contains(SerialParityMode.None, SerialPortPolicy.SupportedParityModes);
        Assert.Contains(SerialStopBitsMode.One, SerialPortPolicy.SupportedStopBitsModes);
        Assert.Equal(
            7d,
            SerialPortPolicy.GetBitsPerCharacter(
                dataBits: 5,
                SerialParityMode.None,
                SerialStopBitsMode.One));
        Assert.Equal(7, SerialPortPolicy.MinimumSupportedBitsPerCharacter);

        var minimumSupportedFrameSize =
            (from dataBits in SerialPortPolicy.SupportedDataBits
             from parity in SerialPortPolicy.SupportedParityModes
             from stopBits in SerialPortPolicy.SupportedStopBitsModes
             select SerialPortPolicy.GetBitsPerCharacter(dataBits, parity, stopBits))
            .Min();
        Assert.Equal(SerialPortPolicy.MinimumSupportedBitsPerCharacter, minimumSupportedFrameSize);

        Assert.Equal(131_657, FileLogWriter.MaximumWireRecordsPerSecond);
        Assert.Equal(164_572, FileLogWriter.ReservedIngressRecordsPerSecond);
        Assert.Equal(TimeSpan.FromMilliseconds(800), FileLogWriter.ProtectedIngressWindow);
        Assert.Equal(131_658, FileLogWriter.RequiredProtectedQueueCapacity);
        Assert.Equal(140_000, FileLogWriter.DefaultQueueCapacity);
        Assert.Equal(8_342, FileLogWriter.DefaultQueueCapacity - FileLogWriter.RequiredProtectedQueueCapacity);
        Assert.True(FileLogWriter.DefaultQueueCapacity >= FileLogWriter.RequiredProtectedQueueCapacity);
        Assert.Equal(TimeSpan.FromMilliseconds(200), FileLogWriter.DefaultIoTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(500), FileLogWriter.DefaultRecoveryTimeBudget);
        Assert.Equal(TimeSpan.FromMilliseconds(25), FileLogWriter.DefaultRecoveryRetryInterval);
    }

    [Fact]
    public async Task ProductionPolicy_921600Baud5N1EquivalentIngressDoesNotDropBeforeFaultDecision()
    {
        using var directory = new TemporaryDirectory();
        var blockedStream = new ControlledIgnoringCancellationIoStream(blockFlush: false);
        var streamOpenCount = 0;
        await using var writer = new FileLogWriter(
            (_, _) => Interlocked.Increment(ref streamOpenCount) == 1
                ? blockedStream
                : throw new IOException("Injected unavailable recovery open."),
            ioTimeout: FileLogWriter.DefaultIoTimeout,
            flushLineInterval: 100,
            recoveryRetryInterval: FileLogWriter.DefaultRecoveryRetryInterval,
            maximumRecoveryAttempts: FileLogWriter.DefaultMaximumRecoveryAttempts,
            recoveryTimeBudget: FileLogWriter.DefaultRecoveryTimeBudget,
            queueCapacity: FileLogWriter.DefaultQueueCapacity);

        await writer.StartAsync(directory.Path, CancellationToken.None);
        try
        {
            var sharedLine = LogLine.System("921600 baud 5N1 maximum-rate equivalent line");
            for (var index = 0; index < 100; index++)
            {
                Assert.True(writer.TryEnqueue(sharedLine));
            }

            await blockedStream.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var rejectedBeforeDecision = 0;
            for (var index = 100; index < FileLogWriter.RequiredProtectedQueueCapacity; index++)
            {
                if (!writer.TryEnqueue(sharedLine))
                {
                    rejectedBeforeDecision++;
                }
            }

            Assert.Equal(0, rejectedBeforeDecision);
            Assert.Equal(0, writer.DroppedLineCount);
            await WaitUntilAsync(
                () => writer.State == FileLogWriterState.Faulted,
                TimeSpan.FromSeconds(3));
            await WaitUntilAsync(
                () => writer.DroppedLineCount == FileLogWriter.RequiredProtectedQueueCapacity,
                TimeSpan.FromSeconds(3));

            Assert.Equal(FileLogWriterFaultCategory.RetryableIo, writer.LastFault?.Category);
            Assert.True(writer.CanAutoRecover);
            Assert.Equal(0, writer.WrittenLineCount);
            Assert.Equal(0, writer.PendingRequestCount);

            blockedStream.ReleaseOperation();
            await WaitUntilAsync(() => writer.DetachedCleanupCount == 0, TimeSpan.FromSeconds(2));
        }
        finally
        {
            blockedStream.ReleaseOperation();
        }
    }

    [Fact]
    public async Task HighSpeedProducer_RemainsBoundedAndAccountsEveryAttemptDuringFailover()
    {
        using var directory = new TemporaryDirectory();
        var blockedStream = new ControlledIgnoringCancellationIoStream(blockFlush: false);
        var streamOpenCount = 0;
        await using var writer = new FileLogWriter(
            (_, _) => Interlocked.Increment(ref streamOpenCount) == 1
                ? blockedStream
                : new DiscardingWriteStream(),
            ioTimeout: TimeSpan.FromMilliseconds(50),
            flushLineInterval: 1,
            recoveryRetryInterval: TimeSpan.FromMilliseconds(1),
            queueCapacity: 32);

        const int attemptedLineCount = 2_000;
        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("producer 0")));
        await blockedStream.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var index = 1; index < attemptedLineCount; index++)
        {
            writer.TryEnqueue(LogLine.System($"producer {index}"));
        }

        try
        {
            await WaitUntilAsync(
                () => writer.WrittenLineCount + writer.DroppedLineCount == attemptedLineCount,
                TimeSpan.FromSeconds(5));
            await writer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(writer.DroppedLineCount > 0);
            Assert.Equal(attemptedLineCount, writer.WrittenLineCount + writer.DroppedLineCount);
            Assert.Equal(0, writer.PendingRequestCount);
        }
        finally
        {
            blockedStream.ReleaseOperation();
        }

        await WaitUntilAsync(() => writer.DetachedCleanupCount == 0, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RecoveryDeadline_CoversOpenWriteAndFlushAsOneAbsoluteIncident()
    {
        using var directory = new TemporaryDirectory();
        var failureAt = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamOpenCount = 0;
        await using var writer = new FileLogWriter(
            (_, _) =>
            {
                if (Interlocked.Increment(ref streamOpenCount) == 1)
                {
                    return new SignalingIoFailureStream(failureAt, failFlush: false);
                }

                Thread.Sleep(45);
                return new DelayedWriteAndFlushStream(
                    writeDelay: TimeSpan.FromMilliseconds(45),
                    flushDelay: TimeSpan.FromMilliseconds(45));
            },
            ioTimeout: TimeSpan.FromMilliseconds(80),
            flushLineInterval: 1,
            recoveryRetryInterval: TimeSpan.FromMilliseconds(1),
            maximumRecoveryAttempts: 20,
            recoveryTimeBudget: TimeSpan.FromMilliseconds(110));

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("must not commit past the incident deadline")));
        var failedTimestamp = await failureAt.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => writer.State == FileLogWriterState.Faulted, TimeSpan.FromSeconds(2));
        var elapsed = Stopwatch.GetElapsedTime(failedTimestamp);

        Assert.InRange(elapsed, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, writer.WrittenLineCount);
        Assert.Equal(1, writer.DroppedLineCount);
        Assert.Contains("recovery", writer.LastFault?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecoveryOpenSuccess_LeavesOnlyRemainingDeadlineForBlockedWrite()
    {
        using var directory = new TemporaryDirectory();
        var failureAt = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedRecoveryStream = new ControlledIgnoringCancellationIoStream(blockFlush: false);
        var streamOpenCount = 0;
        await using var writer = new FileLogWriter(
            (_, _) =>
            {
                if (Interlocked.Increment(ref streamOpenCount) == 1)
                {
                    return new SignalingIoFailureStream(failureAt, failFlush: false);
                }

                Thread.Sleep(70);
                return blockedRecoveryStream;
            },
            ioTimeout: TimeSpan.FromMilliseconds(80),
            flushLineInterval: 1,
            recoveryRetryInterval: TimeSpan.FromMilliseconds(1),
            maximumRecoveryAttempts: 20,
            recoveryTimeBudget: TimeSpan.FromMilliseconds(100));

        await writer.StartAsync(directory.Path, CancellationToken.None);
        try
        {
            Assert.True(writer.TryEnqueue(LogLine.System("remaining deadline")));
            var failedTimestamp = await failureAt.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await blockedRecoveryStream.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => writer.State == FileLogWriterState.Faulted, TimeSpan.FromSeconds(2));
            var elapsed = Stopwatch.GetElapsedTime(failedTimestamp);

            Assert.InRange(elapsed, TimeSpan.FromMilliseconds(70), TimeSpan.FromMilliseconds(250));
            Assert.Equal(0, writer.WrittenLineCount);
            Assert.Equal(1, writer.DroppedLineCount);
            blockedRecoveryStream.ReleaseOperation();
            await WaitUntilAsync(() => writer.DetachedCleanupCount == 0, TimeSpan.FromSeconds(2));
        }
        finally
        {
            blockedRecoveryStream.ReleaseOperation();
        }
    }

    [Fact]
    public async Task OperationReturningSuccessAfterRecoveryDeadline_IsNotCommitted()
    {
        using var directory = new TemporaryDirectory();
        var timeProvider = new ManualMonotonicTimeProvider();
        var failureAt = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamOpenCount = 0;
        await using var writer = new FileLogWriter(
            (_, _) => Interlocked.Increment(ref streamOpenCount) == 1
                ? new SignalingIoFailureStream(failureAt, failFlush: false)
                : new AdvancingWriteStream(timeProvider, TimeSpan.FromMilliseconds(101)),
            ioTimeout: TimeSpan.FromSeconds(1),
            flushLineInterval: 1,
            recoveryRetryInterval: TimeSpan.FromMilliseconds(1),
            maximumRecoveryAttempts: 20,
            recoveryTimeBudget: TimeSpan.FromMilliseconds(100),
            timeProvider: timeProvider);

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("late successful write")));
        await failureAt.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => writer.State == FileLogWriterState.Faulted, TimeSpan.FromSeconds(2));

        Assert.Equal(0, writer.WrittenLineCount);
        Assert.Equal(1, writer.DroppedLineCount);
        Assert.True(timeProvider.Elapsed >= TimeSpan.FromMilliseconds(101));
    }

    [Fact]
    public async Task RotationOpenAndFlush_ShareOneRecoveryDeadline()
    {
        using var directory = new TemporaryDirectory();
        var timeProvider = new ManualMonotonicTimeProvider();
        var streamOpenCount = 0;
        await using var writer = new FileLogWriter(
            (_, _) =>
            {
                if (Interlocked.Increment(ref streamOpenCount) == 1)
                {
                    return new DiscardingWriteStream();
                }

                timeProvider.Advance(TimeSpan.FromMilliseconds(60));
                return new AdvancingWriteAndFlushStream(
                    timeProvider,
                    writeAdvance: TimeSpan.FromMilliseconds(20),
                    flushAdvance: TimeSpan.FromMilliseconds(30));
            },
            ioTimeout: TimeSpan.FromSeconds(1),
            flushLineInterval: 1,
            recoveryRetryInterval: TimeSpan.FromMilliseconds(1),
            maximumRecoveryAttempts: 20,
            recoveryTimeBudget: TimeSpan.FromMilliseconds(100),
            timeProvider: timeProvider)
        {
            MaximumFileSizeBytes = 1
        };

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("durable first segment")));
        await WaitUntilAsync(() => writer.WrittenLineCount == 1, TimeSpan.FromSeconds(2));
        Assert.True(writer.TryEnqueue(LogLine.System("rotation must share deadline")));
        await WaitUntilAsync(() => writer.State == FileLogWriterState.Faulted, TimeSpan.FromSeconds(2));

        Assert.Equal(1, writer.WrittenLineCount);
        Assert.Equal(1, writer.DroppedLineCount);
        Assert.Equal(TimeSpan.FromMilliseconds(110), timeProvider.Elapsed);
    }

    [Fact]
    public async Task FaultingRotationClose_ContinuesWithNextSegmentAndDisposesOnce()
    {
        using var directory = new TemporaryDirectory();
        FaultingDisposeFileStream? faultingStream = null;
        var streamOpenCount = 0;
        await using var writer = new FileLogWriter(
            (path, mode) =>
            {
                if (Interlocked.Increment(ref streamOpenCount) == 1)
                {
                    faultingStream = new FaultingDisposeFileStream(CreateTestFileStream(path, mode));
                    return faultingStream;
                }

                return CreateTestFileStream(path, mode);
            },
            ioTimeout: TimeSpan.FromSeconds(1),
            flushLineInterval: 1)
        {
            MaximumFileSizeBytes = 1
        };

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("before faulting close")));
        await WaitUntilAsync(() => writer.WrittenLineCount == 1, TimeSpan.FromSeconds(2));
        Assert.True(writer.TryEnqueue(LogLine.System("after faulting close")));
        await WaitUntilAsync(() => writer.WrittenLineCount == 2, TimeSpan.FromSeconds(2));
        await writer.StopAsync(CancellationToken.None);

        Assert.NotNull(faultingStream);
        Assert.Equal(1, faultingStream.DisposeCallCount);
        Assert.Equal(0, writer.DroppedLineCount);
        Assert.True(streamOpenCount >= 2);
        var recoveryFile = Assert.Single(Directory.GetFiles(directory.Path, "*_001.log"));
        Assert.Contains(
            "after faulting close",
            await File.ReadAllTextAsync(recoveryFile),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedFaultingRotationClose_StopsAtConsecutiveFailureLimit()
    {
        using var directory = new TemporaryDirectory();
        var streams = new ConcurrentQueue<FaultingDisposeStream>();
        await using var writer = new FileLogWriter(
            (_, _) =>
            {
                var stream = new FaultingDisposeStream();
                streams.Enqueue(stream);
                return stream;
            },
            ioTimeout: TimeSpan.FromSeconds(1),
            flushLineInterval: 1)
        {
            MaximumFileSizeBytes = 1
        };

        const int acceptedLineCount = 10;
        await writer.StartAsync(directory.Path, CancellationToken.None);
        for (var index = 0; index < acceptedLineCount; index++)
        {
            Assert.True(writer.TryEnqueue(LogLine.System($"faulting close {index}")));
        }

        await WaitUntilAsync(() => !writer.IsRunning, TimeSpan.FromSeconds(2));
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(FileLogWriter.MaximumConsecutiveCloseFailureCount, streams.Count);
        Assert.All(streams, stream => Assert.Equal(1, stream.DisposeCallCount));
        Assert.Equal(acceptedLineCount, writer.WrittenLineCount + writer.DroppedLineCount);
        Assert.True(writer.DroppedLineCount > 0);
        Assert.Equal(FileLogWriterState.Faulted, writer.State);
        Assert.Equal(FileLogWriterFaultCategory.CloseFailureLimit, writer.LastFault?.Category);
        Assert.False(writer.CanAutoRecover);
        var restartError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.StartAsync(directory.Path, CancellationToken.None));
        Assert.Contains("consecutive stream close failures", restartError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FileLogWriter.MaximumConsecutiveCloseFailureCount, streams.Count);
    }

    [Fact]
    public async Task ExplicitFilePathOccupiedByDirectory_IsFatalWithoutRetryingFactory()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "capture.log"));
        var factoryCallCount = 0;
        await using var writer = new FileLogWriter(
            (_, _) =>
            {
                Interlocked.Increment(ref factoryCallCount);
                return new DiscardingWriteStream();
            },
            ioTimeout: TimeSpan.FromMilliseconds(50),
            flushLineInterval: 1,
            recoveryRetryInterval: TimeSpan.FromMilliseconds(1));
        writer.UpdateLogFileName("capture.log", requestNewFile: false);

        var error = await Assert.ThrowsAnyAsync<IOException>(() =>
            writer.StartAsync(directory.Path, CancellationToken.None));

        Assert.Contains("occupied by a directory", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, factoryCallCount);
        Assert.Equal(0, writer.RecoveryCount);
        Assert.Equal(FileLogWriterState.Faulted, writer.State);
        Assert.Equal(FileLogWriterFaultCategory.DeterministicConfiguration, writer.LastFault?.Category);
        Assert.False(writer.CanAutoRecover);
    }

    [Fact]
    public async Task PathTooLongOpenFailure_IsFatalWithoutRetry()
    {
        using var directory = new TemporaryDirectory();
        var factoryCallCount = 0;
        await using var writer = new FileLogWriter(
            (_, _) =>
            {
                Interlocked.Increment(ref factoryCallCount);
                throw new PathTooLongException("Injected deterministic path-too-long failure.");
            },
            ioTimeout: TimeSpan.FromMilliseconds(50),
            flushLineInterval: 1,
            recoveryRetryInterval: TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAsync<PathTooLongException>(() =>
            writer.StartAsync(directory.Path, CancellationToken.None));

        Assert.Equal(1, factoryCallCount);
        Assert.Equal(0, writer.RecoveryCount);
        Assert.Equal(FileLogWriterState.Faulted, writer.State);
        Assert.Equal(FileLogWriterFaultCategory.DeterministicConfiguration, writer.LastFault?.Category);
        Assert.False(writer.CanAutoRecover);
    }

    [Fact]
    public async Task DeletedLogDirectory_IsRecreatedInsideIsolatedRotationOpen()
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter(
            (_, _) => new DiscardingWriteStream(),
            ioTimeout: TimeSpan.FromSeconds(1),
            flushLineInterval: 1)
        {
            MaximumFileSizeBytes = 1
        };

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("before directory deletion")));
        await WaitUntilAsync(() => writer.WrittenLineCount == 1, TimeSpan.FromSeconds(2));
        Directory.Delete(directory.Path);

        Assert.True(writer.TryEnqueue(LogLine.System("after directory recreation")));
        await WaitUntilAsync(() => writer.WrittenLineCount == 2, TimeSpan.FromSeconds(2));
        await writer.StopAsync(CancellationToken.None);

        Assert.True(Directory.Exists(directory.Path));
        Assert.Equal(0, writer.DroppedLineCount);
    }

    [Fact]
    public async Task WriteOnlyPermanentFailure_StopsWithinBudgetAndBoundsCreatedSegments()
    {
        using var directory = new TemporaryDirectory();
        var streamOpenCount = 0;
        await using var writer = new FileLogWriter(
            (path, mode) =>
            {
                Interlocked.Increment(ref streamOpenCount);
                return new WriteFailingFileStream(CreateTestFileStream(path, mode));
            },
            ioTimeout: TimeSpan.FromMilliseconds(50),
            flushLineInterval: 1,
            recoveryRetryInterval: TimeSpan.FromMilliseconds(1),
            maximumRecoveryAttempts: 4,
            recoveryTimeBudget: TimeSpan.FromSeconds(1));

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("permanent write failure")));
        await WaitUntilAsync(() => !writer.IsRunning, TimeSpan.FromSeconds(2));
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(4, streamOpenCount);
        Assert.Equal(4, Directory.GetFiles(directory.Path, "*.log").Length);
        Assert.Equal(0, writer.WrittenLineCount);
        Assert.Equal(1, writer.DroppedLineCount);
    }

    [Fact]
    public async Task RepeatedAverageSizedBatches_ReuseSingleLargePooledBuffer()
    {
        using var directory = new TemporaryDirectory();
        var pool = new TrackingArrayPool(largeBufferThreshold: 85_000);
        await using var writer = new FileLogWriter(
            (_, _) => new DiscardingWriteStream(),
            ioTimeout: TimeSpan.FromSeconds(1),
            flushLineInterval: 100,
            bufferPool: pool);
        var payload = new string('A', 850);
        const int lineCount = 5_000;

        await writer.StartAsync(directory.Path, CancellationToken.None);
        for (var index = 0; index < lineCount; index++)
        {
            Assert.True(writer.TryEnqueue(LogLine.System(payload)));
        }

        await WaitUntilAsync(() => writer.WrittenLineCount == lineCount, TimeSpan.FromSeconds(10));
        var rentCountBeforeStop = pool.RentCount;
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(6, rentCountBeforeStop);
        Assert.Equal(1, pool.LargeRentCount);
        Assert.Equal(2, pool.MaximumOutstandingBufferCount);
        Assert.Equal(pool.RentCount, pool.ReturnCount);
    }

    [Fact]
    public async Task RepeatedLongHexSizedLines_DoNotRentPerBatch()
    {
        using var directory = new TemporaryDirectory();
        var pool = new TrackingArrayPool(largeBufferThreshold: 85_000);
        await using var writer = new FileLogWriter(
            (_, _) => new DiscardingWriteStream(),
            ioTimeout: TimeSpan.FromSeconds(1),
            flushLineInterval: 1,
            bufferPool: pool);
        var longHexLikeLine = string.Concat(Enumerable.Repeat("AA ", 70_000));
        const int lineCount = 25;

        await writer.StartAsync(directory.Path, CancellationToken.None);
        for (var index = 0; index < lineCount; index++)
        {
            Assert.True(writer.TryEnqueue(LogLine.System(longHexLikeLine)));
        }

        await WaitUntilAsync(() => writer.WrittenLineCount == lineCount, TimeSpan.FromSeconds(10));
        var rentCountBeforeStop = pool.RentCount;
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(2, rentCountBeforeStop);
        Assert.Equal(1, pool.LargeRentCount);
        Assert.Equal(pool.RentCount, pool.ReturnCount);
    }

    [Fact]
    public async Task TimedOutWrite_KeepsPooledBufferLeasedUntilUnderlyingTaskCompletes()
    {
        using var directory = new TemporaryDirectory();
        var pool = new TrackingArrayPool(largeBufferThreshold: 85_000);
        var blockedStream = new CapturingBlockedWriteStream();
        var streamOpenCount = 0;
        await using var writer = new FileLogWriter(
            (_, _) => Interlocked.Increment(ref streamOpenCount) == 1
                ? blockedStream
                : new DiscardingWriteStream(),
            ioTimeout: TimeSpan.FromMilliseconds(50),
            flushLineInterval: 1,
            recoveryRetryInterval: TimeSpan.FromMilliseconds(1),
            bufferPool: pool);

        await writer.StartAsync(directory.Path, CancellationToken.None);
        try
        {
            Assert.True(writer.TryEnqueue(LogLine.System(new string('B', 100_000))));
            var capturedBuffer = await blockedStream.CapturedBuffer.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => writer.WrittenLineCount == 1, TimeSpan.FromSeconds(2));
            await writer.StopAsync(CancellationToken.None);

            Assert.False(pool.WasReturned(capturedBuffer));
            Assert.Equal(0, blockedStream.DisposeCallCount);
            blockedStream.ReleaseWrite();
            await WaitUntilAsync(() => pool.WasReturned(capturedBuffer), TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => writer.DetachedCleanupCount == 0, TimeSpan.FromSeconds(2));
            Assert.Equal(1, blockedStream.DisposeCallCount);
        }
        finally
        {
            blockedStream.ReleaseWrite();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not satisfied before the test timeout.");
            }

            await Task.Delay(10);
        }
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current || Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }

    private static Stream CreateTestFileStream(string path, FileMode mode)
    {
        return new FileStream(
            path,
            mode,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static async Task<string> ReadAllTextWhileOpenAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private sealed class BlockingOpenController
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private int _activeOpenCount;
        private int _disposeCallCount;
        private int _maximumActiveOpenCount;
        private int _openCallCount;

        public int OpenCallCount => Volatile.Read(ref _openCallCount);

        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        public int MaximumActiveOpenCount => Volatile.Read(ref _maximumActiveOpenCount);

        public Stream Open()
        {
            Interlocked.Increment(ref _openCallCount);
            var active = Interlocked.Increment(ref _activeOpenCount);
            UpdateMaximum(ref _maximumActiveOpenCount, active);
            try
            {
                _release.Wait();
            }
            finally
            {
                Interlocked.Decrement(ref _activeOpenCount);
            }

            return new ControllerOwnedStream(this);
        }

        public void ReleaseAll() => _release.Set();

        private void RecordDispose() => Interlocked.Increment(ref _disposeCallCount);

        private sealed class ControllerOwnedStream(BlockingOpenController owner) : WriteOnlyTestStream
        {
            private int _disposed;

            public override ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.RecordDispose();
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class CountingDisposeStream : WriteOnlyTestStream
    {
        private int _disposeCallCount;

        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCallCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingDelegatingStream(Stream inner, Action disposed) : Stream
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

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                disposed();
                await inner.DisposeAsync();
            }
        }
    }

    private sealed class DelayedWriteAndFlushStream(
        TimeSpan writeDelay,
        TimeSpan flushDelay) : WriteOnlyTestStream
    {
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(writeDelay);
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(flushDelay);
        }
    }

    private sealed class AdvancingWriteStream(
        ManualMonotonicTimeProvider timeProvider,
        TimeSpan advanceBy) : WriteOnlyTestStream
    {
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            timeProvider.Advance(advanceBy);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AdvancingWriteAndFlushStream(
        ManualMonotonicTimeProvider timeProvider,
        TimeSpan writeAdvance,
        TimeSpan flushAdvance) : WriteOnlyTestStream
    {
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            timeProvider.Advance(writeAdvance);
            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            timeProvider.Advance(flushAdvance);
            return Task.CompletedTask;
        }
    }

    private sealed class ManualMonotonicTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _timestamp));

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan value) => Interlocked.Add(ref _timestamp, value.Ticks);
    }

    private sealed class SignalingIoFailureStream(
        TaskCompletionSource<long> failedAt,
        bool failFlush) : WriteOnlyTestStream
    {
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (!failFlush)
            {
                return Task.CompletedTask;
            }

            failedAt.TrySetResult(Stopwatch.GetTimestamp());
            return Task.FromException(new IOException("Injected first flush failure."));
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (failFlush)
            {
                return ValueTask.CompletedTask;
            }

            failedAt.TrySetResult(Stopwatch.GetTimestamp());
            return ValueTask.FromException(new IOException("Injected first write failure."));
        }
    }

    private sealed class DiscardingWriteStream : WriteOnlyTestStream
    {
    }

    private sealed class FaultingDisposeStream : WriteOnlyTestStream
    {
        private int _disposeCallCount;

        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCallCount);
            return ValueTask.FromException(new IOException("Injected close failure."));
        }
    }

    private sealed class FaultingDisposeFileStream(Stream inner) : Stream
    {
        private int _disposeCallCount;

        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

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

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);

        public override async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCallCount);
            await inner.DisposeAsync();
            throw new IOException("Injected close failure after the file handle was closed.");
        }
    }

    private sealed class WriteFailingFileStream(Stream inner) : Stream
    {
        private int _disposed;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("Injected permanent write-only failure.");

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Injected permanent write-only failure."));

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await inner.DisposeAsync();
            }
        }
    }

    private sealed class TrackingArrayPool(int largeBufferThreshold) : ArrayPool<byte>
    {
        private readonly ConcurrentDictionary<byte[], byte> _outstanding = new();
        private readonly ConcurrentDictionary<byte[], byte> _returned = new();
        private int _largeRentCount;
        private int _maximumOutstandingBufferCount;
        private int _rentCount;
        private int _returnCount;

        public int RentCount => Volatile.Read(ref _rentCount);

        public int ReturnCount => Volatile.Read(ref _returnCount);

        public int LargeRentCount => Volatile.Read(ref _largeRentCount);

        public int MaximumOutstandingBufferCount => Volatile.Read(ref _maximumOutstandingBufferCount);

        public override byte[] Rent(int minimumLength)
        {
            var buffer = new byte[minimumLength];
            Interlocked.Increment(ref _rentCount);
            if (minimumLength >= largeBufferThreshold)
            {
                Interlocked.Increment(ref _largeRentCount);
            }

            Assert.True(_outstanding.TryAdd(buffer, 0));
            UpdateMaximum(ref _maximumOutstandingBufferCount, _outstanding.Count);
            return buffer;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Assert.True(_outstanding.TryRemove(array, out _));
            _returned.TryAdd(array, 0);
            Interlocked.Increment(ref _returnCount);
        }

        public bool WasReturned(byte[] buffer) => _returned.ContainsKey(buffer);
    }

    private sealed class CapturingBlockedWriteStream : WriteOnlyTestStream
    {
        private readonly TaskCompletionSource<bool> _releaseWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCallCount;

        public TaskCompletionSource<byte[]> CapturedBuffer { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Assert.True(MemoryMarshal.TryGetArray(buffer, out var segment));
            CapturedBuffer.TrySetResult(segment.Array!);
            return new ValueTask(_releaseWrite.Task);
        }

        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCallCount);
            return ValueTask.CompletedTask;
        }

        public void ReleaseWrite() => _releaseWrite.TrySetResult(true);
    }

    private sealed class PermanentFileFailureStream(bool failFlush) : WriteOnlyTestStream
    {
        public override Task FlushAsync(CancellationToken cancellationToken) => failFlush
            ? Task.FromException(new IOException("Injected permanent flush failure."))
            : Task.CompletedTask;

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) => failFlush
                ? ValueTask.CompletedTask
                : ValueTask.FromException(new IOException("Injected permanent write failure."));
    }

    private sealed class WriteFailureWithHangingDisposeStream : WriteOnlyTestStream
    {
        public TaskCompletionSource<bool> DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask DisposeAsync()
        {
            DisposeStarted.TrySetResult(true);
            return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan));
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Injected write failure before hanging disposal."));
    }

    private sealed class FirstWriteGateStream : WriteOnlyTestStream
    {
        private readonly TaskCompletionSource<bool> _releaseFirstWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writeCount;

        public TaskCompletionSource<bool> FirstWriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseFirstWrite() => _releaseFirstWrite.TrySetResult(true);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writeCount) != 1)
            {
                return ValueTask.CompletedTask;
            }

            FirstWriteStarted.TrySetResult(true);
            return new ValueTask(_releaseFirstWrite.Task.WaitAsync(cancellationToken));
        }
    }

    private sealed class CountingHangingDisposeStream : WriteOnlyTestStream
    {
        private readonly TaskCompletionSource<bool> _releaseDispose =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCallCount;

        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        public void ReleaseDispose() => _releaseDispose.TrySetResult(true);

        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCallCount);
            return new ValueTask(_releaseDispose.Task);
        }
    }

    private sealed class ControlledIgnoringCancellationIoStream(bool blockFlush) : WriteOnlyTestStream
    {
        private readonly TaskCompletionSource<bool> _releaseOperation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCallCount;

        public TaskCompletionSource<bool> OperationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (blockFlush)
            {
                return ValueTask.CompletedTask;
            }

            OperationStarted.TrySetResult(true);
            return new ValueTask(_releaseOperation.Task);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (!blockFlush)
            {
                return Task.CompletedTask;
            }

            OperationStarted.TrySetResult(true);
            return _releaseOperation.Task;
        }

        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCallCount);
            DisposeStarted.TrySetResult(true);
            return ValueTask.CompletedTask;
        }

        public void ReleaseOperation() => _releaseOperation.TrySetResult(true);
    }

    private sealed class SynchronouslyBlockingIoEntryStream(bool blockFlush) : WriteOnlyTestStream
    {
        private readonly ManualResetEventSlim _releaseOperationEntry = new(initialState: false);
        private int _disposeCallCount;

        public TaskCompletionSource<bool> OperationEntryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (blockFlush)
            {
                return ValueTask.CompletedTask;
            }

            BlockOperationEntry("write");
            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (!blockFlush)
            {
                return Task.CompletedTask;
            }

            BlockOperationEntry("flush");
            return Task.CompletedTask;
        }

        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCallCount);
            DisposeStarted.TrySetResult(true);
            _releaseOperationEntry.Dispose();
            return ValueTask.CompletedTask;
        }

        public void ReleaseOperationEntry() => _releaseOperationEntry.Set();

        private void BlockOperationEntry(string action)
        {
            OperationEntryStarted.TrySetResult(true);
            _releaseOperationEntry.Wait();
            throw new IOException($"Injected synchronous {action} entry failure after release.");
        }
    }

    private sealed class WriteFailureWithSynchronouslyBlockingDisposeStream : WriteOnlyTestStream
    {
        private readonly ManualResetEventSlim _releaseDisposeEntry = new(initialState: false);
        private int _disposeCallCount;

        public TaskCompletionSource<bool> DisposeEntryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Injected write failure before synchronous dispose."));

        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCallCount);
            DisposeEntryStarted.TrySetResult(true);
            _releaseDisposeEntry.Wait();
            _releaseDisposeEntry.Dispose();
            return ValueTask.CompletedTask;
        }

        public void ReleaseDisposeEntry() => _releaseDisposeEntry.Set();
    }

    private sealed class SynchronouslyBlockingDisposeStream : WriteOnlyTestStream
    {
        private readonly ManualResetEventSlim _releaseDisposeEntry = new(initialState: false);
        private int _disposeCallCount;

        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCallCount);
            _releaseDisposeEntry.Wait();
            _releaseDisposeEntry.Dispose();
            return ValueTask.CompletedTask;
        }

        public void ReleaseDisposeEntry() => _releaseDisposeEntry.Set();
    }

    private sealed class WriteOnlyTestStreamImpl : WriteOnlyTestStream
    {
    }

    private sealed class HangingDisposeController
    {
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeDisposeCount;
        private int _disposeCallCount;
        private int _maximumActiveDisposeCount;

        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        public int ActiveDisposeCount => Volatile.Read(ref _activeDisposeCount);

        public int MaximumActiveDisposeCount => Volatile.Read(ref _maximumActiveDisposeCount);

        public async Task WaitForReleaseAsync()
        {
            Interlocked.Increment(ref _disposeCallCount);
            var active = Interlocked.Increment(ref _activeDisposeCount);
            UpdateMaximum(ref _maximumActiveDisposeCount, active);
            try
            {
                await _release.Task;
            }
            finally
            {
                Interlocked.Decrement(ref _activeDisposeCount);
            }
        }

        public void ReleaseAll() => _release.TrySetResult(true);

        private static void UpdateMaximum(ref int target, int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref target);
                if (candidate <= current || Interlocked.CompareExchange(ref target, candidate, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class ControlledHangingDisposeStream(HangingDisposeController controller) : WriteOnlyTestStream
    {
        public override ValueTask DisposeAsync() => new(controller.WaitForReleaseAsync());
    }

    private abstract class WriteOnlyTestStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class CancellationBlockedWriteStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
    }

    private sealed class CancellationBlockedFlushStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
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
