using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class CoalescingAsyncOperationTests
{
    [Fact]
    public async Task RunAsync_CoalescesOverlappingRequestsIntoOneTrailingRun()
    {
        var firstRunStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRun = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runCount = 0;
        var concurrentRuns = 0;
        var maxConcurrentRuns = 0;

        var operation = new CoalescingAsyncOperation(async () =>
        {
            var currentConcurrentRuns = Interlocked.Increment(ref concurrentRuns);
            UpdateMaximum(ref maxConcurrentRuns, currentConcurrentRuns);
            var currentRun = Interlocked.Increment(ref runCount);

            try
            {
                if (currentRun == 1)
                {
                    firstRunStarted.TrySetResult(null);
                    await releaseFirstRun.Task;
                }
            }
            finally
            {
                Interlocked.Decrement(ref concurrentRuns);
            }
        });

        var firstRequest = operation.RunAsync();
        await firstRunStarted.Task;

        var secondRequest = operation.RunAsync();
        var thirdRequest = operation.RunAsync();

        Assert.Same(firstRequest, secondRequest);
        Assert.Same(firstRequest, thirdRequest);

        releaseFirstRun.TrySetResult(null);
        await Task.WhenAll(firstRequest, secondRequest, thirdRequest);

        Assert.Equal(2, Volatile.Read(ref runCount));
        Assert.Equal(1, Volatile.Read(ref maxConcurrentRuns));
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref maximum);
            if (candidate <= snapshot ||
                Interlocked.CompareExchange(ref maximum, candidate, snapshot) == snapshot)
            {
                return;
            }
        }
    }
}
