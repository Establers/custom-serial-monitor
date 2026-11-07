using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class HexGroupTimingPolicyTests
{
    [Theory]
    [InlineData(115200, 8, SerialParityMode.None, SerialStopBitsMode.One, 4)]
    [InlineData(9600, 8, SerialParityMode.None, SerialStopBitsMode.One, 6)]
    [InlineData(4800, 8, SerialParityMode.None, SerialStopBitsMode.OnePointFive, 10)]
    [InlineData(4800, 8, SerialParityMode.None, SerialStopBitsMode.Two, 11)]
    [InlineData(9600, 8, SerialParityMode.Mark, SerialStopBitsMode.Two, 7)]
    [InlineData(1200, 5, SerialParityMode.None, SerialStopBitsMode.One, 23)]
    public void AutomaticTimeout_UsesCompleteSerialFrameFormat(
        int baudRate, int dataBits, SerialParityMode parity, SerialStopBitsMode stopBits, int expectedMs)
    {
        var settings = new SerialSettings
        {
            BaudRate = baudRate,
            DataBits = dataBits,
            Parity = parity,
            StopBits = stopBits
        };

        Assert.Equal(expectedMs, HexGroupTimingPolicy.GetDefaultTimeoutMilliseconds(settings));
    }

    [Fact]
    public void NewSettings_DefaultMatchesAutomaticTimeout()
    {
        Assert.Equal(new UiSettings().HexGroupTimeoutMs,
            HexGroupTimingPolicy.GetDefaultTimeoutMilliseconds(new SerialSettings()));
    }
}
