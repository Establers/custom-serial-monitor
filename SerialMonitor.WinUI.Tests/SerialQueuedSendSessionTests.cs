using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.ViewModels;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialQueuedSendSessionTests
{
    [Fact]
    public void SnapshotCannotCombineThePreviousDisconnectWithTheNewGeneration()
    {
        var serial = DispatchProxy.Create<ISerialService, ChangingSerial>();
        ((ChangingSerial)serial).ChangeDuringRead = true;
        var model = Model(serial);
        var connection = (CaptureConnection)typeof(MainViewModel).GetProperty("CaptureConnectionSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        Assert.True(connection.Connected);
        Assert.Equal(2, connection.Generation);
    }

    [Fact]
    public async Task ActualGuiSendAdapterRejectsThePreviousGenerationBeforeTouchingTxOrUi()
    {
        var serial = DispatchProxy.Create<ISerialService, ChangingSerial>();
        ((ChangingSerial)serial).Generation = 2;
        var model = Model(serial);
        var control = new AutomationControlGate(); control.TryReserveAutomation();
        Set(model, "_automationControl", control);
        Set(model, "_startupProfileTask", Task.CompletedTask);
        var args = JsonSerializer.SerializeToElement(new { command = "00 FF", txMode = "hex", lineEnding = "none", expectedSessionGeneration = 1 });
        var method = typeof(MainViewModel).GetMethod("InvokeAutomationOnUiAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var error = await Assert.ThrowsAsync<AutomationException>(() => (Task<object>)method.Invoke(model, ["send", args, CancellationToken.None])!);
        Assert.Equal("disconnected", error.Code);
    }

    private static MainViewModel Model(ISerialService serial)
    {
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        Set(model, "_serialService", serial); return model;
    }
    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    public class ChangingSerial : DispatchProxy
    {
        public bool ChangeDuringRead;
        public long Generation = 1;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_ReceiveSessionGeneration" => Generation,
            "get_IsConnected" => Connected(),
            "get_SerialFrameErrorCount" or "get_SerialParityErrorCount" or "get_SerialOverrunErrorCount" or "get_SerialRxOverErrorCount" => 0L,
            _ => throw new InvalidOperationException("Unexpected serial access: " + method.Name)
        };
        private bool Connected()
        {
            if (ChangeDuringRead) { ChangeDuringRead = false; Generation = 2; return false; }
            return true;
        }
    }

    [Fact]
    public async Task SendQueuedBehindDisconnectCannotWriteIntoTheReconnectedSession()
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        var gate = (SemaphoreSlim)typeof(SerialService).GetField("_writeGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(serial)!;
        var context = new DelayedContext();
        await gate.WaitAsync();
        var disconnecting = serial.DisconnectAsync(default);
        Task sending;
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            sending = serial.SendBytesAsync([0, 255], "old TX", default);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); gate.Release(); }
        try
        {
            await disconnecting.WaitAsync(TimeSpan.FromSeconds(3));
            await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
            await context.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            context.Drain();
            await Assert.ThrowsAsync<InvalidOperationException>(() => sending.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(0, serial.WrittenByteCount);
            Assert.True(serial.IsConnected);
        }
        finally { context.Drain(); await Record.ExceptionAsync(() => sending); }
    }

    private sealed class DelayedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _work = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Post(SendOrPostCallback callback, object? state)
        { _work.Enqueue((callback, state)); Entered.TrySetResult(); }
        public void Drain() { while (_work.TryDequeue(out var work)) work.Callback(work.State); }
    }
}
