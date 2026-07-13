using RJCP.IO.Ports;

namespace SerialMonitor.WinUI.Services;

internal static class WindowsSerialReadTiming
{
    internal const int ImmediateReadInterval = Timeout.Infinite;
    internal const int NoTotalReadTimeout = 0;

    internal static void Apply(WinSerialPortStream serialPort)
    {
        ArgumentNullException.ThrowIfNull(serialPort);

        // Keep Win32 ReadFile as a transport drain, not a packet-boundary
        // detector. RJCP maps Timeout.Infinite (-1) to DWORD MAXDWORD. With
        // both total timeouts set to zero, COMMTIMEOUTS returns the bytes that
        // are already in the driver buffer immediately. RJCP starts the read
        // only after RXCHAR and drains until zero, so this does not busy-poll.
        // Packet grouping remains exclusively in LogPipeline, where the user
        // configured HEX idle timeout is applied to observed chunk arrivals.
        serialPort.Settings.ReadIntervalTimeout = ImmediateReadInterval;
        serialPort.Settings.ReadTotalTimeoutConstant = NoTotalReadTimeout;
        serialPort.Settings.ReadTotalTimeoutMultiplier = NoTotalReadTimeout;
    }
}
