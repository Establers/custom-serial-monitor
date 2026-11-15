using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;
public sealed class ModbusSensorLiveValueTests
{
    private static ModbusSensorDefinition Definition(int delay=0) => new() {SchemaVersion=1,Name="Live",UnitId=1,ResponseDelayMs=delay,
        Points=[new() {Name="Input",Area=4,Address=0,InitialValue="25"},new() {Name="Setting",Area=3,Address=0,InitialValue="10",Writable=true}]};
    private sealed class Rig : IAsyncDisposable
    {
        public readonly ISerialService Serial = DispatchProxy.Create<ISerialService,ModbusRtuMonitorServiceTests.TestSerial>();
        public ModbusRtuMonitorServiceTests.TestSerial Device => (ModbusRtuMonitorServiceTests.TestSerial)(object)Serial;
        public readonly ModbusSensorSession Session = new();
        public readonly CancellationTokenSource Stop = new(TimeSpan.FromSeconds(8));
        public readonly Channel<byte[]> Replies = Channel.CreateUnbounded<byte[]>();
        public Task Run { get; }
        public Rig(int delay=0) => Run = new ModbusSensorService(Serial,(bytes,_)=>Replies.Writer.TryWrite(bytes)).RunAsync(Definition(delay),()=>{},Stop.Token,Session);
        public async Task<byte[]> Read(byte area)
        { Device.Emit(ModbusRtuRequestBuilder.Read(1,area,0,1)); return await Replies.Reader.ReadAsync(Stop.Token); }
        public async ValueTask DisposeAsync() { Stop.Cancel(); await Record.ExceptionAsync(()=>Run); Stop.Dispose(); }
    }
    [Fact] public async Task LocalChangesAndResetsDoNotTransmitAndNextReadUsesTheNewValue()
    {
        await using var rig = new Rig();
        await rig.Session.SetValueAsync(0,"35"); Assert.Empty(rig.Device.Sent);
        Assert.Equal("0104020023F8E9",Convert.ToHexString(await rig.Read(4)));
        Assert.Equal(ModbusSensorChangeSource.User,rig.Session.Snapshot!.Points[0].Source);
        await rig.Session.ResetAsync(0); Assert.Single(rig.Device.Sent);
        Assert.Equal((ushort)25,rig.Session.Snapshot.Points[0].Words[0]);
        rig.Device.Emit(ModbusRtuRequestBuilder.WriteSingle(1,6,0,30));
        await rig.Replies.Reader.ReadAsync(rig.Stop.Token);
        Assert.Equal(ModbusSensorChangeSource.Master,rig.Session.Snapshot.Points[1].Source);
        await rig.Session.ResetAsync(); Assert.Equal((ushort)10,rig.Session.Snapshot.Points[1].Words[0]);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task StopOrDisconnectCompletesQueuedEditsWithoutApplyingThem(bool disconnect)
    {
        await using var rig = new Rig(600);
        rig.Device.Emit(ModbusRtuRequestBuilder.Read(1,4,0,1));
        var edit = rig.Session.SetValueAsync(0,"35");
        if (disconnect) rig.Device.Disconnect(); else rig.Stop.Cancel();
        await Record.ExceptionAsync(()=>rig.Run.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.NotNull(await Record.ExceptionAsync(()=>edit.WaitAsync(TimeSpan.FromSeconds(2))));
        Assert.True(edit.IsCompleted); Assert.False(rig.Session.IsRunning);
        Assert.Equal((ushort)25,rig.Session.Snapshot!.Points[0].Words[0]); Assert.Null(rig.Device.Owner);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>rig.Session.SetValueAsync(0,"50"));
    }
    [Fact] public async Task CancelledQueuedEditIsSkippedAndTheSessionContinues()
    {
        await using var rig = new Rig();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Device.OnSend = async _=> { entered.TrySetResult(); await release.Task; };
        var read = rig.Read(4); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancel = new CancellationTokenSource();
        var edit = rig.Session.SetValueAsync(0,"35",cancel.Token); cancel.Cancel();
        release.TrySetResult(); await read;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>edit);
        await rig.Session.SetValueAsync(1,"20"); // Barrier after the cancelled command.
        Assert.Equal((ushort)25,rig.Session.Snapshot!.Points[0].Words[0]); Assert.True(rig.Session.IsRunning);
    }
    [Fact] public async Task FullControlQueueRejectsNewWorkAndStopSettlesAllPendingEdits()
    {
        await using var rig = new Rig(1000);
        rig.Device.Emit(ModbusRtuRequestBuilder.Read(1,4,0,1));
        var edits = Enumerable.Range(0,70).Select(_=>rig.Session.SetValueAsync(0,"35")).ToArray();
        Assert.Contains(edits,task=>task.IsFaulted);
        rig.Stop.Cancel(); await Record.ExceptionAsync(()=>rig.Run);
        await Record.ExceptionAsync(()=>Task.WhenAll(edits).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.All(edits,task=>Assert.True(task.IsCompleted)); Assert.Empty(rig.Device.Sent);
    }
    private static async Task Ready(ModbusSensorViewModel model)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (model.LiveSession?.IsRunning != true) await Task.Delay(5,deadline.Token);
        model.Refresh();
    }
    [Fact] public async Task ViewModelKeepsRowsAndSelectionStableAndRejectsAnOldEditorAfterRestart()
    {
        var serial = DispatchProxy.Create<ISerialService,ModbusRtuMonitorServiceTests.TestSerial>();
        var model = new ModbusSensorViewModel(new(serial,(_,_)=>{}),()=>true,()=>{});
        model.Json = JsonSerializer.Serialize(Definition(),ModbusSensorDefinition.JsonOptions); model.Validate();
        var json = model.Json; var rows = model.Points; var row = rows[0]; model.SelectedPoint = row;
        Assert.Equal("초기값",model.ValueColumnHeader); Assert.False(model.CanChangeValues);
        var run = model.StartAsync(); await Ready(model); var session = model.LiveSession!;
        await model.ChangeValueAsync(session,row,"35");
        Assert.Equal("35",row.CurrentValue); Assert.Equal("사용자 변경",row.ChangeSource); Assert.True(row.HighlightOpacity>0);
        for (var i=0;i<100;i++) model.Refresh();
        Assert.Same(rows,model.Points); Assert.Same(row,model.SelectedPoint); Assert.Equal(json,model.Json);
        await model.StopAsync(); await run; Assert.Equal("마지막 값",model.ValueColumnHeader); Assert.Equal("35",row.CurrentValue);
        var second = model.StartAsync(); await Ready(model);
        try
        {
            Assert.Equal("25",row.CurrentValue); Assert.Equal("초기값",row.ChangeSource);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>model.ChangeValueAsync(session,row,"99"));
            await model.ChangeValueAsync(model.LiveSession!,row,"40");
            await model.RestoreValuesAsync(model.LiveSession!,row); Assert.Equal("25",row.CurrentValue); Assert.Equal("초기값 복원",row.ChangeSource);
        }
        finally { await model.StopAsync(); await second; }
    }
    [Fact] public async Task InvalidValueKeepsTheLiveValueAndAllowsCorrection()
    {
        var serial = DispatchProxy.Create<ISerialService,ModbusRtuMonitorServiceTests.TestSerial>();
        var model = new ModbusSensorViewModel(new(serial,(_,_)=>{}),()=>true,()=>{});
        model.Json = JsonSerializer.Serialize(Definition(),ModbusSensorDefinition.JsonOptions); model.Validate();
        var run = model.StartAsync(); await Ready(model); var row = model.Points[0];
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(()=>model.ChangeValueAsync(model.LiveSession!,row,"NaN"));
            Assert.Equal("25",row.CurrentValue); Assert.True(model.CanChangeValues);
            await model.ChangeValueAsync(model.LiveSession!,row,"30"); Assert.Equal("30",row.CurrentValue);
        }
        finally { await model.StopAsync(); await run; }
    }
}
