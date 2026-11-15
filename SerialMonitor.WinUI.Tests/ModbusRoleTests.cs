using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;
using SerialMonitor.Core.Protocols;

namespace SerialMonitor.WinUI.Tests;
public sealed class ModbusRoleTests
{
    [Fact] public async Task ObserveBlocksAllTransmitEntryPointsIncludingExclusiveWriters()
    {
        await using var serial = new SerialService();
        serial.SetTransmitMode(SerialTransmitMode.Observe);
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => serial.SendAsync(new("test","hello"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => serial.SendBytesAsync([1],"test",default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => serial.SendTerminalInputAsync([1], serial.ReceiveSessionGeneration, default));
        await using (var lease = serial.AcquireExclusiveSession("Modbus"))
            await Assert.ThrowsAsync<InvalidOperationException>(() => lease.SendAsync([1], default));
        Assert.Equal(0, serial.WrittenByteCount);
        serial.SetTransmitMode(SerialTransmitMode.DirectTest);
        await serial.SendBytesAsync([1],"test",default); Assert.Equal(1, serial.WrittenByteCount);
    }
    [Fact] public async Task SensorOnlyAllowsItsLeaseAndCannotSwitchRoleWhileReserved()
    {
        await using var serial = new SerialService();
        serial.SetTransmitMode(SerialTransmitMode.Sensor);
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => serial.SendBytesAsync([1],"test",default));
        await using (var other = serial.AcquireExclusiveSession("Modbus"))
            await Assert.ThrowsAsync<InvalidOperationException>(() => other.SendAsync([1],default));
        await using var lease = serial.AcquireExclusiveSession(ModbusSensorService.Owner);
        Assert.Throws<InvalidOperationException>(() => serial.SetTransmitMode(SerialTransmitMode.Observe));
        await lease.SendAsync([1],default); Assert.Equal(1,serial.WrittenByteCount);
    }
    [Fact] public async Task RejectedRoleChangeKeepsTheCurrentViewAndSensorIsSeparate()
    {
        await using var analyzer = new ProtocolAnalyzer();
        var model = new ProtocolViewModel(analyzer) { SelectedViewIndex = 1 };
        model.TryChangeRole = _ => false; model.SelectedViewIndex = 2; Assert.Equal(1,model.SelectedViewIndex);
        model.TryChangeRole = _ => true; model.SelectedViewIndex = 4;
        Assert.True(model.IsSensorSelected); Assert.False(model.IsRegisterMonitorSelected);
        model.SelectedViewIndex = 2; Assert.False(model.IsSensorSelected); Assert.True(model.IsRegisterMonitorSelected);
    }
}
