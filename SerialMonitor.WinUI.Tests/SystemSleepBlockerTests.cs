using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class SystemSleepBlockerTests
{
    [Fact]
    public void AcquireAndRelease_AreIdempotent()
    {
        using var blocker = new SystemSleepBlocker();

        blocker.Acquire("SerialMonitor unit test");
        blocker.Acquire("SerialMonitor unit test duplicate acquire");
        Assert.True(blocker.IsActive);

        blocker.Release();
        blocker.Release();
        Assert.False(blocker.IsActive);
    }

    [Fact]
    public void ReleaseIfDisconnected_PreservesActiveRequestWhenConnectionRemains()
    {
        using var blocker = new SystemSleepBlocker();
        blocker.Acquire("SerialMonitor connected-state unit test");

        blocker.ReleaseIfDisconnected(() => true);

        Assert.True(blocker.IsActive);

        blocker.ReleaseIfDisconnected(() => false);
        Assert.False(blocker.IsActive);
    }

    [Fact]
    public void ConnectionStateSequence_ReleasesOnFault_ReacquiresAndIgnoresStaleDisconnectCallback()
    {
        using var blocker = new SystemSleepBlocker();

        blocker.Acquire("initial connected session");
        Assert.True(blocker.IsActive);

        blocker.ReleaseIfDisconnected(() => false);
        Assert.False(blocker.IsActive);

        blocker.Acquire("reconnected session");
        blocker.ReleaseIfDisconnected(() => true);
        Assert.True(blocker.IsActive);
    }

    [Fact]
    public async Task StaleDisconnectCheck_ContendingAcquire_PreservesNewConnectedSession()
    {
        using var blocker = new SystemSleepBlocker();
        using var predicateEntered = new ManualResetEventSlim(initialState: false);
        using var allowPredicateToFinish = new ManualResetEventSlim(initialState: false);
        var acquireStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var isConnected = 0;
        blocker.Acquire("initial connected session");

        var staleDisconnectTask = Task.Run(() =>
            blocker.ReleaseIfDisconnected(() =>
            {
                predicateEntered.Set();
                allowPredicateToFinish.Wait();
                return Volatile.Read(ref isConnected) != 0;
            }));

        Task? acquireTask = null;
        try
        {
            Assert.True(predicateEntered.Wait(TimeSpan.FromSeconds(2)));
            Volatile.Write(ref isConnected, 1);
            acquireTask = Task.Run(() =>
            {
                acquireStarted.TrySetResult();
                blocker.Acquire("new connected session");
            });

            await acquireStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(acquireTask.IsCompleted);
        }
        finally
        {
            Volatile.Write(ref isConnected, 1);
            allowPredicateToFinish.Set();
            await staleDisconnectTask.WaitAsync(TimeSpan.FromSeconds(2));
            if (acquireTask is not null)
            {
                await acquireTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }

        Assert.True(blocker.IsActive);
    }
}
