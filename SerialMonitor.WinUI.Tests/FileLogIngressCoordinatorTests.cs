using System.Diagnostics;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class FileLogIngressCoordinatorTests
{
    [Fact]
    public async Task ConnectedManualStart_BarrierExcludesBeforeActivationAndRoutesFirstLineAfter()
    {
        var coordinator = new FileLogIngressCoordinator();
        var startEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueueCallCount = 0;
        var durableLineCount = 0;
        var droppedLineCount = 7L;
        var writerState = FileLogWriterState.Stopped;
        var viewPause = new ViewPauseStateMachine();

        Assert.True(coordinator.BeginRequest(droppedLineCount, fileErrorCount: 2));
        var startTask = coordinator.StartAndActivateAsync(
            async _ =>
            {
                writerState = FileLogWriterState.Starting;
                startEntered.TrySetResult(true);
                await releaseStart.Task;
                writerState = FileLogWriterState.Running;
                return true;
            },
            CancellationToken.None);

        try
        {
            await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(FileLogIngressState.Armed, coordinator.State);
            Assert.Equal(FileLogWriterState.Starting, writerState);
            Assert.False(OfferThroughFanOut());
            Assert.Equal(0, enqueueCallCount);
            Assert.Equal(0, coordinator.GetDroppedLineCountSinceRequest(droppedLineCount));

            releaseStart.TrySetResult(true);
            await startTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(FileLogIngressState.Active, coordinator.State);
            Assert.True(OfferThroughFanOut());
            Assert.Equal(1, enqueueCallCount);
            Assert.Equal(1, durableLineCount);
            Assert.Equal(0, coordinator.GetDroppedLineCountSinceRequest(droppedLineCount));

            Assert.False(OfferThroughFanOut(accept: false));
            Assert.Equal(2, enqueueCallCount);
            Assert.Equal(1, coordinator.GetDroppedLineCountSinceRequest(droppedLineCount));
        }
        finally
        {
            releaseStart.TrySetResult(true);
            await startTask.WaitAsync(TimeSpan.FromSeconds(2));
        }

        bool OfferThroughFanOut(bool accept = true)
        {
            var decision = viewPause.ClassifyRecord(
                fileEligible: true,
                fileLoggingEnabled: coordinator.IsActive,
                fileLoggingWhileViewPaused: true);
            if (!coordinator.ShouldOfferToWriter(decision.EnqueueFile))
            {
                return false;
            }

            enqueueCallCount++;
            if (writerState == FileLogWriterState.Running && accept)
            {
                durableLineCount++;
                return true;
            }

            droppedLineCount++;
            return false;
        }
    }

    [Fact]
    public void InitialStartFailure_RemainsRequestedAndPreservesPreActivationHealthBaseline()
    {
        var coordinator = new FileLogIngressCoordinator();
        var droppedLineCount = 40L;
        var fileErrorCount = 5L;

        Assert.True(coordinator.BeginRequest(droppedLineCount, fileErrorCount));
        fileErrorCount++;

        Assert.True(coordinator.IsRequested);
        Assert.False(coordinator.IsActive);
        Assert.Equal(FileLogIngressState.Armed, coordinator.State);
        Assert.False(coordinator.ShouldOfferToWriter(fileEligible: true));
        Assert.Equal(1, coordinator.GetFileErrorCountSinceRequest(fileErrorCount));

        Assert.False(coordinator.BeginRequest(droppedLineCount: 999, fileErrorCount: 999));
        Assert.True(coordinator.ActivateAfterSuccessfulStart());

        Assert.True(coordinator.IsActive);
        Assert.True(coordinator.ShouldOfferToWriter(fileEligible: true));
        Assert.Equal(1, coordinator.GetFileErrorCountSinceRequest(fileErrorCount));
    }

    [Fact]
    public async Task FailedStart_DoesNotActivateOrResetRequestBaselines()
    {
        var coordinator = new FileLogIngressCoordinator();
        Assert.True(coordinator.BeginRequest(droppedLineCount: 3, fileErrorCount: 8));

        var activated = await coordinator.StartAndActivateAsync(
            _ => Task.FromResult(false),
            CancellationToken.None);

        Assert.False(activated);
        Assert.Equal(FileLogIngressState.Armed, coordinator.State);
        Assert.True(coordinator.IsRequested);
        Assert.False(coordinator.IsActive);
        Assert.False(coordinator.ShouldOfferToWriter(fileEligible: true));
        Assert.Equal(2, coordinator.GetFileErrorCountSinceRequest(fileErrorCount: 10));
    }

    [Fact]
    public async Task ThrowingStart_LeavesRequestArmedForBoundedAutoRestart()
    {
        var coordinator = new FileLogIngressCoordinator();
        Assert.True(coordinator.BeginRequest(droppedLineCount: 12, fileErrorCount: 4));

        await Assert.ThrowsAsync<IOException>(() => coordinator.StartAndActivateAsync(
            _ => throw new IOException("injected start failure"),
            CancellationToken.None));

        Assert.Equal(FileLogIngressState.Armed, coordinator.State);
        Assert.True(coordinator.IsRequested);
        Assert.False(coordinator.ShouldOfferToWriter(fileEligible: true));

        Assert.True(await coordinator.StartAndActivateAsync(
            _ => Task.FromResult(true),
            CancellationToken.None));
        Assert.Equal(FileLogIngressState.Active, coordinator.State);
    }

    [Fact]
    public void DisconnectedRequest_ArmsUntilConnectStartsWriterBeforeRx()
    {
        var coordinator = new FileLogIngressCoordinator();
        var rxStarted = false;
        var enqueueCallCount = 0;

        Assert.True(coordinator.BeginRequest(droppedLineCount: 0, fileErrorCount: 0));
        Assert.Equal(FileLogIngressState.Armed, coordinator.State);
        Assert.False(coordinator.ShouldOfferToWriter(fileEligible: true));

        Assert.True(coordinator.ActivateAfterSuccessfulStart());
        Assert.False(rxStarted);
        rxStarted = true;

        if (coordinator.ShouldOfferToWriter(fileEligible: true))
        {
            enqueueCallCount++;
        }

        Assert.True(rxStarted);
        Assert.Equal(1, enqueueCallCount);
    }

    [Fact]
    public void RetryableFaultAfterActivation_KeepsIngressAndDropAccountingUntilRestart()
    {
        var coordinator = new FileLogIngressCoordinator();
        var droppedLineCount = 11L;
        var enqueueCallCount = 0;

        Assert.True(coordinator.BeginRequest(droppedLineCount, fileErrorCount: 0));
        Assert.True(coordinator.ActivateAfterSuccessfulStart());

        if (coordinator.ShouldOfferToWriter(fileEligible: true))
        {
            enqueueCallCount++;
            droppedLineCount++;
        }

        Assert.True(coordinator.IsRequested);
        Assert.True(coordinator.IsActive);
        Assert.Equal(1, enqueueCallCount);
        Assert.Equal(1, coordinator.GetDroppedLineCountSinceRequest(droppedLineCount));

        Assert.True(coordinator.ActivateAfterSuccessfulStart());
        Assert.True(coordinator.ShouldOfferToWriter(fileEligible: true));
        Assert.Equal(1, coordinator.GetDroppedLineCountSinceRequest(droppedLineCount));
    }

    [Fact]
    public async Task InitialRetryableFault_AutoRestartActivatesWithoutManualToggle()
    {
        var ingress = new FileLogIngressCoordinator();
        var faulted = 1;
        var restartCount = 0;
        var delayEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(ingress.BeginRequest(droppedLineCount: 20, fileErrorCount: 3));
        await using var restart = new FileLogAutoRestartCoordinator(
            () => ingress.IsRequested && Volatile.Read(ref faulted) != 0,
            async token =>
            {
                Interlocked.Increment(ref restartCount);
                var activated = await ingress.StartAndActivateAsync(
                    _ => Task.FromResult(true),
                    token);
                Volatile.Write(ref faulted, 0);
                return activated;
            },
            initialDelay: TimeSpan.FromMilliseconds(1),
            maximumDelay: TimeSpan.FromMilliseconds(1),
            maximumConsecutiveAttempts: 2,
            delay: (_, token) =>
            {
                delayEntered.TrySetResult(true);
                return releaseDelay.Task.WaitAsync(token);
            });

        try
        {
            Assert.True(restart.RequestRetry());
            await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(ingress.IsRequested);
            Assert.False(ingress.IsActive);

            releaseDelay.TrySetResult(true);
            await WaitUntilAsync(() => ingress.IsActive && !restart.IsRetrying, TimeSpan.FromSeconds(2));

            Assert.Equal(1, restartCount);
            Assert.True(ingress.IsRequested);
            Assert.True(ingress.ShouldOfferToWriter(fileEligible: true));
        }
        finally
        {
            releaseDelay.TrySetResult(true);
            await restart.CancelAsync(resetAttempts: true);
        }
    }

    [Theory]
    [InlineData("manual OFF")]
    [InlineData("shutdown")]
    public void EndRequest_ClosesIngressAndPreventsFurtherWriterOffers(string reason)
    {
        _ = reason;
        var coordinator = new FileLogIngressCoordinator();

        Assert.True(coordinator.BeginRequest(droppedLineCount: 0, fileErrorCount: 0));
        Assert.True(coordinator.ActivateAfterSuccessfulStart());
        Assert.True(coordinator.EndRequest());

        Assert.Equal(FileLogIngressState.Off, coordinator.State);
        Assert.False(coordinator.IsRequested);
        Assert.False(coordinator.IsActive);
        Assert.False(coordinator.ShouldOfferToWriter(fileEligible: true));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException("The expected file-ingress state was not reached.");
            }

            await Task.Delay(10);
        }
    }
}
