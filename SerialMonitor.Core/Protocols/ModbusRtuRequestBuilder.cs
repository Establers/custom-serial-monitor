using System.Buffers.Binary;

namespace SerialMonitor.Core.Protocols;

/// <summary>Builds standard RTU requests; owns no connection or transmit state.</summary>
public static partial class ModbusRtuRequestBuilder
{
    public static byte[] Read(int unitId, byte function, int address, int quantity)
    {
        if (function is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(function));
        ValidateUnit(unitId, allowBroadcast: false);
        ValidateRange(address, quantity, function <= 2 ? 2000 : 125);
        var packet = Header(unitId, function, address, 8);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)quantity);
        return Finish(packet);
    }

    /// <param name="value">For FC 05, 0 means OFF and 1 means ON.</param>
    public static byte[] WriteSingle(int unitId, byte function, int address, ushort value)
    {
        if (function is not (5 or 6)) throw new ArgumentOutOfRangeException(nameof(function));
        ValidateUnit(unitId, allowBroadcast: true);
        ValidateRange(address, 1, 1);
        if (function == 5 && value > 1) throw new ArgumentOutOfRangeException(nameof(value));
        var packet = Header(unitId, function, address, 8);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), function == 5 ? (ushort)(value == 1 ? 0xFF00 : 0) : value);
        return Finish(packet);
    }

    /// <param name="values">FC 0F uses one 0/1 value per coil, in address order.</param>
    public static byte[] WriteMultiple(int unitId, byte function, int address, IReadOnlyList<ushort> values)
    {
        if (function is not (15 or 16)) throw new ArgumentOutOfRangeException(nameof(function));
        ArgumentNullException.ThrowIfNull(values);
        ValidateUnit(unitId, allowBroadcast: true);
        ValidateRange(address, values.Count, function == 15 ? 1968 : 123);
        var byteCount = function == 15 ? (values.Count + 7) / 8 : values.Count * 2;
        var packet = Header(unitId, function, address, 9 + byteCount);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)values.Count);
        packet[6] = (byte)byteCount;
        for (var i = 0; i < values.Count; i++)
        {
            if (function == 15)
            {
                if (values[i] > 1) throw new ArgumentOutOfRangeException(nameof(values), "Coils must be 0 or 1.");
                packet[7 + i / 8] |= (byte)(values[i] << (i % 8));
            }
            else BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(7 + i * 2), values[i]);
        }
        return Finish(packet);
    }

    private static void ValidateUnit(int unitId, bool allowBroadcast)
    {
        if (unitId < (allowBroadcast ? 0 : 1) || unitId > 247)
            throw new ArgumentOutOfRangeException(nameof(unitId));
    }

    private static void ValidateRange(int address, int quantity, int maximumQuantity)
    {
        if (address is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(address));
        if (quantity < 1 || quantity > maximumQuantity) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (address + quantity > 65536) throw new ArgumentOutOfRangeException(nameof(quantity), "Address range exceeds 65535.");
    }

    private static byte[] Header(int unitId, byte function, int address, int length)
    {
        var packet = new byte[length];
        packet[0] = (byte)unitId;
        packet[1] = function;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)address);
        return packet;
    }

    private static byte[] Finish(byte[] packet)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(packet.Length - 2), ModbusRtuCrc.Compute(packet.AsSpan(0, packet.Length - 2)));
        return packet;
    }
}
