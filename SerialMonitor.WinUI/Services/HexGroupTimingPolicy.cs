using SerialMonitor.Core;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public static class HexGroupTimingPolicy
{
    public static int GetDefaultTimeoutMilliseconds(SerialSettings settings)
    {
        var stopBits = settings.StopBits switch
        {
            SerialStopBitsMode.OnePointFive => 1.5,
            SerialStopBitsMode.Two => 2d,
            _ => 1d
        };

        return SerialTimingAdvisor.Calculate(
            settings.BaudRate,
            settings.DataBits,
            settings.Parity != SerialParityMode.None,
            stopBits).SuggestedStartingTimeoutMilliseconds;
    }
}
