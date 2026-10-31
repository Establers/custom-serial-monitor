using System.Reflection;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialDriverTeardownTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task FailedOpenCleanupDoesNotExtendCancellationOrAnExpiredOpenDeadline(bool opening, bool cancel)
    {
        await using var serial = new SerialService();
        using var stop = new CancellationTokenSource();
        var sessionType = typeof(SerialService).GetNestedType("ReceiveSession", BindingFlags.NonPublic)!;
        var session = Activator.CreateInstance(sessionType, 1L, false, CancellationToken.None)!;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = opening ? release.Task : Task.CompletedTask;
        if (!opening) Set(session, "_portRetirementTask", release.Task);
        if (cancel) stop.CancelAfter(TimeSpan.FromMilliseconds(100));
        var cleanupMethod = typeof(SerialService).GetMethod("CleanupFailedReceiveSessionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cleanup = (Task)cleanupMethod.Invoke(serial, [session, null, opened, stop.Token])!;
        try
        {
            await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(Get(serial, "_pendingReceiveTeardown"));
            Assert.Equal(0, Get(session, "_disposed"));
            using var reconnect = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                serial.ConnectAsync(new() { PortName = "MOCK" }, new(), reconnect.Token));
            Assert.Equal(0, serial.ReceiveSessionGeneration);
        }
        finally { release.TrySetResult(); await cleanup; }
        await serial.DisconnectAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        using var finished = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while ((int)Get(session, "_disposed")! == 0) await Task.Delay(10, finished.Token);
    }

    [Fact]
    public async Task DisconnectCancellationInterruptsAnAlreadyPendingDriverRetirement()
    {
        await using var serial = new SerialService();
        using var stop = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(serial, "_pendingReceiveTeardown", release.Task);
        var disconnect = serial.DisconnectAsync(stop.Token);
        try
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disconnect.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Same(release.Task, Get(serial, "_pendingReceiveTeardown"));
        }
        finally { release.TrySetResult(); await Record.ExceptionAsync(() => disconnect); }
        await serial.DisconnectAsync(default);
        Assert.Equal(SerialConnectionState.Disconnected, serial.ConnectionState);
    }

    [Fact]
    public async Task DisconnectCancellationRetainsResourcesUntilTheDelayedDriverCloseActuallyEnds()
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        var generation = serial.ReceiveSessionGeneration;
        var session = Get(serial, "_currentReceiveSession")!;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Hold precisely the same retirement task used by native Close()/Dispose().
        Set(session, "_portRetirementTask", release.Task);
        using var stop = new CancellationTokenSource();
        var disconnect = serial.DisconnectAsync(stop.Token);
        try
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disconnect.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.NotNull(Get(serial, "_pendingReceiveTeardown"));
            Assert.False(serial.IsConnected);
            using var reconnectStop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                serial.ConnectAsync(new() { PortName = "MOCK" }, new(), reconnectStop.Token));
            Assert.Equal(generation, serial.ReceiveSessionGeneration);
        }
        finally { release.TrySetResult(); await Record.ExceptionAsync(() => disconnect); }
        await serial.DisconnectAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        Assert.True(serial.ReceiveSessionGeneration > generation);
        Assert.True(serial.IsConnected);
    }

    private static object? Get(object target, string field) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
