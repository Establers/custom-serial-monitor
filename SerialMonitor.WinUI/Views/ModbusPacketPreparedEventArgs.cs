namespace SerialMonitor.WinUI.Views;

public sealed class ModbusPacketPreparedEventArgs(ReadOnlyMemory<byte> packet) : EventArgs
{
    public ReadOnlyMemory<byte> Packet { get; } = packet;
}
