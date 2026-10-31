using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public interface ICommandSequenceObserver
{
    Task CheckReceiveAsync(int position, CommandSequenceStep step, CancellationToken token) =>
        Task.FromException(new NotSupportedException("RX Check requires a response observer."));
    Task BeforeSendAsync(int position, CommandSequenceStep step, CancellationToken token);
    Task AfterSendAsync(bool sent, CancellationToken token);
    Task AfterStepAsync(CancellationToken token);
}
