namespace SerialMonitor.Core.Automation;

// UI-owned selection remains in the adapter; the asynchronous connection
// contract (cancellation and actual-target verification) is shared and testable.
public static class AutomationConnectionOperation
{
    public static async Task<AutomationDeviceState> ConnectAsync(string port, int baudRate,
        Func<AutomationDeviceState> getState, Func<Task> refreshPorts, Action selectPort,
        Func<CancellationToken, Task> connect, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = getState();
        if (current.Connected)
        {
            VerifyTarget(current, port, baudRate, "already_connected");
            return current;
        }

        // The GUI port refresh is shared with other callers. Cancel this wait
        // without canceling a shared refresh or starting a connection afterward.
        await refreshPorts().WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        selectPort();
        cancellationToken.ThrowIfCancellationRequested();
        await connect(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        current = getState();
        if (!current.Connected)
            throw new AutomationException("connect_failed", current.LastError ?? "Serial connection did not complete.");
        VerifyTarget(current, port, baudRate, "connection_mismatch");
        return current;
    }

    private static void VerifyTarget(AutomationDeviceState state, string port, int baudRate, string code)
    {
        if (!string.Equals(state.Port, port, StringComparison.OrdinalIgnoreCase) || state.BaudRate != baudRate)
            throw new AutomationException(code,
                $"Requested {port} at {baudRate} bps, but the GUI is connected to {state.Port} at {state.BaudRate} bps. Disconnect explicitly first.");
    }
}

// Cancellation of a request is forwarded only while startup is in progress.
// The connection owns an independent lifetime after this scope is disposed.
public sealed class ConnectionStartupCancellation : IDisposable
{
    private readonly CancellationTokenRegistration _registration;

    public ConnectionStartupCancellation(CancellationToken request, CancellationTokenSource connectionLifetime)
    {
        _registration = request.UnsafeRegister(static state =>
        {
            try { ((CancellationTokenSource)state!).Cancel(); }
            catch (ObjectDisposedException) { }
        }, connectionLifetime);
    }

    public void Dispose() => _registration.Dispose();
}
