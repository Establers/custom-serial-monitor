using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class LatestRequestAsyncOperationTests
{
    [Fact]
    public async Task RunAsync_ExecutesOneActiveAndOnlyTheLatestPendingRequest()
    {
        var firstRunStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRun = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new List<int>();
        var concurrentRuns = 0;
        var maxConcurrentRuns = 0;
        using var cancellation = new CancellationTokenSource();

        var operation = new LatestRequestAsyncOperation<int>(async (request, _) =>
        {
            var currentConcurrentRuns = Interlocked.Increment(ref concurrentRuns);
            UpdateMaximum(ref maxConcurrentRuns, currentConcurrentRuns);
            processed.Add(request);

            try
            {
                if (request == 1)
                {
                    firstRunStarted.TrySetResult(null);
                    await releaseFirstRun.Task;
                }
            }
            finally
            {
                Interlocked.Decrement(ref concurrentRuns);
            }
        }, cancellation.Token);

        var firstRequest = operation.RunAsync(1);
        await firstRunStarted.Task;

        var secondRequest = operation.RunAsync(2);
        var thirdRequest = operation.RunAsync(3);

        Assert.Same(firstRequest, secondRequest);
        Assert.Same(firstRequest, thirdRequest);

        releaseFirstRun.TrySetResult(null);
        await Task.WhenAll(firstRequest, secondRequest, thirdRequest);

        Assert.Equal([1, 3], processed);
        Assert.Equal(1, Volatile.Read(ref maxConcurrentRuns));
    }

    [Fact]
    public async Task RunAsync_WhenCombinerIsProvided_AccumulatesPendingRequests()
    {
        var firstRunStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRun = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new List<int>();
        using var cancellation = new CancellationTokenSource();

        var operation = new LatestRequestAsyncOperation<int>(async (request, _) =>
        {
            processed.Add(request);
            if (request == 1)
            {
                firstRunStarted.TrySetResult(null);
                await releaseFirstRun.Task;
            }
        }, cancellation.Token, (pending, incoming) => pending + incoming);

        var firstRequest = operation.RunAsync(1);
        await firstRunStarted.Task;
        var secondRequest = operation.RunAsync(1);
        var thirdRequest = operation.RunAsync(1);

        releaseFirstRun.TrySetResult(null);
        await Task.WhenAll(firstRequest, secondRequest, thirdRequest);

        Assert.Equal([1, 2], processed);
    }

    [Fact]
    public async Task RunAsync_CancellationDropsPendingRequestAndCompletesCleanly()
    {
        var firstRunStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new List<int>();
        using var cancellation = new CancellationTokenSource();

        var operation = new LatestRequestAsyncOperation<int>(async (request, cancellationToken) =>
        {
            processed.Add(request);
            firstRunStarted.TrySetResult(null);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }, cancellation.Token);

        var firstRequest = operation.RunAsync(1);
        await firstRunStarted.Task;
        var pendingRequest = operation.RunAsync(2);

        cancellation.Cancel();
        await Task.WhenAll(firstRequest, pendingRequest);

        Assert.Equal([1], processed);
        await operation.RunAsync(3);
        Assert.Equal([1], processed);
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
