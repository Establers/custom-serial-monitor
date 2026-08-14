using System.Diagnostics;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

[Collection(StabilityIsolationCollection.Name)]
public sealed class BoundedEmergencyDiagnosticWriterTests
{
    [Fact]
    public void BlockingSink_HasHardCallerDeadlineAndOneOutstandingOperation()
    {
        using var sinkStarted = new ManualResetEventSlim(initialState: false);
        using var releaseSink = new ManualResetEventSlim(initialState: false);
        var sinkCalls = 0;
        var writer = new BoundedEmergencyDiagnosticWriter(_ =>
        {
            Interlocked.Increment(ref sinkCalls);
            sinkStarted.Set();
            releaseSink.Wait();
        });

        var stopwatch = Stopwatch.StartNew();
        try
        {
            Assert.False(writer.TryWrite(Work("first"), TimeSpan.FromMilliseconds(50)));
            stopwatch.Stop();
            Assert.True(sinkStarted.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.Equal(1, writer.OutstandingOperationCount);

            Parallel.For(0, 1_000, index =>
            {
                Assert.False(writer.TryWrite(
                    Work($"concurrent {index}"),
                    TimeSpan.FromMilliseconds(50)));
            });

            Assert.Equal(1, writer.OutstandingOperationCount);
            Assert.Equal(1, Volatile.Read(ref sinkCalls));
            Assert.True(writer.RejectedWriteCount >= 1_000);
        }
        finally
        {
            releaseSink.Set();
        }

        Assert.True(SpinWait.SpinUntil(
            () => writer.OutstandingOperationCount == 0,
            TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void LateCompletion_ReleasesSlotAndNextFatalWriteRuns()
    {
        using var releaseFirst = new ManualResetEventSlim(initialState: false);
        var calls = 0;
        var writer = new BoundedEmergencyDiagnosticWriter(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                releaseFirst.Wait();
            }
        });

        try
        {
            Assert.False(writer.TryWrite(Work("late"), TimeSpan.FromMilliseconds(30)));
            Assert.Equal(1, writer.OutstandingOperationCount);
        }
        finally
        {
            releaseFirst.Set();
        }

        Assert.True(SpinWait.SpinUntil(
            () => writer.OutstandingOperationCount == 0,
            TimeSpan.FromSeconds(2)));
        Assert.True(writer.TryWrite(Work("next"), TimeSpan.FromSeconds(1)));
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Equal(0, writer.OutstandingOperationCount);
    }

    [Fact]
    public void ThrowingSink_IsObservedAndDoesNotPoisonSingleSlot()
    {
        var calls = 0;
        var writer = new BoundedEmergencyDiagnosticWriter(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new IOException("Injected emergency sink failure.");
            }
        });

        var exception = Record.Exception(() =>
        {
            Assert.False(writer.TryWrite(Work("throws"), TimeSpan.FromSeconds(1)));
            Assert.True(writer.TryWrite(Work("survives"), TimeSpan.FromSeconds(1)));
        });

        Assert.Null(exception);
        Assert.Equal(1, writer.SinkErrorCount);
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Equal(0, writer.OutstandingOperationCount);
    }

    private static FatalDiagnosticWork Work(string source) =>
        new(source, new InvalidOperationException(source), DateTimeOffset.UtcNow);
}
