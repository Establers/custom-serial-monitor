using System.Reflection;
using System.Runtime.CompilerServices;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ConnectionCancellationRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelingConnectDuringFaultedSessionCleanupRetainsRetirementAndBlocksReopen(bool faultDuringStartup)
    {
        using var injectionPublished = new ManualResetEventSlim();
        await using var serial = new SerialService();
        var retirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        object? faultedSession = null;
        var injected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var injectionClaimed = 0;
        void InjectFault()
        {
            // Connected notifications can overlap on the connect and mock RX
            // threads. A delayed second callback must not fault the final reopen.
            if (Interlocked.CompareExchange(ref injectionClaimed, 1, 0) != 0)
            {
                // Fixture-only barrier: Connect's own Connected callback must
                // not return while a worker won the claim but has not faulted it.
                if (!injectionPublished.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Fault injection setup did not complete.");
                return;
            }
            try
            {
                faultedSession = Get(serial, "_currentReceiveSession")!;
                Set(faultedSession, "_portRetirementTask", retirement.Task);
                typeof(SerialService).GetMethod("ReportError", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(serial, ["Injected startup/receive failure", true, SerialConnectionState.Faulted]);
                injected.TrySetResult();
            }
            catch (Exception ex) { injected.TrySetException(ex); }
            finally { injectionPublished.Set(); }
        }

        if (faultDuringStartup)
        {
            serial.StatusChanged += (_, _) =>
            {
                if (serial.ConnectionState == SerialConnectionState.Connected) InjectFault();
            };
        }
        else
        {
            await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
            InjectFault();
        }

        using var stop = new CancellationTokenSource();
        var connecting = serial.ConnectAsync(new() { PortName = "MOCK" }, new(), stop.Token);
        try
        {
            await injected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.NotNull(faultedSession);
            var generation = serial.ReceiveSessionGeneration;
            Assert.False(connecting.IsCompleted);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(serial.IsConnected);
            Assert.NotNull(Get(serial, "_pendingReceiveTeardown"));
            Assert.False(retirement.Task.IsCompleted);
            Assert.Null(Get(serial, "_currentReceiveSession"));

            using var reopenStop = new CancellationTokenSource();
            var reopening = serial.ConnectAsync(new() { PortName = "MOCK" }, new(), reopenStop.Token);
            Assert.False(reopening.IsCompleted);
            reopenStop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reopening.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(generation, serial.ReceiveSessionGeneration);
        }
        finally
        {
            retirement.TrySetResult();
            await Record.ExceptionAsync(() => connecting);
        }

        await serial.DisconnectAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        Assert.True(serial.IsConnected);
        Assert.Null(Get(serial, "_pendingReceiveTeardown"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelingPortRefreshWaitLeavesSharedScanAliveAndAllowsItsLateCompletion(bool failScan)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scans = 0;
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        Set(model, "_portRefreshOperation", new CoalescingAsyncOperation(async () =>
        {
            if (Interlocked.Increment(ref scans) == 1) await release.Task;
        }));
        using var stop = new CancellationTokenSource();
        var reconnectWait = model.RefreshPortsAsync(stop.Token);
        var otherWait = model.RefreshPortsAsync();
        try
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconnectWait.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(otherWait.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref scans));
            if (failScan)
            {
                release.SetException(new IOException("Injected delayed port scan failure"));
                var error = await Assert.ThrowsAsync<IOException>(() => otherWait.WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.Equal("Injected delayed port scan failure", error.Message);
                await model.RefreshPortsAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            else
            {
                release.SetResult();
                await otherWait.WaitAsync(TimeSpan.FromSeconds(2));
            }
            Assert.Equal(2, Volatile.Read(ref scans));
        }
        finally
        {
            release.TrySetResult();
            await Record.ExceptionAsync(() => otherWait);
        }
    }

    [Fact]
    public async Task AlreadyCanceledRefreshDoesNotStartOrScheduleAnotherScan()
    {
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        var scans = 0;
        Set(model, "_portRefreshOperation", new CoalescingAsyncOperation(() =>
        {
            Interlocked.Increment(ref scans);
            return Task.CompletedTask;
        }));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.RefreshPortsAsync(stop.Token));
        Assert.Equal(0, scans);
        Assert.Equal(0L, Get(model, "_portRefreshGeneration"));
    }

    private static object? Get(object target, string name) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
