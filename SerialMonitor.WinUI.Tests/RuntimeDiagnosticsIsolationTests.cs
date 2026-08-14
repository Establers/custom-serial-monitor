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

    [Fact]
    public async Task BlockingGeneralPump_DoesNotDelayIncidentPump()
    {
        using var generalStarted = new ManualResetEventSlim(initialState: false);
        using var releaseGeneral = new ManualResetEventSlim(initialState: false);
        var incidentCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var general = new BoundedDiagnosticWriter(
            capacity: 2,
            (_, _) =>
            {
                generalStarted.Set();
                releaseGeneral.Wait();
            });
        await using var incident = new BoundedIncidentWriter(
            capacity: 2,
            _ => incidentCompleted.TrySetResult());

        Assert.True(general.TryEnqueue(new GeneralDiagnosticWork(
            GeneralDiagnosticWorkKind.Error,
            "blocked general diagnostic",
            DateTimeOffset.UtcNow)));
        Assert.True(generalStarted.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            Assert.True(incident.TryEnqueue("independent writer incident"));
            await incidentCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, general.PendingWorkCount);
        }
        finally
        {
            releaseGeneral.Set();
        }

        Assert.True(await general.CompleteAndDrainAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await incident.CompleteAndDrainAsync(TimeSpan.FromSeconds(2)));
    }
}
