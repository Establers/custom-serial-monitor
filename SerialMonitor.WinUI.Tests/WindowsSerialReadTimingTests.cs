using RJCP.IO.Ports;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class WindowsSerialReadTimingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(17)]
    [InlineData(100)]
    [InlineData(999)]
    [InlineData(5000)]
    public void HexMode_AppliesExactProfileTimeoutToRjcpSettings(int timeoutMs)
    {
        using var serialPort = new WinSerialPortStream();

        WindowsSerialReadTiming.Apply(
            serialPort,
            new SerialReceiveOptions
            {
                UseNativeIdleTimeout = true,
                IdleTimeoutMs = timeoutMs
            });

        Assert.Equal(timeoutMs, serialPort.Settings.ReadIntervalTimeout);
        Assert.Equal(0, serialPort.Settings.ReadTotalTimeoutConstant);
        Assert.Equal(0, serialPort.Settings.ReadTotalTimeoutMultiplier);
    }

    [Fact]
    public void TerminalMode_UsesImmediateDrainRegardlessOfSavedHexTimeout()
    {
        using var serialPort = new WinSerialPortStream();

        WindowsSerialReadTiming.Apply(
            serialPort,
            new SerialReceiveOptions
            {
                UseNativeIdleTimeout = false,
                IdleTimeoutMs = 437
            });

        Assert.Equal(Timeout.Infinite, serialPort.Settings.ReadIntervalTimeout);
        Assert.Equal(0, serialPort.Settings.ReadTotalTimeoutConstant);
        Assert.Equal(0, serialPort.Settings.ReadTotalTimeoutMultiplier);
    }

}
