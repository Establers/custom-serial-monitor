using System.Reflection;
using Microsoft.Win32;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

// Explicit opt-in prevents CI and ordinary test runs from opening any ports.
// This fixture only permits the registry-verified com0com emulator pair 0;
// environment variables cannot redirect it to a physical device.
public sealed class SerialServiceVirtualDriverTests
{
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

    private static void VerifyEmulatorEndpoint(string name)
    {
        using var endpoint = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\com0com\port\" + name);
        Assert.NotNull(endpoint);
        Assert.Equal("com0com", endpoint.GetValue("Service"));
        using var parameters = endpoint.OpenSubKey("Device Parameters");
        Assert.Equal(name, parameters?.GetValue("PortName"));
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
