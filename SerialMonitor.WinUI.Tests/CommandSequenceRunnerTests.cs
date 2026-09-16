using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class CommandSequenceRunnerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SynchronousZeroDelayLoop_YieldsToQueuedStopOnCallerContext(bool success)
    {
        var previous = SynchronizationContext.Current;
        var context = new PumpContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            using var stop = new CancellationTokenSource();
            var attempts = 0;
            var sequence = Sequence(CommandSequence.MaxRepeatCount);
            var run = new CommandSequenceRunner().RunAsync(sequence,
                _ => Task.FromResult(true),
                (_, _) =>
                {
                    Assert.Same(context, SynchronizationContext.Current);
                    attempts++;
                    return Task.FromResult(success);
                }, () => true, _ => { }, _ => { }, stop.Token);
            Assert.False(run.IsCompleted);
            Assert.InRange(attempts, 0, CommandSequenceRunner.MaxAttemptsPerSlice);
            context.Post(_ => stop.Cancel(), null);
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (!run.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(5)) context.Pump();
            Assert.True(run.IsCanceled);
            Assert.InRange(attempts, 0, 2 * CommandSequenceRunner.MaxAttemptsPerSlice);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class PumpContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add(() => callback(state));
        public void Pump()
        {
            if (_queue.TryTake(out var action, 100)) action();
        }
    }

    [Fact]
    public async Task ReconnectAfterSuccessfulSend_ContinuesAtNextStepAndPreservesRepeats()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reconnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<string>();
        var completed = new List<int>();
        var run = new CommandSequenceRunner().RunAsync(Sequence(repeats: 2),
            async token =>
            {
                if (sent.Count == 1)
                {
                    waiting.TrySetResult();
                    return await reconnect.Task.WaitAsync(token);
                }
                return true;
            },
            (step, _) => { sent.Add(step.CommandText); return Task.FromResult(true); },
            () => true, _ => { }, completed.Add, timeout.Token);

        await waiting.Task.WaitAsync(timeout.Token);
        Assert.Equal(new[] { "A" }, sent);
        Assert.Equal(new[] { 1 }, completed);
        Assert.False(run.IsCompleted);
        reconnect.SetResult(true);
        await run.WaitAsync(timeout.Token);
        Assert.Equal(new[] { "A", "B", "A", "B" }, sent);
        Assert.Equal(new[] { 1, 2, 3, 4 }, completed);
    }

    [Fact]
    public async Task RepeatedDisconnects_RetryOnlyFailedStepWithoutAdvancingProgress()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var attempts = 0;
        var waits = 0;
        var completed = new List<int>();
        await new CommandSequenceRunner().RunAsync(Sequence(),
            async token => { waits++; await Task.Yield(); token.ThrowIfCancellationRequested(); return true; },
            (step, _) =>
            {
                attempts++;
                if (attempts <= 1000)
                {
                    Assert.Equal("A", step.CommandText);
                    Assert.Empty(completed);
                    return Task.FromResult(false);
                }
                return Task.FromResult(true);
            },
            () => true, _ => { }, completed.Add, timeout.Token);
        Assert.Equal(1002, attempts);
        Assert.Equal(attempts, waits);
        Assert.Equal(new[] { 1, 2 }, completed);
    }

    [Fact]
    public async Task StopWhileWaitingForReconnect_PreventsLaterResume()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        var run = new CommandSequenceRunner().RunAsync(Sequence(),
            async token => { waiting.SetResult(); return await reconnect.Task.WaitAsync(token); },
            (_, _) => { sends++; return Task.FromResult(true); },
            () => true, _ => { }, _ => { }, stop.Token);
        await waiting.Task.WaitAsync(timeout.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(timeout.Token));
        reconnect.SetResult(true);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task StopDuringSend_CancelsSendAndDoesNotRetry()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = 0;
        var retries = 0;
        var run = new CommandSequenceRunner().RunAsync(Sequence(),
            _ => Task.FromResult(true),
            async (_, token) =>
            {
                sending.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return true;
            },
            () => { retries++; return true; }, _ => { }, count => completed = count, stop.Token);
        await sending.Task.WaitAsync(timeout.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(timeout.Token));
        Assert.Equal(0, completed);
        Assert.Equal(0, retries);
    }

    [Fact]
    public async Task StopDuringPostSendDelay_DoesNotSendNextStep()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sequence = Sequence();
        sequence.Steps[0].DelayAfterMs = 600_000;
        var sends = 0;
        var run = new CommandSequenceRunner().RunAsync(sequence,
            _ => Task.FromResult(true),
            (_, _) => { sends++; return Task.FromResult(true); },
            () => true, _ => { }, _ => stop.Cancel(), stop.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonRecoverableFailure_StopsWithoutRetry(bool connected)
    {
        var sends = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CommandSequenceRunner().RunAsync(Sequence(),
                _ => Task.FromResult(connected),
                (_, _) => { sends++; return Task.FromResult(false); },
                () => false, _ => { }, _ => Assert.Fail("Failed send must not advance progress"), CancellationToken.None));
        Assert.Equal(connected ? 1 : 0, sends);
    }

    private static CommandSequence Sequence(int repeats = 1) => new()
    {
        Name = "Test",
        RepeatCount = repeats,
        Steps =
        [
            new CommandSequenceStep { CommandText = "A", DelayAfterMs = 0 },
            new CommandSequenceStep { CommandText = "B", DelayAfterMs = 0 }
        ]
    };
}
