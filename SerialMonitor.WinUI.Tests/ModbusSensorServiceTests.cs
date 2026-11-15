using System.Reflection;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;
public sealed class ModbusSensorServiceTests
{
    [Fact] public async Task BundledEe071ExampleLoadsPassivelyAndShowsItsScope()
    {
        var serial = DispatchProxy.Create<ISerialService, ModbusRtuMonitorServiceTests.TestSerial>();
        var device = (ModbusRtuMonitorServiceTests.TestSerial)(object)serial;
        var model = new SerialMonitor.WinUI.ViewModels.ModbusSensorViewModel(new(serial, (_,_) => {}), () => true, () => {});
        await model.LoadExampleAsync();
        Assert.True(model.StartCommand.CanExecute(null));
        Assert.Contains("EE071", model.Summary); Assert.Contains("9600", model.Summary);
        Assert.Contains("초기값", model.Summary); Assert.Contains("재현하지 않음", model.Summary);
        Assert.Empty(device.Sent); Assert.Null(device.Owner);
    }

    [Fact] public async Task RestartResetsWritesAndPreviousConnectionBytesAreIgnored()
    {
        var serial = DispatchProxy.Create<ISerialService, ModbusRtuMonitorServiceTests.TestSerial>();
        var device = (ModbusRtuMonitorServiceTests.TestSerial)(object)serial;
        var replies = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        var service = new ModbusSensorService(serial, (bytes, _) => replies.Writer.TryWrite(bytes));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using (var stop = new CancellationTokenSource())
        {
            var run = service.RunAsync(Definition(), () => {}, stop.Token);
            try
            {
                device.Emit(ModbusRtuRequestBuilder.WriteSingle(1,6,0,100), generation: 0);
                var write = ModbusRtuRequestBuilder.WriteSingle(1,6,0,100);
                device.Emit(write);
                Assert.Equal(write, await replies.Reader.ReadAsync(timeout.Token));
                device.Emit(ModbusRtuRequestBuilder.Read(1,3,0,1));
                var response = await replies.Reader.ReadAsync(timeout.Token);
                Assert.Equal(new byte[] {0,100}, response[3..^2]); Assert.Equal(2, device.Sent.Count);
            }
            finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
        }
        using (var stop = new CancellationTokenSource())
        {
            var run = service.RunAsync(Definition(), () => {}, stop.Token);
            try
            {
                device.Emit(ModbusRtuRequestBuilder.Read(1,3,0,1));
                Assert.Equal("01030200FA3807", Convert.ToHexString(await replies.Reader.ReadAsync(timeout.Token)));
            }
            finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
        }
    }

    [Fact] public async Task TruncatedVariableLengthNoiseDoesNotHideACompleteRequestAtIdleBoundary()
    {
        var serial = DispatchProxy.Create<ISerialService, ModbusRtuMonitorServiceTests.TestSerial>();
        var device = (ModbusRtuMonitorServiceTests.TestSerial)(object)serial;
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ModbusSensorService(serial, (bytes, _) => received.TrySetResult(bytes));
        using var stop = new CancellationTokenSource();
        var run = service.RunAsync(Definition(), () => {}, stop.Token);
        try
        {
            byte[] noise = [1, 16, 0, 0, 0, 100, 200];
            device.Emit(noise.Concat(ModbusRtuRequestBuilder.Read(1,3,0,1)).ToArray(), endsAtIdleBoundary: true);
            Assert.Equal("01030200FA3807", Convert.ToHexString(await received.Task.WaitAsync(TimeSpan.FromSeconds(2))));
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
    }

    private static ModbusSensorDefinition Definition(int delay = 0) => new() { SchemaVersion = 1, Name = "Test", UnitId = 1, ResponseDelayMs = delay,
        Points = [new() { Name = "Value", Area = 3, Address = 0, InitialValue = "250", Writable = true }] };
    [Fact] public async Task WaitsWithoutSendingThenRespondsToSplitRequestAndStopsCleanly()
    {
        var serial = DispatchProxy.Create<ISerialService, ModbusRtuMonitorServiceTests.TestSerial>();
        var device = (ModbusRtuMonitorServiceTests.TestSerial)(object)serial;
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ModbusSensorService(serial, (bytes, _) => received.TrySetResult(bytes));
        using var stop = new CancellationTokenSource();
        var run = service.RunAsync(Definition(), () => {}, stop.Token);
        Assert.Empty(device.Sent);
        var request = ModbusRtuRequestBuilder.Read(1,3,0,1);
        device.Emit(request[..3]); Assert.Empty(device.Sent); device.Emit(request[3..]);
        Assert.Equal("01030200FA3807", Convert.ToHexString(await received.Task.WaitAsync(TimeSpan.FromSeconds(3))));
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        device.Emit(request); Assert.Single(device.Sent); Assert.Null(device.Owner);
    }
    [Fact] public async Task CancelDuringResponseDelayDoesNotSendAndDisconnectEndsRun()
    {
        var serial = DispatchProxy.Create<ISerialService, ModbusRtuMonitorServiceTests.TestSerial>();
        var device = (ModbusRtuMonitorServiceTests.TestSerial)(object)serial;
        var service = new ModbusSensorService(serial, (_, _) => {});
        using var stop = new CancellationTokenSource();
        var run = service.RunAsync(Definition(1000), () => {}, stop.Token);
        device.Emit(ModbusRtuRequestBuilder.Read(1,3,0,1));
        await Task.Delay(30); stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(device.Sent); Assert.Null(device.Owner);
        var second = service.RunAsync(Definition(), () => {}, CancellationToken.None);
        device.Disconnect(); await Assert.ThrowsAsync<IOException>(() => second);
        Assert.Empty(device.Sent); Assert.Null(device.Owner);
    }

    [Fact] public async Task ImportIsPassiveAndEditingInvalidatesTheValidatedDefinition()
    {
        var serial = DispatchProxy.Create<ISerialService, ModbusRtuMonitorServiceTests.TestSerial>();
        var device = (ModbusRtuMonitorServiceTests.TestSerial)(object)serial;
        var model = new SerialMonitor.WinUI.ViewModels.ModbusSensorViewModel(new(serial, (_,_) => {}), () => true, () => {});
        model.Json = System.Text.Json.JsonSerializer.Serialize(Definition(), ModbusSensorDefinition.JsonOptions);
        model.Validate(); Assert.True(model.StartCommand.CanExecute(null)); Assert.Empty(device.Sent);
        model.Json += "x"; Assert.False(model.StartCommand.CanExecute(null));
        model.Validate(); Assert.False(model.StartCommand.CanExecute(null)); Assert.Equal("JSON 확인 실패", model.Status); Assert.Empty(device.Sent);
        model.Json = System.Text.Json.JsonSerializer.Serialize(Definition(), ModbusSensorDefinition.JsonOptions); model.Validate();
        var run = model.StartAsync(); Assert.True(model.IsBusy); Assert.False(model.ValidateCommand.CanExecute(null));
        await model.StopAsync(); await run; Assert.False(model.IsBusy); Assert.Empty(device.Sent);
    }

    [Fact] public async Task OversizedReceiveStopsWithoutSendingAndReleasesOwnership()
    {
        var serial = DispatchProxy.Create<ISerialService, ModbusRtuMonitorServiceTests.TestSerial>();
        var device = (ModbusRtuMonitorServiceTests.TestSerial)(object)serial;
        var service = new ModbusSensorService(serial, (_, _) => {});
        var run = service.RunAsync(Definition(), () => {}, CancellationToken.None);
        device.Emit(new byte[65537]);
        await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Empty(device.Sent); Assert.Null(device.Owner);
    }

    [Fact] public async Task LocalWriteEchoStopsInsteadOfResendingForever()
    {
        var serial = DispatchProxy.Create<ISerialService, ModbusRtuMonitorServiceTests.TestSerial>();
        var device = (ModbusRtuMonitorServiceTests.TestSerial)(object)serial;
        var service = new ModbusSensorService(serial, (bytes, _) => device.Emit(bytes));
        var run = service.RunAsync(Definition(), () => {}, CancellationToken.None);
        device.Emit(ModbusRtuRequestBuilder.WriteSingle(1,6,0,100));
        await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Single(device.Sent); Assert.Null(device.Owner);
    }
}
