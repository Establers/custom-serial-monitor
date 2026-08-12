using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class RuntimeDiagnosticsIsolationTests
{
    [Fact]
    public async Task BlockingIncidentOperation_DoesNotBlockGeneralDiagnosticOperation()
    {
        var gates = new DiagnosticOperationGates();
        using var incidentStarted = new ManualResetEventSlim(initialState: false);
        using var releaseIncident = new ManualResetEventSlim(initialState: false);
        var generalCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var incidentTask = Task.Run(() =>
            gates.RunFileWriterIncident(() =>
            {
                incidentStarted.Set();
                releaseIncident.Wait();
            }));

        Assert.True(incidentStarted.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            var generalTask = Task.Run(() =>
                gates.RunGeneral(() => generalCompleted.TrySetResult()));

            await generalCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await generalTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(incidentTask.IsCompleted);
        }
        finally
        {
            releaseIncident.Set();
        }

        await incidentTask.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
