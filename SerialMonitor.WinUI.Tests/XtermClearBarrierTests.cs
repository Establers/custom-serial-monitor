using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class XtermClearBarrierTests
{
    [Fact]
    public async Task DelayedClear_BlocksInjectedBatchUntilMatchingClearCompletes()
    {
        var barrier = new XtermClearBarrier();
        var allowClearToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clearGeneration = 7L;
        var queuedBatchCount = 0;

        barrier.Begin(clearGeneration);
        var clearTask = Task.Run(async () =>
        {
            await allowClearToFinish.Task;
            return barrier.TryComplete(clearGeneration);
        });

        queuedBatchCount++;
        Assert.False(barrier.ShouldStartPump(
            pumpRunning: false,
            recoveryPending: false,
            queuedBatchCount));

        allowClearToFinish.SetResult();
        Assert.True(await clearTask);
        Assert.True(barrier.ShouldStartPump(
            pumpRunning: false,
            recoveryPending: false,
            queuedBatchCount));
    }

    [Fact]
    public void OlderClearCannotReleaseNewerClearBarrier()
    {
        var barrier = new XtermClearBarrier();
        barrier.Begin(10);
        barrier.Begin(11);

        Assert.False(barrier.TryComplete(10));
        Assert.True(barrier.IsPending);
        Assert.False(barrier.ShouldStartPump(false, false, queuedBatchCount: 1));

        Assert.True(barrier.TryComplete(11));
        Assert.False(barrier.IsPending);
        Assert.True(barrier.ShouldStartPump(false, false, queuedBatchCount: 1));
    }
}
