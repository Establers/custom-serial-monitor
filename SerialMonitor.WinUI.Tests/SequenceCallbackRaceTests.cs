using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class SequenceCallbackRaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOldCallbackCannotRewindTheErrorCounterOrInvalidateTheNextStep(bool pauseStatus)
    {
        await WithSession(async (model, serial, session, store) =>
        {
            await session.BeginAsync(Rx(), 1, 1, 1, default);
            serial.PauseNext = 1;
            var old = Task.Run(() => Notify(model, pauseStatus, 1, "old"));
            try
            {
                await serial.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                serial.Errors = 1;
                Notify(model, !pauseStatus, 1, "first");
                await session.CheckFullWindowAsync(default); await session.EndStepAsync(default);
                await session.BeginAsync(Rx(), 2, 1, 1, default);
                serial.Release.Set(); await old.WaitAsync(TimeSpan.FromSeconds(5));
                Notify(model, false, 1, "READY");
                await session.CheckFullWindowAsync(default); await session.EndStepAsync(default);
                var rows = (await store.ReadPageAsync(session.RunId)).Rows;
                Assert.Equal("inconclusive", rows[0].CheckStatus); // genuine first-step error
                Assert.Equal("matched", rows[1].CheckStatus); Assert.Null(rows[1].Error);
                Assert.Equal(1L, Get(model, "_sequenceResultSerialErrors"));
            }
            finally { serial.Release.Set(); await old; }
        });
    }

    [Fact]
    public async Task ADelayedConnectedSnapshotCannotUndoANewerDisconnect()
    {
        await WithSession(async (model, serial, session, store) =>
        {
            serial.PauseNext = 1;
            var old = Task.Run(() => Notify(model, true, 1, ""));
            try
            {
                await serial.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                serial.Connected = false; Notify(model, true, 1, "");
                serial.Release.Set(); await old.WaitAsync(TimeSpan.FromSeconds(5));
                await session.BeginAsync(Rx(), 1, 1, 1, default);
                await session.CheckFullWindowAsync(default); await session.EndStepAsync(default);
                Assert.Equal("inconclusive", Assert.Single((await store.ReadPageAsync(session.RunId)).Rows).CheckStatus);
            }
            finally { serial.Release.Set(); await old; }
        });
    }

    [Fact]
    public async Task AnOldGenerationCannotOverwriteTheNewGenerationsResetCounter()
    {
        await WithSession(async (model, serial, session, store) =>
        {
            serial.Errors = 2; Set(model, "_sequenceResultSerialErrors", 2L);
            await session.BeginAsync(Rx(), 1, 1, 1, default);
            serial.PauseNext = 1;
            var old = Task.Run(() => Notify(model, false, 1, "old"));
            try
            {
                await serial.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                serial.Generation = 2; serial.Errors = 0;
                Notify(model, true, 2, "");
                await session.CheckFullWindowAsync(default); await session.EndStepAsync(default);
                await session.BeginAsync(Rx(), 2, 1, 2, default);
                serial.Release.Set(); await old.WaitAsync(TimeSpan.FromSeconds(5));
                Notify(model, false, 2, "READY");
                await session.CheckFullWindowAsync(default); await session.EndStepAsync(default);
                var row = (await store.ReadPageAsync(session.RunId)).Rows.Last();
                Assert.Equal("matched", row.CheckStatus); Assert.Null(row.Error);
                Assert.Equal(0L, Get(model, "_sequenceResultSerialErrors"));
            }
            finally { serial.Release.Set(); await old; }
        });
    }

    private static SequenceRunStep Rx() => new(1, "READY", "Terminal", "None", 0, new("text", "READY"), "", "RxCheck");
    private static async Task WithSession(Func<MainViewModel, ControlledSerial, SequenceRunSession, SequenceRunStore, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "SequenceCallbackRaceTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SequenceRunStore(root);
            await using var session = await SequenceRunSession.StartAsync(store, new("race", 2, "MOCK", "Utf8", [Rx()]), Encoding.UTF8, default);
            var serial = DispatchProxy.Create<ISerialService, ControlledSerial>();
            var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
            Set(model, "_serialService", serial); Set(model, "_sequenceRunSession", session);
            // Optional only so this regression also runs against the original code.
            typeof(MainViewModel).GetField("_sequenceResultGate", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(model, new object());
            typeof(MainViewModel).GetField("_sequenceResultGeneration", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(model, 1L);
            await action(model, (ControlledSerial)serial, session, store);
            await session.FinishAsync("completed");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Notify(MainViewModel model, bool status, long generation, string text)
    {
        var method = status ? "OnSequenceResultConnection" : "OnSequenceResultBytes";
        object?[] args = status ? [null, EventArgs.Empty] : [generation, new ReceivedByteChunk(Encoding.UTF8.GetBytes(text), Stopwatch.GetTimestamp())];
        typeof(MainViewModel).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, args);
    }
    private static object? Get(MainViewModel model, string field) => typeof(MainViewModel).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(model);
    private static void Set(MainViewModel model, string field, object value) => typeof(MainViewModel).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(model, value);

    public class ControlledSerial : DispatchProxy
    {
        public long Errors, Generation = 1;
        public bool Connected = true;
        public int PauseNext;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_IsConnected": return Volatile.Read(ref Connected);
                case "get_ReceiveSessionGeneration": return Volatile.Read(ref Generation);
                case "get_SerialFrameErrorCount":
                case "get_SerialParityErrorCount":
                case "get_SerialOverrunErrorCount": return 0L;
                case "get_SerialRxOverErrorCount":
                    var value = Volatile.Read(ref Errors);
                    if (Interlocked.Exchange(ref PauseNext, 0) == 1)
                    { Entered.TrySetResult(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
                    return value;
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
