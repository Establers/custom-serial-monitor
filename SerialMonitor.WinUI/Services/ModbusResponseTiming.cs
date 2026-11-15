using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

// Conservative host-side scheduling. USB completion timestamps cannot verify
// the physical t1.5 inter-character limit or transceiver direction timing.
internal sealed class ModbusResponseTiming(int baudRate, SerialPortConfiguration? configuration)
{
    public double BitsPerCharacter { get; } = Math.Max(11, configuration is null ? 11 :
        1 + configuration.DataBits + (configuration.Parity == SerialParityMode.None ? 0 : 1) +
        (configuration.StopBits switch { SerialStopBitsMode.Two => 2, SerialStopBitsMode.OnePointFive => 1.5, _ => 1 }));
    public TimeSpan FrameGap => TimeSpan.FromMilliseconds(baudRate > 19200 ? 1.75 : 3500 * BitsPerCharacter / Math.Max(1, baudRate));
    public TimeSpan WireDuration(int bytes) => TimeSpan.FromSeconds(bytes * BitsPerCharacter / Math.Max(1, baudRate));
}
