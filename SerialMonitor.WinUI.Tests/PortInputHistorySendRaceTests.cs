using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.UI.Dispatching;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class PortInputHistorySendRaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulWriteWhoseUiContinuationIsDelayedKeepsTheSendingPortsHistory(bool bridge)
    {
        var fixture = new SendFixture(bridge);
        var sending = fixture.StartSend();
        Assert.False(sending.IsCompleted);
        Assert.True(fixture.Model.CanManualDisconnect); // Actual GUI guard permits disconnect while awaiting TX.
        Assert.False(fixture.Model.CanEditConnectionSettings);
        await fixture.CompleteWriteAndWaitForUiContinuation();
        Assert.Equal("COM7", fixture.WrittenPort);
        Assert.Equal(Encoding.UTF8.GetBytes(SendFixture.Payload), fixture.WrittenBytes);
        Assert.False(sending.IsCompleted);

        fixture.DisconnectAndSelect("COM8");
        Assert.True(fixture.Model.CanEditConnectionSettings);
        fixture.Context.Drain();
        Assert.True(await sending.WaitAsync(TimeSpan.FromSeconds(3)), fixture.Model.LastTxError);
        fixture.AssertHistoryBelongsToCom7();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReentrantTxSentHandlerSwitchingPortsCannotRedirectTheCompletedCommandsHistory(bool bridge)
    {
        var fixture = new SendFixture(bridge);
        var notified = false;
        fixture.Model.TxSent += (_, _) =>
        {
            notified = true;
            fixture.DisconnectAndSelect("COM8");
        };
        var sending = fixture.StartSend();
        await fixture.CompleteWriteAndWaitForUiContinuation();
        fixture.Context.Drain();
        Assert.True(await sending.WaitAsync(TimeSpan.FromSeconds(3)), fixture.Model.LastTxError);
        Assert.True(notified);
        fixture.AssertHistoryBelongsToCom7();
    }

    [Fact]
    public async Task InactivePortCompletionPreservesTheNewPortsHistorySearchAndDraft()
    {
        var fixture = new SendFixture(false, seedNextPort: true);
        var sending = fixture.StartSend();
        await fixture.CompleteWriteAndWaitForUiContinuation();
        fixture.DisconnectAndSelect("COM8");
        fixture.Model.Commands.CurrentCommandText = " COM8 draft\t ";
        fixture.Model.SearchText = " COM8 search draft ";
        fixture.Context.Drain();
        Assert.True(await sending.WaitAsync(TimeSpan.FromSeconds(3)), fixture.Model.LastTxError);
        Assert.Equal("COM8 existing", Assert.Single(fixture.Model.Commands.CommandHistory).CommandText);
        Assert.Equal(" COM8 draft\t ", fixture.Model.Commands.CurrentCommandText);
        Assert.Equal(" COM8 search draft ", fixture.Model.SearchText);
        fixture.DisconnectAndSelect("COM7");
        Assert.Equal(SendFixture.Payload, Assert.Single(fixture.Model.Commands.CommandHistory).CommandText);
    }

    [Theory]
    [InlineData(false, "failure")]
    [InlineData(true, "failure")]
    [InlineData(false, "canceled")]
    [InlineData(true, "canceled")]
    [InlineData(false, "canceled_after_write")]
    [InlineData(true, "canceled_after_write")]
    public async Task FailedOrCanceledTransmissionAddsNoHistoryToEitherPort(bool bridge, string outcome)
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new SendFixture(bridge);
        var sending = fixture.StartSend(cancellation.Token);
        if (outcome == "failure") fixture.Completion.TrySetException(new IOException("Injected send failure"));
        else if (outcome == "canceled")
        {
            cancellation.Cancel();
            fixture.Completion.TrySetCanceled(cancellation.Token);
        }
        else
        {
            // The write may have completed just before cancellation is observed by the caller.
            await fixture.CompleteWriteAndWaitForUiContinuation();
            cancellation.Cancel();
        }
        await fixture.Context.Posted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.DisconnectAndSelect("COM8");
        fixture.Context.Drain();
        if (outcome == "failure") Assert.False(await sending.WaitAsync(TimeSpan.FromSeconds(3)));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Empty(fixture.Model.Commands.CommandHistory);
        fixture.DisconnectAndSelect("COM7");
        Assert.Empty(fixture.Model.Commands.CommandHistory);
    }

    private sealed class SendFixture
    {
        public const string Payload = " \tPING value\t ";
        public MainViewModel Model { get; }
        public DeferredUiContext Context { get; } = new();
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? WrittenPort { get; private set; }
        public byte[]? WrittenBytes { get; private set; }
        private bool _connected = true;
        private bool _bridgeRunning;
        private string _devicePort = "COM7";

        public SendFixture(bool bridge, bool seedNextPort = false)
        {
            _bridgeRunning = bridge;
            Model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
            Set(Model, "_dispatcherQueue", new ImmediateUiDispatcher());
            Set(Model, "_serialService", Proxy<ISerialService>((method, args) => method.Name switch
            {
                "get_IsConnected" => _connected,
                "get_ConnectionState" => _connected ? SerialConnectionState.Connected : SerialConnectionState.Disconnected,
                "SendBytesAsync" => Write((byte[])args![0]!),
                _ => Default(method.ReturnType)
            }));
            Set(Model, "_bridgeService", Proxy<ISerialBridgeService>((method, args) => method.Name switch
            {
                "get_IsRunning" => _bridgeRunning,
                "QueueManualTransmitAsync" => Queue((Func<CancellationToken, Task>)args![0]!, (CancellationToken)args[1]!),
                _ => Default(method.ReturnType)
            }));
            Set(Model, "_eventDetector", Proxy<IEventDetector>());
            Set(Model, "_logPipeline", Proxy<ILogPipeline>());
            Set(Model, "_fileLogWriter", Proxy<IFileLogWriter>());
            Set(Model, "_bridgeLogProcessor", Proxy<IBridgeLogProcessor>());
            Set(Model, "_automationControl", new AutomationControlGate());
            Set(Model, "_currentSerialSettings", new SerialSettings { PortName = "COM7" });
            Set(Model, "_currentUiSettings", new UiSettings());
            Set(Model, "_currentLogSettings", new LogSettings());
            Set(Model, "_currentBridgeSettings", new BridgeSettings());
            Set(Model, "_selectedPort", "COM7");
            Set(Model, "_selectedTxLineEnding", TxLineEndingMode.None);
            Set(Model, "_isConnected", true);
            Set(Model, "_viewPauseGate", new object());
            Set(Model, "_viewPause", new ViewPauseStateMachine());
            Set(Model, "_backgroundHealthErrorGate", new object());
            Set(Model, "_activeBackgroundHealthErrors", new Dictionary<string, string>());
            Set(Model, "_lastAvailableActualPorts", new HashSet<string>(["COM7", "COM8"]));
            foreach (var name in new[] { "_pendingReconnectSettings", "_pendingRestartSettings", "_pendingLogStartSettings" })
                Set(Model, name, new HashSet<string>());
            Set(Model, "_portInputHistories", new PortInputHistoryState());
            Set(Model, "_searchHistory", new List<string>());
            Set(Model, "_searchText", string.Empty);
            Set(Model, "_logBatchDispatcher", Batch<LogLine>());
            Set(Model, "_eventBatchDispatcher", Batch<DetectedEvent>());
            Set(Model, "_eventContextBatchDispatcher", Batch<EventEvidenceReference>());
            Set(Model, "<Commands>k__BackingField", new CommandViewModel());
            Set(Model, "<Log>k__BackingField", new LogViewModel(1000));
            Set(Model, "<PortNames>k__BackingField", new ObservableCollection<string>(["COM7", "COM8"]));
            Set(Model, "<BridgePortNames>k__BackingField", new ObservableCollection<string>());
            Set(Model, "<SearchResults>k__BackingField", new BulkObservableCollection<VisibleSearchResult>());
            foreach (var field in typeof(MainViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                if (field.FieldType == typeof(AsyncRelayCommand)) field.SetValue(Model, new AsyncRelayCommand(() => Task.CompletedTask));
            var profile = new AppProfile
            {
                SerialSettings = new() { PortName = "COM7" }, PortHistoryMigrationVersion = 1
            };
            if (seedNextPort) profile.PortHistories["COM8"] = new InstanceInputHistory
            {
                CommandHistory = [new() { CommandText = "COM8 existing" }],
                SearchHistory = ["COM8 old search"], LastSearchText = "COM8 old search"
            };
            Invoke(Model, "RestorePortInputHistory", profile);
        }

        public Task<bool> StartSend(CancellationToken token = default)
        {
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(Context);
                return (Task<bool>)Invoke(Model, "SendCommandAsync", new TxCommand("test", Payload),
                    true, null, token, false, false, false)!;
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        public async Task CompleteWriteAndWaitForUiContinuation()
        {
            Completion.TrySetResult();
            await Context.Posted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }

        public void DisconnectAndSelect(string port)
        {
            _connected = false;
            _bridgeRunning = false;
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsConnected))!.SetValue(Model, false);
            Model.SelectedPort = port; // Use production selection/history-switch behavior.
            _devicePort = port;
        }

        public void AssertHistoryBelongsToCom7()
        {
            Assert.Equal("COM8", Model.SelectedActualPortName);
            Assert.Empty(Model.Commands.CommandHistory);
            Invoke(Model, "CaptureCurrentPortInputHistory");
            var state = (PortInputHistoryState)Get(Model, "_portInputHistories")!;
            var snapshot = state.Snapshot();
            Assert.Empty(snapshot["COM8"].CommandHistory);
            Assert.Equal(Payload, Assert.Single(snapshot["COM7"].CommandHistory).CommandText);
            DisconnectAndSelect("COM7");
            Assert.Equal(Payload, Assert.Single(Model.Commands.CommandHistory).CommandText);
        }

        private Task Write(byte[] bytes)
        {
            WrittenBytes = bytes.ToArray();
            WrittenPort = _devicePort;
            return Completion.Task;
        }

        private static async Task<ManualTransmitResult> Queue(Func<CancellationToken, Task> write, CancellationToken token)
        {
            await write(token).ConfigureAwait(false);
            return ManualTransmitResult.Sent;
        }

    }

    private sealed class ImmediateUiDispatcher : IUiThreadDispatcher
    {
        public bool HasThreadAccess => true;
        public bool TryEnqueue(Action action) { action(); return true; }
        public DispatcherQueueTimer CreateTimer() => throw new NotSupportedException("This fixture does not run native UI timers.");
    }

    // Only the captured continuation is held; the serial write itself completes.
    private sealed class DeferredUiContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _work = new();
        public TaskCompletionSource Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Post(SendOrPostCallback callback, object? state)
        {
            _work.Enqueue((callback, state));
            Posted.TrySetResult();
        }
        public void Drain()
        {
            while (_work.TryDequeue(out var item)) item.Callback(item.State);
        }
    }

    private static UiBatchDispatcher<T> Batch<T>()
    {
        // No native timer/dispatcher is needed: SendCommandAsync only posts and reads counts.
        var result = (UiBatchDispatcher<T>)RuntimeHelpers.GetUninitializedObject(typeof(UiBatchDispatcher<T>));
        typeof(UiBatchDispatcher<T>).GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(result, new MemoryBoundedQueue<T>(32, 1024 * 1024, _ => 1));
        typeof(UiBatchDispatcher<T>).GetField("_dropOldestWhenFull", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(result, true);
        return result;
    }

    public class ServiceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handle;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handle?.Invoke(method!, args) ?? Default(method!.ReturnType);
    }
    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var proxy = DispatchProxy.Create<T, ServiceProxy>();
        ((ServiceProxy)(object)proxy).Handle = handler;
        return proxy;
    }
    private static object? Default(Type type) => type == typeof(void) ? null : type.IsValueType ? Activator.CreateInstance(type) : null;
    private static object? Invoke(MainViewModel model, string method, params object?[] args) =>
        typeof(MainViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, args);
    private static void Set(MainViewModel model, string name, object value) =>
        typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, value);
    private static object? Get(MainViewModel model, string name) =>
        typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model);
}
