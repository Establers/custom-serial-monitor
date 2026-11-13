using SerialMonitor.Core.Protocols;

namespace SerialMonitor.Core.Modbus;

/// <summary>A single unicast read operation, using the zero-based wire address.</summary>
public sealed record ModbusReadRequest(int UnitId, byte Function, int Address, int Quantity)
{
    public void Validate()
    {
        if (UnitId is < 1 or > 247) throw new ArgumentOutOfRangeException(nameof(UnitId));
        if (Function is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(Function));
        if (Address is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(Address));
        if (Quantity < 1 || Quantity > (Function <= 2 ? 2000 : 125))
            throw new ArgumentOutOfRangeException(nameof(Quantity));
        if ((long)Address + Quantity > 65536)
            throw new ArgumentOutOfRangeException(nameof(Quantity), "Address range exceeds 65535.");
    }

    public byte[] BuildPacket()
    {
        Validate();
        return ModbusRtuRequestBuilder.Read(UnitId, Function, Address, Quantity);
    }
}
