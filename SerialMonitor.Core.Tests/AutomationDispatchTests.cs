using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed class AutomationDispatchTests
{
    [Fact]
    public async Task CancelQueuedSendReturnsWithoutWaitingForTheUiAndNeverRunsLater()
    {
        Action? queued = null;
        using var stop = new CancellationTokenSource();
        var sends = 0;
        var request = AutomationDispatch.InvokeAsync(false, action => { queued = action; return true; },
            () => { sends++; return Task.FromResult<object>(new { sent = true }); }, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(1)));
        queued!();
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task CancellationAfterReserveStartsMustNotHideItsCommittedResult()
    {
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var result = new object();
        var request = AutomationDispatch.InvokeAsync(true, _ => throw new Exception("Not queued."), async () =>
        {
            acquired.SetResult(); await acknowledged.Task; return result;
        }, stop.Token);
        await acquired.Task;
        stop.Cancel();
        Assert.False(request.IsCompleted);
        acknowledged.SetResult();
        Assert.Same(result, await request.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task RejectedQueueAndPreCanceledRequestsCannotTouchTheDevice()
    {
        var calls = 0;
        Task<object> Work() { calls++; return Task.FromResult<object>(new object()); }
        var error = await Assert.ThrowsAsync<AutomationException>(() => AutomationDispatch.InvokeAsync(false, _ => false, Work, default));
        Assert.Equal("gui_unavailable", error.Code);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AutomationDispatch.InvokeAsync(true, _ => true, Work, stop.Token));
        Assert.Equal(0, calls);
    }
}
