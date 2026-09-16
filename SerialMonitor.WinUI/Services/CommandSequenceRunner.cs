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
        CancellationToken cancellationToken)
    {
        var total = checked(sequence.Steps.Count * sequence.RepeatCount);
        var attemptsInSlice = 0;
        var sliceStarted = Stopwatch.GetTimestamp();
        for (var position = 0; position < total; position++)
        {
            var step = sequence.Steps[position % sequence.Steps.Count];
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
                if (!await waitUntilReady(cancellationToken))
                    throw new InvalidOperationException("Sequence stopped: serial connection is unavailable and automatic reconnect is not active.");

                cancellationToken.ThrowIfCancellationRequested();
                startingStep(position);
                if (await send(step, cancellationToken))
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
            if (step.DelayAfterMs > 0)
                await Task.Delay(step.DelayAfterMs, cancellationToken);
        }
    }
}
