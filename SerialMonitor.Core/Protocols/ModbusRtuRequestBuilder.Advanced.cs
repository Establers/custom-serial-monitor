using System.Buffers.Binary;

namespace SerialMonitor.Core.Protocols;

public sealed record ModbusFileReadRecord(int FileNumber, int RecordNumber, int Quantity);
public sealed record ModbusFileWriteRecord(int FileNumber, int RecordNumber, IReadOnlyList<ushort> Values);

public static partial class ModbusRtuRequestBuilder
{
    public static byte[] WithoutData(int unitId, byte function)
    {
        if (function is not (7 or 11 or 12 or 17)) throw new ArgumentOutOfRangeException(nameof(function));
        ValidateUnit(unitId, allowBroadcast: false);
        return WithData(unitId, function, []);
    }

    public static byte[] Diagnostics(int unitId, int subFunction, IReadOnlyList<ushort> data)
    {
        ValidateUnit(unitId, allowBroadcast: false);
        ArgumentNullException.ThrowIfNull(data);
        if (subFunction is not (>= 0 and <= 4 or >= 10 and <= 18 or 20))
            throw new ArgumentOutOfRangeException(nameof(subFunction));
        if (data.Count is < 1 or > 125 || subFunction != 0 && data.Count != 1)
            throw new ArgumentOutOfRangeException(nameof(data), "Only Return Query Data supports multiple diagnostic words (1–125).");
        if (subFunction == 1 && data[0] is not (0 or 0xFF00))
            throw new ArgumentException("Restart data must be 0x0000 or 0xFF00.", nameof(data));
        if (subFunction == 3 && (data[0] & 255) != 0)
            throw new ArgumentException("ASCII delimiter data must be CHAR followed by 00 (e.g. 0x0A00).", nameof(data));
        if (subFunction is not (0 or 1 or 3) && data[0] != 0)
            throw new ArgumentException("This diagnostic subfunction requires data 0x0000.", nameof(data));
        var payload = new byte[2 + data.Count * 2];
        Word(payload, 0, subFunction);
        for (var i = 0; i < data.Count; i++) Word(payload, 2 + i * 2, data[i]);
        return WithData(unitId, 8, payload);
    }

    public static byte[] MaskWrite(int unitId, int address, ushort andMask, ushort orMask)
    {
        ValidateUnit(unitId, allowBroadcast: true);
        ValidateRange(address, 1, 1);
        var packet = Header(unitId, 22, address, 10);
        Word(packet, 4, andMask); Word(packet, 6, orMask);
        return Finish(packet);
    }

    public static byte[] ReadWriteMultiple(int unitId, int readAddress, int readQuantity, int writeAddress, IReadOnlyList<ushort> values)
    {
        ValidateUnit(unitId, allowBroadcast: false);
        ArgumentNullException.ThrowIfNull(values);
        ValidateRange(readAddress, readQuantity, 125);
        ValidateRange(writeAddress, values.Count, 121);
        var packet = Header(unitId, 23, readAddress, 13 + values.Count * 2);
        Word(packet, 4, readQuantity); Word(packet, 6, writeAddress); Word(packet, 8, values.Count);
        packet[10] = (byte)(values.Count * 2);
        for (var i = 0; i < values.Count; i++) Word(packet, 11 + i * 2, values[i]);
        return Finish(packet);
    }

    public static byte[] ReadFifo(int unitId, int address)
    {
        ValidateUnit(unitId, allowBroadcast: false);
        ValidateRange(address, 1, 1);
        return Finish(Header(unitId, 24, address, 6));
    }

    public static byte[] ReadDeviceIdentification(int unitId, int accessCode, int objectId)
    {
        ValidateUnit(unitId, allowBroadcast: false);
        if (accessCode is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(accessCode));
        if (objectId is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(objectId));
        return WithData(unitId, 43, [(byte)14, (byte)accessCode, (byte)objectId]);
    }

    public static byte[] ReadFileRecords(int unitId, IReadOnlyList<ModbusFileReadRecord> records)
    {
        ValidateUnit(unitId, allowBroadcast: false);
        ArgumentNullException.ThrowIfNull(records);
        // Both the request and the expected response must fit the 253-byte PDU.
        if (records.Count is < 1 or > 35) throw new ArgumentOutOfRangeException(nameof(records));
        var responseBytes = 2;
        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record);
            ValidateFileRecord(record.FileNumber, record.RecordNumber, record.Quantity, 124);
            responseBytes += 2 + record.Quantity * 2;
        }
        if (responseBytes > 253) throw new ArgumentException("Combined file read response exceeds the 253-byte PDU limit.", nameof(records));
        var data = new byte[1 + records.Count * 7];
        data[0] = (byte)(data.Length - 1);
        for (var i = 0; i < records.Count; i++)
            FileHeader(data, 1 + i * 7, records[i].FileNumber, records[i].RecordNumber, records[i].Quantity);
        return WithData(unitId, 20, data);
    }

    public static byte[] WriteFileRecords(int unitId, IReadOnlyList<ModbusFileWriteRecord> records)
    {
        ValidateUnit(unitId, allowBroadcast: true);
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count is < 1 or > 27) throw new ArgumentOutOfRangeException(nameof(records));
        var byteCount = 0;
        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(record.Values);
            ValidateFileRecord(record.FileNumber, record.RecordNumber, record.Values.Count, 122);
            byteCount += 7 + record.Values.Count * 2;
        }
        if (byteCount > 251) throw new ArgumentException("Combined file write exceeds the 253-byte PDU limit.", nameof(records));
        var data = new byte[1 + byteCount];
        data[0] = (byte)byteCount;
        var offset = 1;
        foreach (var record in records)
        {
            FileHeader(data, offset, record.FileNumber, record.RecordNumber, record.Values.Count);
            offset += 7;
            foreach (var value in record.Values) { Word(data, offset, value); offset += 2; }
        }
        return WithData(unitId, 21, data);
    }

    /// <summary>Unicast custom request: validates framing only, not vendor-specific data semantics.</summary>
    public static byte[] Custom(int unitId, int function, ReadOnlySpan<byte> data)
    {
        ValidateUnit(unitId, allowBroadcast: false);
        if (function is < 1 or > 127) throw new ArgumentOutOfRangeException(nameof(function));
        if (data.Length > 252) throw new ArgumentOutOfRangeException(nameof(data));
        return WithData(unitId, (byte)function, data);
    }

    private static void ValidateFileRecord(int file, int record, int quantity, int maximum)
    {
        if (file is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(file));
        if (record is < 0 or > 9999) throw new ArgumentOutOfRangeException(nameof(record));
        if (quantity < 1 || quantity > maximum || record + quantity > 10000)
            throw new ArgumentOutOfRangeException(nameof(quantity));
    }

    private static void FileHeader(byte[] data, int offset, int file, int record, int quantity)
    {
        data[offset] = 6;
        Word(data, offset + 1, file); Word(data, offset + 3, record); Word(data, offset + 5, quantity);
    }

    private static void Word(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), (ushort)value);

    private static byte[] WithData(int unitId, byte function, ReadOnlySpan<byte> data)
    {
        if (data.Length > 252) throw new ArgumentOutOfRangeException(nameof(data));
        var packet = new byte[4 + data.Length];
        packet[0] = (byte)unitId; packet[1] = function;
        data.CopyTo(packet.AsSpan(2));
        return Finish(packet);
    }
}
