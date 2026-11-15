using System.Diagnostics;
using System.Reflection;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;
public sealed class ModbusResponseTimingTests
{
    [Theory]
    [InlineData(1200,32.083333)] [InlineData(9600,4.010416)] [InlineData(19200,2.005208)]
    [InlineData(38400,1.75)] [InlineData(115200,1.75)]
    public void FrameGapFollowsSerialLineSpecification(int baud, double expected)
    {
        Assert.InRange(new ModbusResponseTiming(baud,null).FrameGap.TotalMilliseconds,expected-0.001,expected+0.001);
    }
    [Fact] public void NonstandardLongCharacterAndQueuedTxAreConservativelyAccountedFor()
    {
        var config = new SerialPortConfiguration(9600,8,SerialParityMode.Even,SerialStopBitsMode.Two,SerialHandshakeMode.None,false,false,false);
        var timing = new ModbusResponseTiming(9600,config);
        Assert.Equal(12,timing.BitsPerCharacter);
        Assert.Equal(4.375,timing.FrameGap.TotalMilliseconds);
        Assert.Equal(10,timing.WireDuration(8).TotalMilliseconds);
    }
    [Fact] public async Task ResponsesWaitForSilenceAndForPreviousQueuedTransmitDuration()
    {
        var serial = DispatchProxy.Create<ISerialService,ModbusRtuMonitorServiceTests.TestSerial>();
        var device = (ModbusRtuMonitorServiceTests.TestSerial)(object)serial; device.BaudRate = 1200;
        var starts = System.Threading.Channels.Channel.CreateUnbounded<long>();
        device.OnSend = async _ => { starts.Writer.TryWrite(Stopwatch.GetTimestamp()); await Task.Delay(120); };
        var times = System.Threading.Channels.Channel.CreateUnbounded<long>();
        var service = new ModbusSensorService(serial,(_,_) => times.Writer.TryWrite(Stopwatch.GetTimestamp()));
        using var stop = new CancellationTokenSource();
        var run = service.RunAsync(new() { SchemaVersion=1,Name="Timing",UnitId=1,
            Points=[new() {Name="P",Area=3,Address=0,InitialValue="1"}]},()=>{},stop.Token);
        try
        {
            var before = Stopwatch.GetTimestamp();
            var request = ModbusRtuRequestBuilder.Read(1,3,0,1);
            device.Emit([..request,..request]);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var firstStart = await starts.Reader.ReadAsync(deadline.Token);
            var firstCompleted = await times.Reader.ReadAsync(deadline.Token);
            var secondStart = await starts.Reader.ReadAsync(deadline.Token);
            await times.Reader.ReadAsync(deadline.Token);
            Assert.True(Stopwatch.GetElapsedTime(before,firstStart).TotalMilliseconds >= 32);
            // Count the queued wire time and gap after SendAsync completes, not when it starts.
            Assert.True(Stopwatch.GetElapsedTime(firstCompleted,secondStart).TotalMilliseconds >= 96);
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>run); }
    }
    [Fact] public async Task CancellationDuringSilenceGuardSendsNothing()
    {
        var serial = DispatchProxy.Create<ISerialService,ModbusRtuMonitorServiceTests.TestSerial>();
        var device = (ModbusRtuMonitorServiceTests.TestSerial)(object)serial; device.BaudRate = 300;
        var service = new ModbusSensorService(serial,(_,_)=>{});
        using var stop = new CancellationTokenSource();
        var run = service.RunAsync(new() {SchemaVersion=1,Name="Timing",UnitId=1,
            Points=[new() {Name="P",Area=3,Address=0,InitialValue="1"}]},()=>{},stop.Token);
        device.Emit(ModbusRtuRequestBuilder.Read(1,3,0,1));
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>run);
        Assert.Empty(device.Sent); Assert.Null(device.Owner);
    }
}
