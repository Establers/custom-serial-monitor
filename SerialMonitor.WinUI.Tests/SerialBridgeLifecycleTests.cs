using System.Reflection;
using RJCP.IO.Ports;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialBridgeLifecycleTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedPortCleanupReturnsATaskAndRemainsTrackedAfterWaitCancellation(bool faultFirst)
    {
        await using var bridge = new SerialBridgeService();
        var port = new DelayedDisposePort();
        var cancellation = new CancellationTokenSource();
        Set(bridge, "_virtualPort", port);
        Set(bridge, "_cancellation", cancellation);
        Set(bridge, "_isRunning", true);
        using var stop = new CancellationTokenSource();
        var callerThread = 0;
        // A dedicated calling thread lets the test detect StopAsync blocking
        // before it even returns a Task, without hanging the test runner.
        var invocation = Task.Factory.StartNew(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            if (faultFirst) Invoke(bridge, "FaultBridge", "Injected bridge fault");
            return bridge.StopAsync(stop.Token);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await port.Entered.Task.WaitAsync(Deadline);
            var stopping = await invocation.WaitAsync(Deadline);
            Assert.NotEqual(callerThread, port.DisposeThread);
            Assert.False(stopping.IsCompleted);
            Assert.False(bridge.IsRunning);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping.WaitAsync(Deadline));

            var shutdown = Get(bridge, "_shutdownTask");
            Assert.NotNull(shutdown);
            Assert.Same(port, Get(bridge, "_virtualPort"));
            Assert.Same(cancellation, Get(bridge, "_cancellation"));
            Assert.True(cancellation.IsCancellationRequested);
            _ = cancellation.Token; // Still owned by the cleanup, not disposed by its canceled waiter.

            using var secondStop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                bridge.StopAsync(secondStop.Token).WaitAsync(Deadline));
            Assert.Same(shutdown, Get(bridge, "_shutdownTask"));
            await AssertRestartWaitsForCleanupAsync(bridge);
        }
        finally
        {
            port.Release.Set();
            await Record.ExceptionAsync(() => invocation.Unwrap().WaitAsync(Deadline));
            await bridge.StopAsync(default).WaitAsync(Deadline);
        }

        Assert.Equal(1, port.DisposeCalls);
        Assert.Null(Get(bridge, "_shutdownTask"));
        Assert.Null(Get(bridge, "_virtualPort"));
        Assert.Throws<ObjectDisposedException>(() => { _ = cancellation.Token; });
    }

    [Fact]
    public async Task CanceledStopKeepsTheOldSchedulerUntilItExitsAndTheNextManualTxCanSend()
    {
        await using var bridge = new SerialBridgeService();
        var cancellation = StartScheduler(bridge);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manual = bridge.QueueManualTransmitAsync(async _ =>
        {
            entered.SetResult();
            await release.Task; // Simulate a write that is slow to observe cancellation.
        }, default);
        var scheduler = (Task)Get(bridge, "_deviceWriterTask")!;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.StopAsync(stop.Token).WaitAsync(Deadline));
            Assert.Equal(ManualTransmitResult.Canceled, await manual.WaitAsync(Deadline));
            Assert.Same(scheduler, Get(bridge, "_deviceWriterTask"));
            Assert.Same(cancellation, Get(bridge, "_cancellation"));
            Assert.False(scheduler.IsCompleted);
            await AssertRestartWaitsForCleanupAsync(bridge);
        }
        finally
        {
            release.TrySetResult();
            await bridge.StopAsync(default).WaitAsync(Deadline);
        }

        // Start the next isolated run after the barrier, with no physical COM port.
        StartScheduler(bridge);
        var sends = 0;
        var next = bridge.QueueManualTransmitAsync(_ =>
        {
            Interlocked.Increment(ref sends);
            return Task.CompletedTask;
        }, default);
        Assert.Equal(ManualTransmitResult.Sent, await next.WaitAsync(Deadline));
        Assert.Equal(1, sends);
        Assert.Equal(ManualTxState.Idle, bridge.ManualTxState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateManualCompletionCannotOverwriteAnotherRequestOrRunsState(bool replaceRun)
    {
        await using var bridge = new SerialBridgeService();
        using var oldCancellation = StartScheduler(bridge);
        var scheduler = (Task)Get(bridge, "_deviceWriterTask")!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = bridge.QueueManualTransmitAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        }, default);
        using var currentCancellation = new CancellationTokenSource();
        using var nextCancellation = new CancellationTokenSource();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            oldCancellation.Cancel();
            Set(bridge, "_pendingManual", null);
            Set(bridge, "_manualTxState", ManualTxState.Idle);
            if (replaceRun) Set(bridge, "_cancellation", currentCancellation);
            var next = bridge.QueueManualTransmitAsync(_ => Task.CompletedTask, nextCancellation.Token);
            var pending = Get(bridge, "_pendingManual");
            Assert.Equal(ManualTxState.WaitingForBridgeIdle, bridge.ManualTxState);
            if (replaceRun) Set(bridge, "_virtualToDeviceWriteActive", true);

            release.TrySetResult();
            await scheduler.WaitAsync(Deadline);
            await old.WaitAsync(Deadline);
            Assert.Same(pending, Get(bridge, "_pendingManual"));
            Assert.Equal(ManualTxState.WaitingForBridgeIdle, bridge.ManualTxState);
            if (replaceRun) Assert.Equal(true, Get(bridge, "_virtualToDeviceWriteActive"));
            Assert.False(next.IsCompleted);
            nextCancellation.Cancel();
            Assert.Equal(ManualTransmitResult.Canceled, await next.WaitAsync(Deadline));
        }
        finally
        {
            release.TrySetResult();
            await scheduler.WaitAsync(Deadline);
            await bridge.StopAsync(default).WaitAsync(Deadline);
        }
    }

    private static async Task AssertRestartWaitsForCleanupAsync(SerialBridgeService bridge)
    {
        using var restart = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        // Reaching Open on this deliberately invalid name would fail instead of
        // honoring the caller's cancellation while waiting for the old cleanup.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.StartAsync(
            new BridgeSettings { VirtualPortName = "INVALID_TEST_PORT" }, new SerialSettings(),
            (_, _) => Task.CompletedTask, restart.Token).WaitAsync(Deadline));
        Assert.False(bridge.IsRunning);
    }

    private static CancellationTokenSource StartScheduler(SerialBridgeService bridge)
    {
        var cancellation = new CancellationTokenSource();
        Set(bridge, "_cancellation", cancellation);
        Set(bridge, "_isRunning", true);
        Set(bridge, "_manualTxIdleGuardMs", 0);
        var scheduler = (Task)Invoke(bridge, "RunDeviceSchedulerAsync",
            (Func<byte[], CancellationToken, Task>)((_, _) => Task.CompletedTask), cancellation.Token)!;
        Set(bridge, "_deviceWriterTask", scheduler);
        return cancellation;
    }

    private sealed class DelayedDisposePort : SerialPortStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public int DisposeThread { get; private set; }
        public int DisposeCalls;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref DisposeCalls);
                DisposeThread = Environment.CurrentManagedThreadId;
                Entered.TrySetResult();
                Release.Wait();
            }
            base.Dispose(disposing);
        }
    }

    private static object? Get(object target, string name) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void Set(object target, string name, object? value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
