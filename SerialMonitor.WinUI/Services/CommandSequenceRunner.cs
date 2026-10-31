using System.Diagnostics;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed class CommandSequenceRunner : ICommandSequenceRunner
{
    internal const int MaxAttemptsPerSlice = 16;
    private static readonly TimeSpan MaxSliceDuration = TimeSpan.FromMilliseconds(8);
    public async Task RunAsync(
        CommandSequence sequence,
        Func<CancellationToken, Task<bool>> waitUntilReady,
        Func<CommandSequenceStep, CancellationToken, Task<bool>> send,
        Func<bool> canRetryFailedSend,
        Action<int> startingStep,
        Action<int> completedSteps,
        CancellationToken cancellationToken,
        SequenceExecutionControl? control = null,
        ICommandSequenceObserver? observer = null)
    {
        var steps = sequence.Steps.Where(step => step.IsEnabled).ToArray();
        if (steps.Any(step => !Enum.IsDefined(step.Kind))) throw new InvalidOperationException("Invalid sequence step kind; nothing was sent.");
        if (steps.Length == 0)
            throw new InvalidOperationException("Sequence stopped: enable at least one step before running.");
        if (observer is null && steps.Any(step => step.Kind == CommandSequenceStepKind.RxCheck))
            throw new InvalidOperationException("RX Check requires a response observer; nothing was sent.");
        var total = checked(steps.Length * sequence.RepeatCount);
        var attemptsInSlice = 0;
        var sliceStarted = Stopwatch.GetTimestamp();
        try
        {
            for (var position = 0; position < total; position++)
            {
                var step = steps[position % steps.Length];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (attemptsInSlice >= MaxAttemptsPerSlice || Stopwatch.GetElapsedTime(sliceStarted) >= MaxSliceDuration)
                    {
                        // Preserve the caller's UI context: callbacks access UI-owned state.
                        // Task.Yield alone can continuously refill the UI dispatch queue.
                        // A real timer wait also covers synchronously completed sends/retries.
                        await Task.Delay(1, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        attemptsInSlice = 0;
                        sliceStarted = Stopwatch.GetTimestamp();
                    }
                    attemptsInSlice++;
                    if (control is not null) await control.WaitAtStepBoundaryAsync(cancellationToken);
                    if (!await waitUntilReady(cancellationToken))
                        throw new InvalidOperationException("Sequence stopped: serial connection is unavailable and automatic reconnect is not active.");

                    // Pause may have been requested while reconnecting.
                    if (control is not null && await control.WaitBeforeSendAsync(cancellationToken))
                        continue; // Readiness may have changed while paused.
                    cancellationToken.ThrowIfCancellationRequested();
                    startingStep(position);
                    if (step.Kind == CommandSequenceStepKind.RxCheck)
                    {
                        await observer!.CheckReceiveAsync(position, step, cancellationToken);
                        break;
                    }
                    if (observer is not null) await observer.BeforeSendAsync(position, step, cancellationToken);
                    var sent = await send(step, cancellationToken);
                    if (observer is not null) await observer.AfterSendAsync(sent, cancellationToken);
                    if (sent)
                        break;

                    cancellationToken.ThrowIfCancellationRequested();
                    if (!canRetryFailedSend())
                        throw new InvalidOperationException($"Sequence stopped: step failed ({step.DisplayName}).");
                    // A disconnected write is retried only after the transport is ready.
                    // Without a device ACK, a failed write can have reached the device.
                }

                // Advance only on successful sends. An outage during this delay does
                // not replay the command; the next iteration waits for reconnection.
                completedSteps(position + 1);
                if (step.Kind == CommandSequenceStepKind.Tx && step.DelayAfterMs > 0)
                    await Task.Delay(step.DelayAfterMs, cancellationToken);
                if (observer is not null) await observer.AfterStepAsync(cancellationToken);
            }
        }
        finally { control?.EndRun(); }
    }
}
