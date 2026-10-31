using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ShutdownRegressionTests
{
    [Fact]
    public async Task AutomationHostStopFailureStillDisposesCaptureAndRemovesRxSubscriptions()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(ShutdownRegressionTests), Guid.NewGuid().ToString("N"));
        var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"),
            (_, _) => Task.FromResult(AutomationResponse.Ok(new { })), _ => { }, root);
        var cachedStop = typeof(AutomationPipeHost).GetField("_disposeTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
        cachedStop.SetValue(host, Task.FromException(new IOException("Injected broken pipe cleanup failure")));
        var serial = DispatchProxy.Create<ISerialService, EventSerial>();
        var captures = new RxCaptureService(Path.Combine(root, "captures"));
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        Set(model, "_automationHost", host);
        Set(model, "_captures", captures);
        Set(model, "_serialService", serial);
        try
        {
            await ((Task)Invoke(model, "StopAutomationAsync", CancellationToken.None)!).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Null(Get(model, "_automationHost"));
            Assert.Null(Get(model, "_captures"));
            Assert.Equal(2, ((EventSerial)(object)serial).RemovedSubscriptions);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => captures.CaptureAsync(new(0), "COM7",
                System.Text.Encoding.UTF8, () => new(true, 1, 0), null, default));
        }
        finally
        {
            cachedStop.SetValue(host, null);
            await host.StopAsync(default);
            await captures.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ShutdownImmediatelyCancelsTheSequenceAndRefreshesTxCommandState()
    {
        await using var bridge = new SerialBridgeService();
        using var sequence = new CancellationTokenSource();
        var model = CreateState(bridge);
        Set(model, "_sequenceCancellation", sequence);
        Set(model, "_isSequenceRunning", true);
        var changes = new HashSet<string>();
        var notified = false;
        model.PropertyChanged += (_, args) => changes.Add(args.PropertyName!);
        model.RunCommandSequenceCommand.CanExecuteChanged += (_, _) =>
        {
            Assert.True(sequence.IsCancellationRequested);
            Assert.False(CanRun(model));
            notified = true;
        };

        Assert.True((bool)Invoke(model, "TryBeginShutdown")!);
        Assert.True(sequence.IsCancellationRequested);
        Assert.True(model.WasSequenceRunningDuringShutdown);
        Assert.False(model.CanSendManualTx);
        Assert.Contains(nameof(model.CanSendManualTx), changes);
        Assert.True(notified);
        Assert.False((bool)Invoke(model, "TryBeginShutdown")!);
    }

    [Fact]
    public async Task ShutdownRejectsManualAndRuleSequencesAndEveryDirectTxEntry()
    {
        await using var bridge = new SerialBridgeService();
        var model = CreateState(bridge);
        Assert.True(model.CanSendManualTx);
        Assert.True(CanRun(model));
        Assert.True((bool)Invoke(model, "TryBeginShutdown")!);
        Assert.False(model.CanSendManualTx);
        Assert.False(CanRun(model));
        var requestType = typeof(MainViewModel).GetNestedType("SequenceRunRequest", BindingFlags.NonPublic)!;
        var ruleRequest = Activator.CreateInstance(requestType, model.SelectedCommandSequence, "rule trigger");
        Assert.False((bool)Invoke(model, "CanRunCommandSequence", ruleRequest)!);
        await (Task)Invoke(model, "RunCommandSequenceAsync", ruleRequest)!;
        Assert.False(model.IsSequenceRunning);
        Assert.False(model.CanConnect);
        Assert.False(model.CanStartBridge);
        // No serial service is supplied: every entry must return before touching it.
        foreach (var (automation, sequence) in new[] { (false, false), (true, false), (false, true) })
            Assert.False(await (Task<bool>)Invoke(model, "SendCommandAsync",
                new TxCommand("shutdown", "ping"), false, null, CancellationToken.None, automation, false, sequence)!);
    }

    [Fact]
    public async Task AutomationShutdownUsesTheCallersDeadlineAndKeepsOneCleanupUntilTheHandlerReturns()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(ShutdownRegressionTests), Guid.NewGuid().ToString("N"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AutomationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), (_, _) =>
        {
            entered.TrySetResult();
            return release.Task;
        }, _ => { }, root);
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        Set(model, "_automationHost", host);
        Set(model, "_serialService", DispatchProxy.Create<ISerialService, EventSerial>());
        host.Start();
        var client = AutomationPipeClient.CallAsync(host.Instance, "status", new { }, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ((Task)Invoke(model, "StopAutomationAsync", deadline.Token)!).WaitAsync(TimeSpan.FromSeconds(3)));
            var cleanup = Get(model, "_automationStopTask");
            Assert.Same(host, Get(model, "_automationHost"));
            var secondWait = (Task)Invoke(model, "StopAutomationAsync", CancellationToken.None)!;
            Assert.Same(cleanup, Get(model, "_automationStopTask"));
            Assert.False(secondWait.IsCompleted);
            release.SetResult(new(true, JsonSerializer.SerializeToElement(new { })));
            await secondWait.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Null(Get(model, "_automationHost"));
        }
        finally
        {
            release.TrySetCanceled();
            await host.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            await Record.ExceptionAsync(() => client);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static MainViewModel CreateState(SerialBridgeService bridge)
    {
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        Set(model, "_isConnected", true);
        Set(model, "_bridgeService", bridge);
        Set(model, "_automationControl", new AutomationControlGate());
        Set(model, "_selectedCommandSequence", new CommandSequence { Name = "test", Steps = [new() { CommandText = "ping" }] });
        foreach (var field in typeof(MainViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            if (field.FieldType == typeof(AsyncRelayCommand)) field.SetValue(model, new AsyncRelayCommand(() => Task.CompletedTask));
        return model;
    }

    private static bool CanRun(MainViewModel model) => (bool)Invoke(model, "CanRunCommandSequence", (object?)null)!;
    private static object? Invoke(MainViewModel model, string name, params object?[] args) =>
        typeof(MainViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, args);
    private static object? Get(MainViewModel model, string field) =>
        typeof(MainViewModel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model);
    private static void Set(MainViewModel model, string field, object value) =>
        typeof(MainViewModel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, value);

    public class EventSerial : DispatchProxy
    {
        public int RemovedSubscriptions { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name is "remove_CaptureBytesReceived" or "remove_StatusChanged")
            { RemovedSubscriptions++; return null; }
            throw new NotSupportedException(method.Name);
        }
    }
}
