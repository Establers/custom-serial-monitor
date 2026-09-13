using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public interface ICommandSequenceRunner
{
    Task RunAsync(
        CommandSequence sequence,
        Func<CancellationToken, Task<bool>> waitUntilReady,
        Func<CommandSequenceStep, CancellationToken, Task<bool>> send,
        Func<bool> canRetryFailedSend,
        Action<int> startingStep,
        Action<int> completedSteps,
        CancellationToken cancellationToken);
}
