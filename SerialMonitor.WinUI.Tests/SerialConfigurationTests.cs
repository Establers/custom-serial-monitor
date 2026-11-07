using System.Reflection;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialConfigurationTests
{
    [Fact]
    public async Task CancellationAtDriverReadbackNeverReturnsAnUnpublishedConfiguration()
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            await using var serial = new SerialService();
            await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
            using var stop = new CancellationTokenSource();
            var session = typeof(SerialService).GetField("_currentReceiveSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(serial)!;
            var runtime = (SerialControlLineRuntime)session.GetType().GetProperty("ControlLines")!.GetValue(session)!;
            var port = new CancelAtReadbackPort(stop);
            typeof(SerialControlLineRuntime).GetField("_port", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, port);
            var requested = serial.AppliedConfiguration! with { BaudRate = 57600 };
            try
            {
                var applied = await serial.ConfigureAsync(requested, serial.ReceiveSessionGeneration, stop.Token);
                Assert.Equal(applied, serial.AppliedConfiguration);
                Assert.Equal(applied.BaudRate, serial.AppliedBaudRate);
            }
            catch (OperationCanceledException)
            {
                // A late write may force retirement, but never keep an active
                // connection reporting an old configuration after that write.
                if (port.Changed) Assert.False(serial.IsConnected);
            }
        }
    }

    [Fact]
    public async Task LiveFramingAndFlowChangesKeepSessionAndPendingTerminalBytes()
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        var generation = serial.ReceiveSessionGeneration;
        var reader = serial.ReceivedBytes;
        await serial.SetControlLineAsync(SerialControlLine.Dtr, true, generation, default);
        var requested = serial.AppliedConfiguration! with { BaudRate = 57600, DataBits = 7,
            Parity = SerialParityMode.Even, StopBits = SerialStopBitsMode.Two, Handshake = SerialHandshakeMode.XOn };
        var changes = serial.ConfigureAsync(requested, generation, default);
        var keys = serial.SendTerminalInputAsync([0x41, 0x42, 0x43], generation, default);
        Assert.Equal(requested, await changes);
        await keys;
        Assert.True(serial.IsConnected);
        Assert.Equal(generation, serial.ReceiveSessionGeneration);
        Assert.Same(reader, serial.ReceivedBytes);
        Assert.Equal(3, serial.WrittenByteCount);
        Assert.Equal(57600, serial.AppliedBaudRate);
        Assert.Equal(SerialControlLineMode.On, serial.ControlLineStatus.Dtr);
    }

    [Fact]
    public async Task InvalidSettingsAndStaleGenerationCannotChangeTheConnection()
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        var original = serial.AppliedConfiguration!;
        var generation = serial.ReceiveSessionGeneration;
        await Assert.ThrowsAsync<ArgumentException>(() => serial.ConfigureAsync(original with
            { DataBits = 4 }, generation, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => serial.ConfigureAsync(original with
            { BaudRate = 57600 }, generation + 1, default));
        Assert.Equal(original, serial.AppliedConfiguration);
        Assert.True(serial.IsConnected);
    }

    [Fact]
    public async Task CancellationBehindAnExistingWriterLeavesConfigurationUntouched()
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        var original = serial.AppliedConfiguration!;
        var gate = (SemaphoreSlim)typeof(SerialService).GetField("_writeGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(serial)!;
        await gate.WaitAsync();
        try
        {
            using var stop = new CancellationTokenSource();
            var operation = serial.ConfigureAsync(original with { BaudRate = 57600 }, serial.ReceiveSessionGeneration, stop.Token);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(original, serial.AppliedConfiguration);
            Assert.True(serial.IsConnected);
        }
        finally { gate.Release(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARejectedDriverConfigurationKeepsOldSettingsOrFaultsIfUnverified(bool restored)
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        var original = serial.AppliedConfiguration!;
        var session = typeof(SerialService).GetField("_currentReceiveSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(serial)!;
        var runtime = (SerialControlLineRuntime)session.GetType().GetProperty("ControlLines")!.GetValue(session)!;
        typeof(SerialControlLineRuntime).GetField("_port", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, new RejectingPort(restored));
        await Assert.ThrowsAsync<SerialPortConfigurationException>(() => serial.ConfigureAsync(original with
            { BaudRate = 57600 }, serial.ReceiveSessionGeneration, default));
        Assert.Equal(restored, serial.IsConnected);
        if (restored) Assert.Equal(original, serial.AppliedConfiguration);
        else Assert.Equal(SerialConnectionState.Faulted, serial.ConnectionState);
    }

    private sealed class RejectingPort(bool restored) : ISerialControlLinePort
    {
        public (SerialControlLineMode Dtr, SerialControlLineMode Rts) ReadModes() => (SerialControlLineMode.Off, SerialControlLineMode.Off);
        public (bool Cts, bool Dsr, bool Dcd, bool Ri) ReadInputs() => (false, false, false, false);
        public void WriteMode(SerialControlLine line, SerialControlLineMode mode) { }
        public SerialPortConfiguration Configure(SerialPortConfiguration configuration) =>
            throw new SerialPortConfigurationException("Injected driver rejection", restored, new IOException("rejected"));
    }

    private sealed class CancelAtReadbackPort(CancellationTokenSource stop) : ISerialControlLinePort
    {
        internal volatile bool Changed;
        public (SerialControlLineMode Dtr, SerialControlLineMode Rts) ReadModes() => (SerialControlLineMode.Off, SerialControlLineMode.Off);
        public (bool Cts, bool Dsr, bool Dcd, bool Ri) ReadInputs() => (false, false, false, false);
        public void WriteMode(SerialControlLine line, SerialControlLineMode mode) { }
        public SerialPortConfiguration Configure(SerialPortConfiguration configuration)
        {
            Changed = true;
            stop.Cancel();
            return configuration;
        }
    }
}
