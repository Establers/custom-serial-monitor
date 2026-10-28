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

    [Theory]
    [InlineData(1)]
    [InlineData(CommandSequenceRunner.MaxAttemptsPerSlice * 3 + 1)]
    [InlineData(1000)]
    public async Task RepeatedDisconnects_RetryOnlyFailedStepWithoutAdvancingProgress(int failedAttempts)
    {
        // This verifies retry/progress semantics, not scheduler throughput. The
        // runner yields on its own; an extra Task.Yield per fake reconnect made
        // hosted CI hit the old five-second cutoff despite correct behavior.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var attempts = 0;
        var waits = 0;
        var completed = new List<int>();
        var sent = new List<string>();
        await new CommandSequenceRunner().RunAsync(Sequence(),
            token => { waits++; token.ThrowIfCancellationRequested(); return Task.FromResult(true); },
            (step, _) =>
            {
                attempts++;
                if (attempts <= failedAttempts)
                {
                    Assert.Equal("A", step.CommandText);
                    Assert.Empty(completed);
                    return Task.FromResult(false);
                }
                sent.Add(step.CommandText);
                return Task.FromResult(true);
            },
            () => true, _ => { }, completed.Add, timeout.Token);
        Assert.Equal(failedAttempts + 2, attempts);
        Assert.Equal(attempts, waits);
        Assert.Equal(new[] { "A", "B" }, sent);
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

    [Fact]
    public async Task PauseDuringSend_FinishesCurrentTxAndDelayBeforePausing()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var control = new SequenceExecutionControl();
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = PausedSignal(control);
        var sequence = Sequence();
        sequence.Steps[0].DelayAfterMs = 50;
        var sent = new List<string>();
        var completed = new List<int>();
        var delayClock = new System.Diagnostics.Stopwatch();
        var run = new CommandSequenceRunner().RunAsync(sequence,
            _ => Task.FromResult(true),
            async (step, token) =>
            {
                sent.Add(step.CommandText);
                if (sent.Count == 1)
                {
                    sending.SetResult();
                    await finishSend.Task.WaitAsync(token);
                    delayClock.Start();
                }
                return true;
            }, () => true, _ => { }, completed.Add, timeout.Token, control);

        await sending.Task.WaitAsync(timeout.Token);
        control.Pause();
        Assert.True(control.IsPauseRequested);
        Assert.False(control.IsPaused);
        finishSend.SetResult();
        await paused.Task.WaitAsync(timeout.Token);
        Assert.True(delayClock.Elapsed >= TimeSpan.FromMilliseconds(40));
        Assert.Equal(new[] { "A" }, sent);
        Assert.Equal(new[] { 1 }, completed);
        Assert.False(run.IsCompleted);

        control.Resume();
        await run.WaitAsync(timeout.Token);
        Assert.Equal(new[] { "A", "B" }, sent);
        Assert.False(control.IsPaused);
    }

    [Fact]
    public async Task PauseDuringReconnect_HoldsBeforeSendingAndResumeDoesNotReplay()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var control = new SequenceExecutionControl();
        var reconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkingAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnectAgain = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = PausedSignal(control);
        var sent = new List<string>();
        var checks = 0;
        var run = new CommandSequenceRunner().RunAsync(Sequence(),
            async token =>
            {
                if (++checks == 1)
                {
                    reconnecting.SetResult();
                    return await reconnect.Task.WaitAsync(token);
                }
                if (checks == 2)
                {
                    checkingAgain.SetResult();
                    return await reconnectAgain.Task.WaitAsync(token);
                }
                return true;
            },
            (step, _) => { sent.Add(step.CommandText); return Task.FromResult(true); },
            () => true, _ => { }, _ => { }, timeout.Token, control);

        await reconnecting.Task.WaitAsync(timeout.Token);
        control.Pause();
        reconnect.SetResult(true);
        await paused.Task.WaitAsync(timeout.Token);
        Assert.Empty(sent);

        control.Resume();
        await checkingAgain.Task.WaitAsync(timeout.Token);
        Assert.Empty(sent); // The earlier readiness result was invalidated by the pause.
        reconnectAgain.SetResult(true);
        await run.WaitAsync(timeout.Token);
        Assert.Equal(new[] { "A", "B" }, sent);
    }

    [Fact]
    public async Task StopWhilePaused_WakesWaitAndPreventsAnyLaterSend()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var control = new SequenceExecutionControl();
        control.Pause();
        var paused = PausedSignal(control);
        var sends = 0;
        var readyChecks = 0;
        var run = new CommandSequenceRunner().RunAsync(Sequence(),
            _ => { readyChecks++; return Task.FromResult(true); },
            (_, _) => { sends++; return Task.FromResult(true); },
            () => true, _ => { }, _ => { }, stop.Token, control);

        await paused.Task.WaitAsync(timeout.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(timeout.Token));
        control.Resume();
        Assert.Equal(0, sends);
        Assert.Equal(0, readyChecks);
        Assert.False(control.IsPaused);
    }

    [Fact]
    public async Task DisabledSteps_AreSkippedWithFrozenRunOrderAndEnabledProgress()
    {
        var sequence = Sequence(repeats: 2);
        sequence.Steps[0].IsEnabled = false;
        var sent = new List<string>();
        var started = new List<int>();
        var completed = new List<int>();
        await new CommandSequenceRunner().RunAsync(sequence,
            _ => Task.FromResult(true),
            (step, _) =>
            {
                sent.Add(step.CommandText);
                // Editing availability after start cannot add work to this run.
                sequence.Steps[0].IsEnabled = true;
                sequence.Steps[1].IsEnabled = false;
                return Task.FromResult(true);
            }, () => true, started.Add, completed.Add, CancellationToken.None);
        Assert.Equal(new[] { "B", "B" }, sent);
        Assert.Equal(new[] { 0, 1 }, started);
        Assert.Equal(new[] { 1, 2 }, completed);
    }

    [Fact]
    public async Task AllDisabledSequence_FailsBeforeReadinessOrTx()
    {
        var sequence = Sequence();
        foreach (var step in sequence.Steps) step.IsEnabled = false;
        var readyChecks = 0;
        var sends = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CommandSequenceRunner().RunAsync(sequence,
            _ => { readyChecks++; return Task.FromResult(true); },
            (_, _) => { sends++; return Task.FromResult(true); },
            () => false, _ => { }, _ => { }, CancellationToken.None));
        Assert.Contains("enable at least one step", error.Message);
        Assert.Equal(0, readyChecks);
        Assert.Equal(0, sends);
    }

    private static TaskCompletionSource PausedSignal(SequenceExecutionControl control)
    {
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        control.StateChanged += () => { if (control.IsPaused) paused.TrySetResult(); };
        return paused;
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
