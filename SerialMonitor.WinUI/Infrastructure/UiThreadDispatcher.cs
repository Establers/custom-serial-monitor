using Microsoft.UI.Dispatching;

namespace SerialMonitor.WinUI.Infrastructure;

// Keep view-model dispatch independent of a native window or message loop.
internal interface IUiThreadDispatcher
{
    bool HasThreadAccess { get; }
    bool TryEnqueue(Action action);
    DispatcherQueueTimer CreateTimer();
}

internal sealed class UiThreadDispatcher(DispatcherQueue queue) : IUiThreadDispatcher
{
    public bool HasThreadAccess => queue.HasThreadAccess;
    public bool TryEnqueue(Action action) => queue.TryEnqueue(() => action());
    public DispatcherQueueTimer CreateTimer() => queue.CreateTimer();
}
