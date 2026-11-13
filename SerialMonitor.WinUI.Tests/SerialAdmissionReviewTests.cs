using System.Reflection;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialAdmissionReviewTests
{
    [Fact]
    public async Task BaudChangeQueuedBehindDisconnectAndReconnectCannotChangeTheReplacement()
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new() { PortName = "MOCK", BaudRate = 115200 }, new(), default);
        var generation = serial.ReceiveSessionGeneration;
        var lifecycle = (SemaphoreSlim)typeof(SerialService).GetField("_lifecycleGate",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(serial)!;
        await lifecycle.WaitAsync();
        Task disconnecting, reconnecting;
        Task<int> changing;
        try
        {
            disconnecting = serial.DisconnectAsync(default);
            reconnecting = serial.ConnectAsync(new() { PortName = "MOCK", BaudRate = 115200 }, new(), default);
            changing = serial.ChangeBaudRateAsync(57600, default);
        }
        finally { lifecycle.Release(); }
        await disconnecting.WaitAsync(TimeSpan.FromSeconds(5));
        await reconnecting.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => changing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotEqual(generation, serial.ReceiveSessionGeneration);
        Assert.True(serial.IsConnected);
        Assert.Equal(115200, serial.AppliedBaudRate);
        await using var reservation = serial.AcquireExclusiveSession("replacement");
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("command")]
    [InlineData("direct-input")]
    [InlineData("baud")]
    [InlineData("configuration")]
    [InlineData("dtr")]
    [InlineData("rts-toggle")]
    public async Task AdmissionContentionCannotRetargetAnOldOperationOntoAReplacementConnection(string operation)
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new() { PortName = "MOCK", BaudRate = 115200 }, new(), default);
        var originalGeneration = serial.ReceiveSessionGeneration;
        Func<Task> invoke = operation switch
        {
            "bytes" => () => serial.SendBytesAsync([1, 2], "old command", default),
            "command" => () => serial.SendAsync(new("old", "ping"), default),
            "direct-input" => () => serial.SendTerminalInputAsync([3], originalGeneration, default),
            "baud" => () => serial.ChangeBaudRateAsync(57600, default),
            "configuration" => () => serial.ConfigureAsync(SerialPortConfiguration.FromSettings(
                new() { PortName = "MOCK", BaudRate = 57600 }), originalGeneration, default),
            "dtr" => () => serial.SetControlLineAsync(SerialControlLine.Dtr, true, originalGeneration, default),
            _ => () => serial.SetRtsToggleAsync(true, originalGeneration, default)
        };
        var work = await InvokeAcrossReplacement(serial, invoke);
        await Assert.ThrowsAsync<InvalidOperationException>(() => work);
        Assert.NotEqual(originalGeneration, serial.ReceiveSessionGeneration);
        Assert.True(serial.IsConnected);
        Assert.Equal(0, serial.WrittenByteCount);
        Assert.Equal(115200, serial.AppliedBaudRate);
        Assert.Equal(SerialControlLineMode.Off, serial.ControlLineStatus.Dtr);
        Assert.Equal(SerialControlLineMode.Off, serial.ControlLineStatus.Rts);
        // Failure must retire admission too; the replacement remains usable.
        await using var owner = serial.AcquireExclusiveSession("new owner");
        Assert.Equal(serial.ReceiveSessionGeneration, owner.Generation);
        await owner.SendAsync([4], default);
        Assert.Equal(1, serial.WrittenByteCount);
    }

    private static async Task<Task> InvokeAcrossReplacement(SerialService serial, Func<Task> operation)
    {
        var gate = typeof(SerialService).GetField("_transmitAdmissionGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(serial)!;
        using var release = new ManualResetEventSlim(false);
        var held = Signal();
        var holderFinished = Signal();
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = new Thread(() =>
        {
            try
            {
                lock (gate)
                {
                    held.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Admission holder was not released.");
                }
                holderFinished.TrySetResult();
            }
            catch (Exception ex) { holderFinished.TrySetException(ex); }
        }) { IsBackground = true };
        var sender = new Thread(() =>
        {
            try { returned.TrySetResult(operation()); }
            catch (Exception ex) { returned.TrySetException(ex); }
        }) { IsBackground = true };
        holder.Start();
        var senderStarted = false;
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            sender.Start();
            senderStarted = true;
            using var waitLimit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // No sleeps determine the race: this dedicated sender has reached
            // Monitor.Enter and cannot resume until the holder is released.
            while ((sender.ThreadState & ThreadState.WaitSleepJoin) == 0)
            {
                Assert.False(returned.Task.IsCompleted, "Operation escaped the held admission gate.");
                await Task.Delay(1, waitLimit.Token);
            }
            await serial.DisconnectAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            await serial.ConnectAsync(new() { PortName = "MOCK", BaudRate = 115200 }, new(), default)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await holderFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (senderStarted)
            {
                var finishing = await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Record.ExceptionAsync(() => finishing.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(finishing.IsCompleted, "Admitted serial operation did not settle after release.");
            }
        }
        return await returned.Task;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
