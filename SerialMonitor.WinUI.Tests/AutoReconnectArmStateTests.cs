using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

[Collection(StabilityIsolationCollection.Name)]
public sealed class AutoReconnectArmStateTests
{
    [Fact]
    public async Task ManualDisconnectBeforeTransportCommit_RevokesOwnershipAndRetiresTransport()
    {
        var armState = new AutoReconnectArmState();
        armState.SetForEstablishedUserSession(shouldArm: true);
        var ownership = armState.CaptureOwnership();
        using var lifecycleGate = new SemaphoreSlim(1, 1);
        var commitReached = NewSignal();
        var allowCommit = NewSignal();
        await using var serial = new SerialService();

        var reconnectTask = Task.Run(async () =>
        {
            await lifecycleGate.WaitAsync();
            try
            {
                await serial.ConnectAsync(
                    new SerialSettings { PortName = "MOCK" },
                    new SerialReceiveOptions(),
                    CancellationToken.None);
                commitReached.TrySetResult();
                await allowCommit.Task;
                if (!armState.TryCommitAutomaticReconnect(ownership))
                {
                    serial.BeginDisconnect();
                    await serial.DisconnectAsync(CancellationToken.None);
                }
            }
            finally
            {
                lifecycleGate.Release();
            }
        });

        await commitReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        armState.Disarm();
        serial.BeginDisconnect();
        allowCommit.TrySetResult();

        await reconnectTask.WaitAsync(TimeSpan.FromSeconds(3));
        await lifecycleGate.WaitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await serial.DisconnectAsync(CancellationToken.None);
        }
        finally
        {
            lifecycleGate.Release();
        }

        Assert.False(armState.IsArmed);
        Assert.False(serial.IsConnected);
        Assert.Equal(SerialConnectionState.Disconnected, serial.ConnectionState);
        Assert.True(reconnectTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ManualDisconnectImmediatelyAfterTransportCommit_RemainsFinalOwner()
    {
        var armState = new AutoReconnectArmState();
        armState.SetForEstablishedUserSession(shouldArm: true);
        var ownership = armState.CaptureOwnership();
        using var lifecycleGate = new SemaphoreSlim(1, 1);
        var committed = NewSignal();
        var releaseReconnect = NewSignal();
        await using var serial = new SerialService();

        var reconnectTask = Task.Run(async () =>
        {
            await lifecycleGate.WaitAsync();
            try
            {
                await serial.ConnectAsync(
                    new SerialSettings { PortName = "MOCK" },
                    new SerialReceiveOptions(),
                    CancellationToken.None);
                Assert.True(armState.TryCommitAutomaticReconnect(ownership));
                committed.TrySetResult();
                await releaseReconnect.Task;
            }
            finally
            {
                lifecycleGate.Release();
            }
        });

        await committed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        armState.Disarm();
        serial.BeginDisconnect();
        releaseReconnect.TrySetResult();

        await lifecycleGate.WaitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await serial.DisconnectAsync(CancellationToken.None);
        }
        finally
        {
            lifecycleGate.Release();
        }

        await reconnectTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(armState.IsArmed);
        Assert.False(serial.IsConnected);
        Assert.Equal(SerialConnectionState.Disconnected, serial.ConnectionState);
        Assert.True(reconnectTask.IsCompletedSuccessfully);
    }

    [Fact]
    public void StaleReconnectCommit_NeverWritesArmedStateBackToTrue()
    {
        var armState = new AutoReconnectArmState();
        armState.SetForEstablishedUserSession(shouldArm: true);
        var ownership = armState.CaptureOwnership();

        armState.Disarm();

        Assert.False(armState.TryCommitAutomaticReconnect(ownership));
        Assert.False(armState.IsArmed);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
