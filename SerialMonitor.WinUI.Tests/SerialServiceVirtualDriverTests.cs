using System.Reflection;
using System.Diagnostics;
using Microsoft.Win32;
using RJCP.IO.Ports;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using Xunit.Abstractions;

namespace SerialMonitor.WinUI.Tests;

// Explicit opt-in prevents CI and ordinary test runs from opening any ports.
// This fixture only permits the registry-verified com0com emulator pair 0;
// environment variables cannot redirect it to a physical device.
public sealed class SerialServiceVirtualDriverTests(ITestOutputHelper output)
{
    [Com0comPair0Fact]
    public async Task SerialSettingsReachTheWindowsDriverForEveryFrameAndHandshakeOption()
    {
        VerifyEmulatorEndpoint("CNCA0");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var cases = new List<SerialSettings>();
        foreach (var baud in new[] { 300, 1200, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 250000, 460800, 921600, 1000000 })
            cases.Add(new() { BaudRate = baud });
        foreach (var dataBits in new[] { 5, 6, 7, 8 })
            cases.Add(new() { DataBits = dataBits });
        foreach (var parity in Enum.GetValues<SerialParityMode>())
            cases.Add(new() { Parity = parity });
        foreach (var stopBits in Enum.GetValues<SerialStopBitsMode>())
            cases.Add(new() { DataBits = stopBits == SerialStopBitsMode.OnePointFive ? 5 : 8, StopBits = stopBits });
        foreach (var handshake in Enum.GetValues<SerialHandshakeMode>())
        foreach (var dtr in new[] { false, true })
        foreach (var rts in new[] { false, true })
            cases.Add(new() { Handshake = handshake, DtrEnable = dtr, RtsEnable = rts });

        foreach (var settings in cases)
        {
            settings.PortName = "CNCA0";
            await using var service = new SerialService();
            await service.ConnectAsync(settings, new(), cancellation.Token);
            var port = GetOpenPort(service);
            // This reads GetCommState on the open Windows handle, rather than
            // merely checking the values cached before Open().
            Assert.Equal(settings.BaudRate, port.ReadOpenPortBaudRate());
            Assert.Equal(settings.DataBits, port.DataBits);
            Assert.Equal(settings.Parity.ToString(), port.Parity.ToString());
            Assert.Equal(settings.StopBits switch
            {
                SerialStopBitsMode.OnePointFive => StopBits.One5,
                SerialStopBitsMode.Two => StopBits.Two,
                _ => StopBits.One
            }, port.StopBits);
            Assert.True(settings.Handshake.ToString() == port.Handshake.ToString(),
                $"Requested {settings.Handshake}, DTR={settings.DtrEnable}, RTS={settings.RtsEnable}; driver reports {port.Handshake}.");
            if (!UsesDtrHandshake(settings.Handshake)) Assert.Equal(settings.DtrEnable, port.DtrEnable);
            if (!UsesRtsHandshake(settings.Handshake)) Assert.Equal(settings.RtsEnable, port.RtsEnable);
        }
    }

    [Com0comPair0Fact]
    public async Task HardwareHandshakePausesAndResumesActualTransmission()
    {
        VerifyEmulatorEndpoint("CNCA0");
        VerifyEmulatorEndpoint("CNCB0");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (var handshake in Enum.GetValues<SerialHandshakeMode>().Where(mode => UsesDtrHandshake(mode) || UsesRtsHandshake(mode)))
        {
            await using var peer = new SerialService();
            await using var sender = new SerialService();
            await peer.ConnectAsync(new() { PortName = "CNCB0", DtrEnable = true, RtsEnable = true }, new(), cancellation.Token);
            await sender.ConnectAsync(new() { PortName = "CNCA0", Handshake = handshake }, new(), cancellation.Token);
            var peerPort = GetOpenPort(peer);
            var senderPort = GetOpenPort(sender);
            byte[] before = [0x41, 0x42, 0x43];
            await sender.SendBytesAsync(before, "", cancellation.Token);
            Assert.Equal(before, await ReadBytesAsync(peer, before.Length, cancellation.Token));

            if (UsesRtsHandshake(handshake)) peerPort.RtsEnable = false;
            if (UsesDtrHandshake(handshake)) peerPort.DtrEnable = false;
            await WaitUntilAsync(() => (!UsesRtsHandshake(handshake) || !senderPort.CtsHolding) &&
                (!UsesDtrHandshake(handshake) || !senderPort.DsrHolding), cancellation.Token);
            byte[] paused = [0x44, 0x45, 0x46];
            await sender.SendBytesAsync(paused, "", cancellation.Token);
            await AssertNoRxAsync(peer, cancellation.Token);

            peerPort.RtsEnable = true;
            peerPort.DtrEnable = true;
            Assert.Equal(paused, await ReadBytesAsync(peer, paused.Length, cancellation.Token));
        }
    }

    [Com0comPair0Fact]
    public async Task SoftwareHandshakeConsumesXoffAndXonAndResumesQueuedTransmission()
    {
        VerifyEmulatorEndpoint("CNCA0");
        VerifyEmulatorEndpoint("CNCB0");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        foreach (var handshake in new[] { SerialHandshakeMode.XOn, SerialHandshakeMode.RtsXOn, SerialHandshakeMode.DtrXOn, SerialHandshakeMode.DtrRtsXOn })
        {
            await using var peer = new SerialService();
            await using var sender = new SerialService();
            await peer.ConnectAsync(new() { PortName = "CNCB0", DtrEnable = true, RtsEnable = true }, new(), cancellation.Token);
            await sender.ConnectAsync(new() { PortName = "CNCA0", Handshake = handshake }, new(), cancellation.Token);
            await peer.SendBytesAsync([0x13], "", cancellation.Token);
            await GetOpenPort(peer).FlushAsync(cancellation.Token);
            await Task.Delay(100, cancellation.Token);
            byte[] payload = [0x41, 0x42, 0x43];
            await sender.SendBytesAsync(payload, "", cancellation.Token);
            await AssertNoRxAsync(peer, cancellation.Token);
            await peer.SendBytesAsync([0x11], "", cancellation.Token);
            Assert.Equal(payload, await ReadBytesAsync(peer, payload.Length, cancellation.Token));
            Assert.False(sender.ReceivedBytes.TryRead(out _));
        }
    }

    [Com0comPair0Fact]
    public async Task TxEndingOptionsProduceExactBytesAndCanChangeWithoutReconnecting()
    {
        VerifyEmulatorEndpoint("CNCA0");
        VerifyEmulatorEndpoint("CNCB0");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var sender = new SerialService();
        await using var peer = new SerialService();
        await sender.ConnectAsync(new() { PortName = "CNCA0" }, new(), cancellation.Token);
        await peer.ConnectAsync(new() { PortName = "CNCB0" }, new(), cancellation.Token);
        var generation = sender.ReceiveSessionGeneration;
        foreach (var (ending, suffix) in new[]
        {
            (TxLineEndingMode.None, ""), (TxLineEndingMode.Cr, "\r"),
            (TxLineEndingMode.Lf, "\n"), (TxLineEndingMode.Crlf, "\r\n")
        })
        {
            await sender.SendAsync(new("test", "STATUS") { LineEndingMode = ending }, cancellation.Token);
            var expected = System.Text.Encoding.UTF8.GetBytes("STATUS" + suffix);
            Assert.Equal(expected, await ReadBytesAsync(peer, expected.Length, cancellation.Token));
            Assert.Equal(generation, sender.ReceiveSessionGeneration);
            await AssertNoRxAsync(peer, cancellation.Token);
        }
    }

    [Com0comPair0Fact]
    public async Task ExistingWindowsHandlesKeepBinaryRxAndTxAfterLiveBaudChange()
    {
        VerifyEmulatorEndpoint("CNCA0");
        VerifyEmulatorEndpoint("CNCB0");
        using (var pair = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT\COM0COM\0000\Device Parameters"))
            Assert.Equal(0, pair?.GetValue("PortNum"));

        await using var first = new SerialService();
        await using var second = new SerialService();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await first.ConnectAsync(new SerialSettings { PortName = "CNCA0", BaudRate = 115200 }, new(), cancellation.Token);
        await second.ConnectAsync(new SerialSettings { PortName = "CNCB0", BaudRate = 115200 }, new(), cancellation.Token);
        var firstGeneration = first.ReceiveSessionGeneration;
        var secondGeneration = second.ReceiveSessionGeneration;
        var firstChannel = first.ReceivedBytes;
        var secondChannel = second.ReceivedBytes;
        var firstPort = GetOpenPort(first);
        var secondPort = GetOpenPort(second);
        Assert.Equal(115200, firstPort.ReadOpenPortBaudRate());
        Assert.Equal(115200, secondPort.ReadOpenPortBaudRate());

        byte[] request = [0x01, 0x03, 0x00, 0xFF, 0x80, 0x0D, 0x0A, 0x00];
        await first.SendBytesAsync(request, "", cancellation.Token);
        Assert.Equal(request, await ReadBytesAsync(second, request.Length, cancellation.Token));
        var firstWritten = first.WrittenByteCount;
        var secondReceived = second.ReceivedByteCount;

        Assert.Equal(57600, await first.ChangeBaudRateAsync(57600, cancellation.Token));
        Assert.Equal(57600, await second.ChangeBaudRateAsync(57600, cancellation.Token));
        Assert.Equal(57600, firstPort.ReadOpenPortBaudRate());
        Assert.Equal(57600, secondPort.ReadOpenPortBaudRate());
        Assert.Same(firstPort, GetOpenPort(first));
        Assert.Same(secondPort, GetOpenPort(second));
        Assert.Equal(firstGeneration, first.ReceiveSessionGeneration);
        Assert.Equal(secondGeneration, second.ReceiveSessionGeneration);
        Assert.Same(firstChannel, first.ReceivedBytes);
        Assert.Same(secondChannel, second.ReceivedBytes);
        Assert.Equal(firstWritten, first.WrittenByteCount);
        Assert.Equal(secondReceived, second.ReceivedByteCount);

        // One protocol stream may arrive through several writes, while two
        // protocol packets may arrive through one write. RX must conserve the
        // original binary stream regardless of native completion boundaries.
        byte[] responsePartOne = [0xA5, 0x00];
        byte[] responsePartTwo = [0xFF, 0x10, 0x20];
        byte[] mergedPackets = [0xAA, 0x55, 0x00, 0x01, 0xAA, 0x55, 0xFF, 0x02];
        var response = responsePartOne.Concat(responsePartTwo).Concat(mergedPackets).ToArray();
        await second.SendBytesAsync(responsePartOne, "", cancellation.Token);
        await Task.Delay(15, cancellation.Token);
        await second.SendBytesAsync(responsePartTwo, "", cancellation.Token);
        await second.SendBytesAsync(mergedPackets, "", cancellation.Token);
        Assert.Equal(response, await ReadBytesAsync(first, response.Length, cancellation.Token));
        await first.SendBytesAsync(request, "", cancellation.Token);
        Assert.Equal(request, await ReadBytesAsync(second, request.Length, cancellation.Token));
        Assert.Equal(0, first.SerialFrameErrorCount + first.SerialParityErrorCount + first.SerialOverrunErrorCount + first.SerialRxOverErrorCount);
        Assert.Equal(0, second.SerialFrameErrorCount + second.SerialParityErrorCount + second.SerialOverrunErrorCount + second.SerialRxOverErrorCount);

        // Leave both emulator settings at their original speed before closing.
        Assert.Equal(115200, await first.ChangeBaudRateAsync(115200, cancellation.Token));
        Assert.Equal(115200, await second.ChangeBaudRateAsync(115200, cancellation.Token));
    }

    [Com0comPair0Fact]
    public async Task LostNativeHandleReachesTheFaultIndicatorAndARealReopenClearsIt()
    {
        VerifyEmulatorEndpoint("CNCA0");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var serial = new SerialService();
        await using var bridge = new SerialBridgeService();
        var model = ConnectionFaultIndicatorTests.ObserveSerialStatus(serial, bridge);
        await serial.ConnectAsync(new() { PortName = "CNCA0" }, new(), timeout.Token);
        await Task.Delay(600, timeout.Token); // Quiet native RX must remain healthy.
        Assert.True(serial.IsConnected);
        Assert.False(model.IsSerialConnectionFaulted);

        // Close only this test's registry-verified virtual handle. No physical
        // device, device-manager setting, or another application's port is touched.
        await Task.Run(GetOpenPort(serial).Close, timeout.Token);
        while (serial.ConnectionState != SerialConnectionState.Faulted || !model.IsSerialConnectionFaulted)
            await Task.Delay(20, timeout.Token);
        Assert.Contains("Serial receive failed", serial.LastError);
        Assert.Equal(1, serial.ConnectionErrorCount);

        await serial.DisconnectAsync(timeout.Token);
        Assert.True(model.IsSerialConnectionFaulted);
        await serial.ConnectAsync(new() { PortName = "CNCA0" }, new(), timeout.Token);
        Assert.True(serial.IsConnected);
        Assert.False(model.IsSerialConnectionFaulted);
        await serial.DisconnectAsync(timeout.Token);
        Assert.False(model.IsSerialConnectionFaulted);
    }

    [Com0comPair0Fact]
    public async Task RepeatedNativeMonitorLossDuringBinaryRxDisconnectsAndReopensPromptly()
    {
        VerifyEmulatorEndpoint("CNCA0");
        VerifyEmulatorEndpoint("CNCB0");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var receiver = new SerialService();
        await using var peer = new SerialService();
        await peer.ConnectAsync(new() { PortName = "CNCB0" }, new(), deadline.Token);
        var payload = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
        var maxDisconnectMs = 0d;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await receiver.ConnectAsync(new() { PortName = "CNCA0" },
                new() { UseNativeIdleTimeout = attempt % 2 == 0, IdleTimeoutMs = 15 }, deadline.Token);
            var generation = receiver.ReceiveSessionGeneration;
            var before = receiver.ReceivedByteCount;
            using var producing = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var writes = Task.Run(async () =>
            {
                while (true)
                {
                    producing.Token.ThrowIfCancellationRequested();
                    await peer.SendBytesAsync(payload, "", producing.Token);
                    await Task.Delay(1, producing.Token);
                }
            });
            try
            {
                await WaitUntilAsync(() => receiver.ReceivedByteCount > before, deadline.Token);
                await Task.Run(GetOpenPort(receiver).Close, deadline.Token).WaitAsync(TimeSpan.FromSeconds(3));
                await WaitUntilAsync(() => receiver.ConnectionState == SerialConnectionState.Faulted, deadline.Token);
                var timer = Stopwatch.StartNew();
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await receiver.DisconnectAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(3));
                maxDisconnectMs = Math.Max(maxDisconnectMs, timer.Elapsed.TotalMilliseconds);
                Assert.Equal(SerialConnectionState.Disconnected, receiver.ConnectionState);
                Assert.Equal(generation, receiver.ReceiveSessionGeneration);
            }
            finally
            {
                producing.Cancel();
                var error = await Record.ExceptionAsync(() => writes.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.True(error is OperationCanceledException or InvalidOperationException, error?.ToString());
            }
        }
        output.WriteLine($"20 native RX fault/reopen cycles; maximum disconnect {maxDisconnectMs:F2} ms.");
    }

    [Com0comPair0Fact]
    public async Task NativeMonitorLossDuringBlockedHardwareTxStopsTheWriteAndDisposesPromptly()
    {
        VerifyEmulatorEndpoint("CNCA0");
        VerifyEmulatorEndpoint("CNCB0");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var peer = new SerialService();
        await peer.ConnectAsync(new() { PortName = "CNCB0", RtsEnable = false }, new(), deadline.Token);
        var maxDisposeMs = 0d;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var sender = new SerialService();
            Task? writing = null;
            try
            {
                await sender.ConnectAsync(new() { PortName = "CNCA0", Handshake = SerialHandshakeMode.Rts }, new(), deadline.Token);
                var port = GetOpenPort(sender);
                await WaitUntilAsync(() => !port.CtsHolding, deadline.Token);
                var payload = new byte[128 * 1024];
                await sender.SendBytesAsync(payload, "", deadline.Token);
                await WaitUntilAsync(() => port.BytesToWrite > 0, deadline.Token);
                writing = sender.SendBytesAsync(payload, "", deadline.Token);
                Assert.False(writing.IsCompleted);
                await Task.Run(port.Close, deadline.Token).WaitAsync(TimeSpan.FromSeconds(3));
                await Assert.ThrowsAsync<InvalidOperationException>(() => writing.WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.Equal(SerialConnectionState.Faulted, sender.ConnectionState);
                var timer = Stopwatch.StartNew();
                await sender.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                maxDisposeMs = Math.Max(maxDisposeMs, timer.Elapsed.TotalMilliseconds);
            }
            finally
            {
                if (writing is not null) await Record.ExceptionAsync(() => writing);
                await sender.DisposeAsync();
            }
        }
        output.WriteLine($"10 native faults during CTS-blocked TX; maximum dispose {maxDisposeMs:F2} ms.");
    }

    private static void VerifyEmulatorEndpoint(string name)
    {
        using var endpoint = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\com0com\port\" + name);
        Assert.NotNull(endpoint);
        Assert.Equal("com0com", endpoint.GetValue("Service"));
        using var parameters = endpoint.OpenSubKey("Device Parameters");
        Assert.Equal(name, parameters?.GetValue("PortName"));
    }

    private static bool UsesDtrHandshake(SerialHandshakeMode mode) =>
        mode is SerialHandshakeMode.Dtr or SerialHandshakeMode.DtrXOn or SerialHandshakeMode.DtrRts or SerialHandshakeMode.DtrRtsXOn;

    private static bool UsesRtsHandshake(SerialHandshakeMode mode) =>
        mode is SerialHandshakeMode.Rts or SerialHandshakeMode.RtsXOn or SerialHandshakeMode.DtrRts or SerialHandshakeMode.DtrRtsXOn;

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition()) await Task.Delay(10, cancellationToken);
    }

    private static async Task AssertNoRxAsync(SerialService service, CancellationToken cancellationToken)
    {
        using var quietWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        quietWindow.CancelAfter(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.ReceivedBytes.ReadAsync(quietWindow.Token));
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static BoundaryPreservingSerialPortStream GetOpenPort(SerialService service)
    {
        var session = typeof(SerialService).GetField("_currentReceiveSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        return Assert.IsType<BoundaryPreservingSerialPortStream>(session.GetType().GetProperty("Port")!.GetValue(session));
    }

    private static async Task<byte[]> ReadBytesAsync(SerialService service, int length, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        while (bytes.Count < length)
            bytes.AddRange((await service.ReceivedBytes.ReadAsync(cancellationToken)).Bytes);
        Assert.Equal(length, bytes.Count);
        return bytes.ToArray();
    }
}

public sealed class Com0comPair0FactAttribute : FactAttribute
{
    public Com0comPair0FactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SERIAL_MONITOR_TEST_COM0COM_PAIR0") != "1")
            Skip = "Set SERIAL_MONITOR_TEST_COM0COM_PAIR0=1 to test the explicit CNCA0/CNCB0 com0com emulator pair.";
    }
}
