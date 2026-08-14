using System.Collections.Concurrent;
using System.Diagnostics;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

[Collection(StabilityIsolationCollection.Name)]
public sealed class BoundedDiagnosticWriterTests
{
    [Fact]
    public async Task BlockingSink_KeepsInflightAndQueuedWorkWithinCapacity()
    {
        using var releaseSink = new ManualResetEventSlim(initialState: false);
        var sinkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var writer = new BoundedDiagnosticWriter(
            capacity: 4,
            (_, _) =>
            {
                sinkStarted.TrySetResult();
                releaseSink.Wait();
            });

        Assert.True(writer.TryEnqueue(Work("first")));
        await sinkStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            var stopwatch = Stopwatch.StartNew();
            for (var index = 0; index < 10_000; index++)
            {
                writer.TryEnqueue(Work(index.ToString()));
            }

            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
            Assert.InRange(writer.PendingWorkCount, 1, writer.Capacity);
            Assert.True(writer.DroppedWorkCount > 0);
        }
        finally
        {
            releaseSink.Set();
        }

        Assert.True(await writer.CompleteAndDrainAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ThrowingSink_DoesNotKillPumpAndReportsBoundedDropSummary()
    {
        var entries = new ConcurrentQueue<(string Text, long Dropped)>();
        var successful = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var writer = new BoundedDiagnosticWriter(
            capacity: 4,
            (work, dropped) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new IOException("Injected diagnostic sink failure.");
                }

                entries.Enqueue((work.Text, dropped));
                successful.TrySetResult();
            });

        Assert.True(writer.TryEnqueue(Work("fails")));
        Assert.True(writer.TryEnqueue(Work("survives")));
        await successful.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains(entries, entry => entry.Text == "survives");
        Assert.Equal(1, writer.SinkErrorCount);
    }

    [Fact]
    public async Task CompleteAndDrain_IsIdempotentAndBoundedWhenSinkBlocks()
    {
        using var sinkStarted = new ManualResetEventSlim(initialState: false);
        using var releaseSink = new ManualResetEventSlim(initialState: false);
        await using var writer = new BoundedDiagnosticWriter(
            capacity: 2,
            (_, _) =>
            {
                sinkStarted.Set();
                releaseSink.Wait();
            });

        Assert.True(writer.TryEnqueue(Work("last")));
        Assert.True(sinkStarted.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            Assert.False(await writer.CompleteAndDrainAsync(TimeSpan.FromMilliseconds(50)));
            Assert.False(writer.TryEnqueue(Work("after complete")));
            Assert.Equal(1, writer.PendingWorkCount);
        }
        finally
        {
            releaseSink.Set();
        }

        Assert.True(await writer.CompleteAndDrainAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, writer.PendingWorkCount);
    }

    [Fact]
    public async Task FlushBarrier_CompletesAfterPriorWorkAndKeepsSessionOpen()
    {
        var processed = new ConcurrentQueue<string>();
        using var firstStarted = new ManualResetEventSlim(initialState: false);
        using var releaseFirst = new ManualResetEventSlim(initialState: false);
        var afterFlushProcessed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var writer = new BoundedDiagnosticWriter(
            capacity: 4,
            (work, _) =>
            {
                processed.Enqueue(work.Text);
                if (work.Text == "first")
                {
                    firstStarted.Set();
                    releaseFirst.Wait();
                }
                else if (work.Text == "after flush")
                {
                    afterFlushProcessed.TrySetResult();
                }
            });

        Assert.True(writer.TryEnqueue(Work("first")));
        Assert.True(writer.TryEnqueue(Work("second")));
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(2)));
        var flushTask = writer.FlushAsync(TimeSpan.FromSeconds(2));
        try
        {
            await Task.Delay(30);
            Assert.False(flushTask.IsCompleted);
        }
        finally
        {
            releaseFirst.Set();
        }

        Assert.True(await flushTask!);
        Assert.Equal(["first", "second"], processed.ToArray());

        Assert.True(writer.TryEnqueue(Work("after flush")));
        await afterFlushProcessed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(await writer.CompleteAndDrainAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task FlushBarrier_BlockingOrFullSinkReturnsWithinBoundAndLaterRecovers()
    {
        using var sinkStarted = new ManualResetEventSlim(initialState: false);
        using var releaseSink = new ManualResetEventSlim(initialState: false);
        await using var writer = new BoundedDiagnosticWriter(
            capacity: 2,
            (_, _) =>
            {
                sinkStarted.Set();
                releaseSink.Wait();
            });

        Assert.True(writer.TryEnqueue(Work("blocked")));
        Assert.True(sinkStarted.Wait(TimeSpan.FromSeconds(2)));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Assert.False(await writer.FlushAsync(TimeSpan.FromMilliseconds(50)));
            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));

            // The timed-out barrier occupies the second and final bounded slot.
            Assert.False(await writer.FlushAsync(TimeSpan.FromMilliseconds(50)));
            Assert.Equal(writer.Capacity, writer.PendingWorkCount);
        }
        finally
        {
            releaseSink.Set();
        }

        Assert.True(SpinWait.SpinUntil(
            () => writer.PendingWorkCount == 0,
            TimeSpan.FromSeconds(2)));
        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(2)));
        Assert.True(writer.TryEnqueue(Work("still open")));
        Assert.True(await writer.CompleteAndDrainAsync(TimeSpan.FromSeconds(2)));
        Assert.False(await writer.FlushAsync(TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task FlushBarrier_FullQueueWaitsForSlotThenPreservesFifoBeforeDeadline()
    {
        var processed = new ConcurrentQueue<string>();
        using var firstStarted = new ManualResetEventSlim(initialState: false);
        using var releaseFirst = new ManualResetEventSlim(initialState: false);
        await using var writer = new BoundedDiagnosticWriter(
            capacity: 2,
            (work, _) =>
            {
                processed.Enqueue(work.Text);
                if (work.Text == "first")
                {
                    firstStarted.Set();
                    releaseFirst.Wait();
                }
            });

        Assert.True(writer.TryEnqueue(Work("first")));
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(writer.TryEnqueue(Work("second")));
        Assert.Equal(writer.Capacity, writer.PendingWorkCount);

        var flushTask = writer.FlushAsync(TimeSpan.FromSeconds(2));
        try
        {
            await Task.Delay(30);
            Assert.False(flushTask.IsCompleted);
            Assert.Equal(writer.Capacity, writer.PendingWorkCount);
        }
        finally
        {
            releaseFirst.Set();
        }

        Assert.True(await flushTask);
        Assert.Equal(["first", "second"], processed.ToArray());
        Assert.Equal(0, writer.PendingWorkCount);

        Assert.True(writer.TryEnqueue(Work("after flush")));
        Assert.True(await writer.CompleteAndDrainAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task FlushBarrier_FullPermanentBlockTimesOutWithoutOrphanWaiterOrSlot()
    {
        using var sinkStarted = new ManualResetEventSlim(initialState: false);
        using var releaseSink = new ManualResetEventSlim(initialState: false);
        await using var writer = new BoundedDiagnosticWriter(
            capacity: 1,
            (_, _) =>
            {
                sinkStarted.Set();
                releaseSink.Wait();
            });

        Assert.True(writer.TryEnqueue(Work("blocked")));
        Assert.True(sinkStarted.Wait(TimeSpan.FromSeconds(2)));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Assert.False(await writer.FlushAsync(TimeSpan.FromMilliseconds(50)));
            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.Equal(1, writer.PendingWorkCount);
        }
        finally
        {
            releaseSink.Set();
        }

        Assert.True(SpinWait.SpinUntil(
            () => writer.PendingWorkCount == 0,
            TimeSpan.FromSeconds(2)));
        Assert.True(writer.TryEnqueue(Work("slot recovered")));
        Assert.True(await writer.CompleteAndDrainAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task FlushBarrier_CompleteRaceCancelsSlotWaitWithoutLeak()
    {
        using var sinkStarted = new ManualResetEventSlim(initialState: false);
        using var releaseSink = new ManualResetEventSlim(initialState: false);
        await using var writer = new BoundedDiagnosticWriter(
            capacity: 1,
            (_, _) =>
            {
                sinkStarted.Set();
                releaseSink.Wait();
            });

        Assert.True(writer.TryEnqueue(Work("blocked")));
        Assert.True(sinkStarted.Wait(TimeSpan.FromSeconds(2)));
        var flushTask = writer.FlushAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Task.Delay(30);
            Assert.False(flushTask.IsCompleted);
            Assert.False(await writer.CompleteAndDrainAsync(TimeSpan.FromMilliseconds(50)));
            Assert.False(await flushTask.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(1, writer.PendingWorkCount);
            Assert.False(writer.TryEnqueue(Work("after complete")));
        }
        finally
        {
            releaseSink.Set();
        }

        Assert.True(await writer.CompleteAndDrainAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, writer.PendingWorkCount);
        Assert.False(writer.TryEnqueue(Work("still complete")));
    }

    [Fact]
    public async Task FlushBarrier_SinkFailureDoesNotPreventFollowingBarrierOrThrow()
    {
        var calls = 0;
        await using var writer = new BoundedDiagnosticWriter(
            capacity: 3,
            (_, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new IOException("Injected sink failure.");
                }
            });

        Assert.True(writer.TryEnqueue(Work("fails")));
        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, writer.SinkErrorCount);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.False(await writer.FlushAsync(TimeSpan.FromSeconds(1), canceled.Token));
    }

    [Fact]
    public async Task StagedShutdown_FullQueueWaitsForCapacityAndCompletesInFifoOrder()
    {
        var processed = new ConcurrentQueue<string>();
        using var firstStarted = new ManualResetEventSlim(initialState: false);
        using var releaseFirst = new ManualResetEventSlim(initialState: false);
        await using var writer = new BoundedDiagnosticWriter(
            capacity: 2,
            (work, _) =>
            {
                processed.Enqueue(work.Text);
                if (work.Text == "first")
                {
                    firstStarted.Set();
                    releaseFirst.Wait();
                }
            });

        Assert.True(writer.TryEnqueue(Work("first")));
        Task<bool>? flushTask = null;
        try
        {
            Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(writer.TryEnqueue(Work("second")));
            Assert.True(writer.TryStageCriticalWork(ShutdownWork("shutdown")));
            flushTask = writer.FlushAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(30);
            Assert.False(flushTask.IsCompleted);
            Assert.Equal(writer.Capacity, writer.PendingWorkCount);
        }
        finally
        {
            releaseFirst.Set();
        }

        Assert.NotNull(flushTask);
        Assert.True(await flushTask!);
        Assert.Equal(["first", "second", "shutdown"], processed.ToArray());
        Assert.False(writer.HasStagedCriticalWork);

        Assert.True(writer.TryEnqueue(Work("after shutdown flush")));
        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task StagedShutdown_FullPermanentBlockTimesOutWithOneBoundedRetrySlot()
    {
        using var sinkStarted = new ManualResetEventSlim(initialState: false);
        using var releaseSink = new ManualResetEventSlim(initialState: false);
        var processed = new ConcurrentQueue<string>();
        await using var writer = new BoundedDiagnosticWriter(
            capacity: 1,
            (work, _) =>
            {
                processed.Enqueue(work.Text);
                if (work.Text == "blocked")
                {
                    sinkStarted.Set();
                    releaseSink.Wait();
                }
            });

        Assert.True(writer.TryEnqueue(Work("blocked")));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Assert.True(sinkStarted.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(writer.TryStageCriticalWork(ShutdownWork("shutdown")));
            Assert.False(await writer.FlushAsync(TimeSpan.FromMilliseconds(50)));
            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.Equal(writer.Capacity, writer.PendingWorkCount);
            Assert.True(writer.HasStagedCriticalWork);
        }
        finally
        {
            releaseSink.Set();
        }

        Assert.True(SpinWait.SpinUntil(
            () => writer.PendingWorkCount == 0,
            TimeSpan.FromSeconds(2)));
        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(["blocked", "shutdown"], processed.ToArray());
    }

    [Fact]
    public async Task StagedShutdown_TimeoutAfterEnqueueDoesNotPermitDuplicate()
    {
        using var firstStarted = new ManualResetEventSlim(initialState: false);
        using var releaseFirst = new ManualResetEventSlim(initialState: false);
        var processed = new ConcurrentQueue<string>();
        await using var writer = new BoundedDiagnosticWriter(
            capacity: 2,
            (work, _) =>
            {
                processed.Enqueue(work.Text);
                if (work.Text == "blocked")
                {
                    firstStarted.Set();
                    releaseFirst.Wait();
                }
            });

        Assert.True(writer.TryEnqueue(Work("blocked")));
        try
        {
            Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(writer.TryStageCriticalWork(ShutdownWork("shutdown")));
            Assert.False(await writer.FlushAsync(TimeSpan.FromMilliseconds(50)));
            Assert.Equal(2, writer.PendingWorkCount);
            Assert.False(writer.TryStageCriticalWork(ShutdownWork("duplicate")));
        }
        finally
        {
            releaseFirst.Set();
        }

        Assert.True(SpinWait.SpinUntil(
            () => processed.Count == 2 && writer.PendingWorkCount == 0,
            TimeSpan.FromSeconds(2)));
        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, processed.Count(text => text == "shutdown"));
        Assert.DoesNotContain("duplicate", processed);
    }

    [Fact]
    public async Task StagedShutdown_SinkFailureIsObservedOnceAndPumpRemainsUsable()
    {
        var processed = new ConcurrentQueue<string>();
        await using var writer = new BoundedDiagnosticWriter(
            capacity: 3,
            (work, _) =>
            {
                processed.Enqueue(work.Text);
                if (work.Kind == GeneralDiagnosticWorkKind.Shutdown)
                {
                    throw new IOException("Injected shutdown sink failure.");
                }
            });

        Assert.True(writer.TryStageCriticalWork(ShutdownWork("shutdown")));
        Assert.False(await writer.FlushAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, writer.SinkErrorCount);
        Assert.False(writer.TryStageCriticalWork(ShutdownWork("duplicate")));

        Assert.True(writer.TryEnqueue(Work("survives")));
        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, processed.Count(text => text == "shutdown"));
        Assert.Contains("survives", processed);
    }

    [Fact]
    public async Task StagedShutdown_CompleteRaceCancelsCapacityWaitWithoutLeak()
    {
        using var sinkStarted = new ManualResetEventSlim(initialState: false);
        using var releaseSink = new ManualResetEventSlim(initialState: false);
        var processed = new ConcurrentQueue<string>();
        await using var writer = new BoundedDiagnosticWriter(
            capacity: 1,
            (work, _) =>
            {
                processed.Enqueue(work.Text);
                sinkStarted.Set();
                releaseSink.Wait();
            });

        Assert.True(writer.TryEnqueue(Work("blocked")));
        Task<bool>? flushTask = null;
        try
        {
            Assert.True(sinkStarted.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(writer.TryStageCriticalWork(ShutdownWork("shutdown")));
            flushTask = writer.FlushAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(30);
            Assert.False(await writer.CompleteAndDrainAsync(TimeSpan.FromMilliseconds(50)));
            Assert.False(await flushTask.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.False(writer.HasStagedCriticalWork);
            Assert.Equal(1, writer.PendingWorkCount);
        }
        finally
        {
            releaseSink.Set();
        }

        Assert.NotNull(flushTask);
        Assert.True(await writer.CompleteAndDrainAsync(TimeSpan.FromSeconds(2)));
        Assert.DoesNotContain("shutdown", processed);
        Assert.Equal(0, writer.PendingWorkCount);
    }

    [Fact]
    public async Task StagedShutdown_LatestPreFlushValueWinsButInFlightReplacementIsRejected()
    {
        var processed = new ConcurrentQueue<string>();
        await using var writer = new BoundedDiagnosticWriter(
            capacity: 2,
            (work, _) => processed.Enqueue(work.Text));

        Assert.True(writer.TryStageCriticalWork(ShutdownWork("older")));
        Assert.True(writer.TryStageCriticalWork(ShutdownWork("latest")));
        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(["latest"], processed.ToArray());
        Assert.False(writer.TryStageCriticalWork(ShutdownWork("after accepted")));
        Assert.True(writer.TryEnqueue(Work("session remains open")));
        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void PublicDiagnosticEntryPoints_NeverThrowWhenInactiveOrFormattingFails()
    {
        var exception = new ThrowingToStringException();

        var failure = Record.Exception(() =>
        {
            RuntimeDiagnostics.RecordStartup();
            RuntimeDiagnostics.RecordError("test", exception);
            RuntimeDiagnostics.RecordShutdown("shutdown");
            RuntimeDiagnostics.ClearLastError();
            _ = RuntimeDiagnostics.ReadLastError();
            _ = RuntimeDiagnostics.ReadLastShutdown();
        });

        Assert.Null(failure);
    }

    [Fact]
    public async Task AsyncRelayCommand_ExceptionPathRecoversAfterNonThrowingDiagnosticCall()
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changeCount = 0;
        var command = new AsyncRelayCommand(() =>
            Task.FromException(new IOException("Injected command failure.")));
        command.CanExecuteChanged += (_, _) =>
        {
            if (Interlocked.Increment(ref changeCount) >= 2)
            {
                settled.TrySetResult();
            }
        };

        command.Execute(null);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(command.CanExecute(null));
    }

    private static GeneralDiagnosticWork Work(string text) => new(
        GeneralDiagnosticWorkKind.Error,
        text,
        DateTimeOffset.UtcNow);

    private static GeneralDiagnosticWork ShutdownWork(string text) => new(
        GeneralDiagnosticWorkKind.Shutdown,
        text,
        DateTimeOffset.UtcNow);

    private sealed class ThrowingToStringException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("formatting failed");
    }
}
