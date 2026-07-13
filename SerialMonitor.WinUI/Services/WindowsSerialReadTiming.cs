using SerialMonitor.Core;
using RJCP.IO.Ports;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

internal static class WindowsSerialReadTiming
{
    internal static void Apply(WinSerialPortStream serialPort, SerialReceiveOptions options)
    {
        ArgumentNullException.ThrowIfNull(serialPort);
        ArgumentNullException.ThrowIfNull(options);

        var normalized = options.Normalize();
        var timing = SerialReadTimingPolicy.Create(
            normalized.UseNativeIdleTimeout,
            normalized.IdleTimeoutMs);

        // HEX mode uses the exact profile timeout as the Win32 inter-byte
        // timeout. This lets a pending ReadFile complete at the earliest layer
        // that still observes byte arrival gaps. Terminal mode retains RJCP's
        // immediate drain behavior so a large HEX timeout cannot stall a
        // continuous terminal stream.
        serialPort.Settings.ReadIntervalTimeout = timing.ReadIntervalTimeout;
        serialPort.Settings.ReadTotalTimeoutConstant = timing.ReadTotalTimeoutConstant;
        serialPort.Settings.ReadTotalTimeoutMultiplier = timing.ReadTotalTimeoutMultiplier;
    }
}
