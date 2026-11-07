using Microsoft.Win32;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

// The explicit, registry-verified emulator pair is the only permitted target.
public sealed class SerialControlLineVirtualDriverTests
{
    [Com0comPair0Fact]
    public async Task NativeLiveFramingAndEveryHandshakeModeReadBackWithoutReopening()
    {
        VerifyPair();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var serial = new SerialService();
        await using var peer = new SerialService();
        await serial.ConnectAsync(new() { PortName = "CNCA0" }, new(), timeout.Token);
        await peer.ConnectAsync(new() { PortName = "CNCB0", RtsEnable = true, DtrEnable = true }, new(), timeout.Token);
        var generation = serial.ReceiveSessionGeneration;
        var reader = serial.ReceivedBytes;
        var session = typeof(SerialService).GetField("_currentReceiveSession",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(serial)!;
        var port = Assert.IsType<BoundaryPreservingSerialPortStream>(session.GetType().GetProperty("Port")!.GetValue(session));
        foreach (var handshake in Enum.GetValues<SerialHandshakeMode>())
        {
            var requested = serial.AppliedConfiguration! with { BaudRate = 57600, DataBits = 7,
                Parity = SerialParityMode.Even, StopBits = SerialStopBitsMode.Two, Handshake = handshake };
            await serial.ConfigureAsync(requested, generation, timeout.Token);
            Assert.Equal(57600, port.ReadOpenPortBaudRate());
            Assert.Equal(7, port.DataBits);
            Assert.Equal(RJCP.IO.Ports.Parity.Even, port.Parity);
            Assert.Equal(RJCP.IO.Ports.StopBits.Two, port.StopBits);
            Assert.Equal(handshake.ToString(), port.Handshake.ToString());
        }
        await serial.ConfigureAsync(serial.AppliedConfiguration! with { Handshake = SerialHandshakeMode.None }, generation, timeout.Token);
        await peer.ConfigureAsync(peer.AppliedConfiguration! with { BaudRate = 57600, DataBits = 7,
            Parity = SerialParityMode.Even, StopBits = SerialStopBitsMode.Two }, peer.ReceiveSessionGeneration, timeout.Token);
        await serial.SendTerminalInputAsync([0x41, 0x42, 0x43], generation, timeout.Token);
        Assert.Equal(new byte[] { 0x41, 0x42, 0x43 }, await ReadBytesAsync(peer, 3, timeout.Token));
        Assert.Equal(generation, serial.ReceiveSessionGeneration);
        Assert.Same(reader, serial.ReceivedBytes);
    }

    [Com0comPair0Fact]
    public async Task LiveOutputsReachPeerInputSignalsWithoutReopeningOrDroppingBytes()
    {
        VerifyPair();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var first = new SerialService();
        await using var peer = new SerialService();
        await first.ConnectAsync(new() { PortName = "CNCA0" }, new(), timeout.Token);
        await peer.ConnectAsync(new() { PortName = "CNCB0" }, new(), timeout.Token);
        var generation = first.ReceiveSessionGeneration;
        var reader = first.ReceivedBytes;
        foreach (var on in new[] { true, false, true, false })
        {
            await first.SetControlLineAsync(SerialControlLine.Rts, on, generation, timeout.Token);
            await first.SetControlLineAsync(SerialControlLine.Dtr, on, generation, timeout.Token);
            await UntilAsync(() => peer.ControlLineStatus.InputStatus == SerialModemStatusKind.Available &&
                peer.ControlLineStatus.Cts == on && peer.ControlLineStatus.Dsr == on && peer.ControlLineStatus.Dcd == on, timeout.Token);
            Assert.Equal(on, first.ControlLineStatus.Rts == SerialControlLineMode.On);
            Assert.Equal(on, first.ControlLineStatus.Dtr == SerialControlLineMode.On);
            Assert.NotNull(peer.ControlLineStatus.Ri);
            await peer.SendBytesAsync([0x00, 0xAA, 0x55, 0xFF], "", timeout.Token);
            Assert.Equal(new byte[] { 0x00, 0xAA, 0x55, 0xFF }, await ReadBytesAsync(first, 4, timeout.Token));
        }
        Assert.Same(reader, first.ReceivedBytes);
        Assert.Equal(generation, first.ReceiveSessionGeneration);
        Assert.Equal(0, first.ConnectionErrorCount);
    }

    [Com0comPair0Fact]
    public async Task NativeTxToggleSurvivesLiveBaudChangeAndReturnsToManualControl()
    {
        VerifyPair();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var first = new SerialService();
        await using var peer = new SerialService();
        await first.ConnectAsync(new() { PortName = "CNCA0", RtsToggleOnTransmit = true }, new(), timeout.Token);
        await peer.ConnectAsync(new() { PortName = "CNCB0" }, new(), timeout.Token);
        await UntilAsync(() => first.ControlLineStatus.Rts == SerialControlLineMode.Toggle, timeout.Token);
        var generation = first.ReceiveSessionGeneration;
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.SetControlLineAsync(SerialControlLine.Rts, true, generation, timeout.Token));
        Assert.Equal(57600, await first.ChangeBaudRateAsync(57600, timeout.Token));
        var session = typeof(SerialService).GetField("_currentReceiveSession",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(first)!;
        var port = Assert.IsType<BoundaryPreservingSerialPortStream>(session.GetType().GetProperty("Port")!.GetValue(session));
        Assert.Equal(SerialControlLineMode.Toggle, port.CreateControlLinePort().ReadModes().Rts);
        await UntilAsync(() => first.ControlLineStatus.Rts == SerialControlLineMode.Toggle, timeout.Token);
        await first.SendBytesAsync([1, 2, 3, 4, 5], "", timeout.Token);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await ReadBytesAsync(peer, 5, timeout.Token));
        await first.SetRtsToggleAsync(false, generation, timeout.Token);
        Assert.Equal(SerialControlLineMode.Off, first.ControlLineStatus.Rts);
        await first.SetControlLineAsync(SerialControlLine.Rts, true, generation, timeout.Token);
        await UntilAsync(() => peer.ControlLineStatus.Cts == true, timeout.Token);
        Assert.Equal(generation, first.ReceiveSessionGeneration);
    }

    [Com0comPair0Fact]
    public async Task NativeHardwareHandshakeCannotBeOverriddenByManualCommands()
    {
        VerifyPair();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var serial = new SerialService();
        await serial.ConnectAsync(new() { PortName = "CNCA0", Handshake = SerialHandshakeMode.DtrRts }, new(), timeout.Token);
        await UntilAsync(() => serial.ControlLineStatus.Dtr == SerialControlLineMode.Handshake &&
            serial.ControlLineStatus.Rts == SerialControlLineMode.Handshake, timeout.Token);
        var generation = serial.ReceiveSessionGeneration;
        foreach (var line in Enum.GetValues<SerialControlLine>())
            await Assert.ThrowsAsync<InvalidOperationException>(() => serial.SetControlLineAsync(line, false, generation, timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => serial.SetRtsToggleAsync(true, generation, timeout.Token));
        Assert.Equal(SerialControlLineMode.Handshake, serial.ControlLineStatus.Dtr);
        Assert.Equal(SerialControlLineMode.Handshake, serial.ControlLineStatus.Rts);
        Assert.True(serial.IsConnected);
    }

    private static void VerifyPair()
    {
        foreach (var name in new[] { "CNCA0", "CNCB0" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\com0com\port\" + name);
            Assert.NotNull(key);
            Assert.Equal("com0com", key.GetValue("Service"));
            using var parameters = key.OpenSubKey("Device Parameters");
            Assert.Equal(name, parameters?.GetValue("PortName"));
        }
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    { while (!condition()) await Task.Delay(10, token); }

    private static async Task<byte[]> ReadBytesAsync(SerialService serial, int count, CancellationToken token)
    {
        var result = new List<byte>();
        while (result.Count < count) result.AddRange((await serial.ReceivedBytes.ReadAsync(token)).Bytes);
        return result.ToArray();
    }
}
