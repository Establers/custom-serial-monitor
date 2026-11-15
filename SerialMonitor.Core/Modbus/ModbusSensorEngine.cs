using System.Buffers.Binary;
using SerialMonitor.Core.Protocols;

namespace SerialMonitor.Core.Modbus;

// Single consumer owns this bounded register map. Writes live only until restart.
public sealed class ModbusSensorEngine
{
    private readonly int _unit;
    private readonly Dictionary<(int Area, int Address), (ushort Value, bool Writable)> _cells = [];
    public ModbusSensorEngine(ModbusSensorDefinition definition)
    {
        definition.Validate();
        _unit = definition.UnitId;
        foreach (var point in definition.Points)
        {
            var values = ModbusItemCodec.Encode(point.InitialValue, point.Item(), point.Area <= 2);
            for (var i = 0; i < values.Length; i++) _cells.Add((point.Area, point.Address + i), (values[i], point.Writable));
        }
    }
    public byte[]? Respond(ReadOnlySpan<byte> request)
    {
        if (request.Length is < 4 or > 256 || ModbusRtuCrc.Compute(request[..^2]) != BinaryPrimitives.ReadUInt16LittleEndian(request[^2..])) return null;
        var broadcast = request[0] == 0;
        if (!broadcast && request[0] != _unit) return null;
        var fc = request[1];
        byte[] Exception(byte code) => Finish([(byte)_unit, (byte)(fc | 128), code]);
        if (fc == 22) return MaskWrite(request, broadcast);
        if (fc == 23) return broadcast ? null : ReadWrite(request);
        if (fc is not (1 or 2 or 3 or 4 or 5 or 6 or 15 or 16)) return broadcast ? null : Exception(1);
        if (request.Length < 8) return broadcast ? null : Exception(3);
        int address = BinaryPrimitives.ReadUInt16BigEndian(request[2..]);
        int count = BinaryPrimitives.ReadUInt16BigEndian(request[4..]);
        var read = fc <= 4;
        if (broadcast && read) return null;
        var area = read ? fc : fc is 5 or 15 ? 1 : 3;
        ushort[] values;
        if (read)
        {
            if (request.Length != 8 || count < 1 || count > (fc <= 2 ? 2000 : 125)) return Exception(3);
            values = new ushort[count];
        }
        else if (fc is 5 or 6)
        {
            if (request.Length != 8 || fc == 5 && count is not (0 or 65280)) return broadcast ? null : Exception(3);
            values = [(ushort)(fc == 5 ? count == 0 ? 0 : 1 : count)]; count = 1;
        }
        else
        {
            var bytes = fc == 15 ? (count + 7) / 8 : count * 2;
            if (count < 1 || count > (fc == 15 ? 1968 : 123) || request.Length != 9 + bytes || request[6] != bytes)
                return broadcast ? null : Exception(3);
            values = new ushort[count];
            for (var i = 0; i < count; i++) values[i] = fc == 15 ? (ushort)((request[7 + i / 8] >> (i % 8)) & 1)
                : BinaryPrimitives.ReadUInt16BigEndian(request[(7 + i * 2)..]);
        }
        // Validate the complete operation before changing any cell.
        for (var i = 0; i < count; i++)
        {
            if (!_cells.TryGetValue((area, address + i), out var cell) || !read && !cell.Writable)
                return broadcast ? null : Exception(2);
            if (read) values[i] = cell.Value;
        }
        if (!read)
        {
            for (var i = 0; i < count; i++) _cells[(area, address + i)] = (values[i], true);
            return broadcast ? null : Finish(request[..6].ToArray());
        }
        var payload = new byte[3 + (fc <= 2 ? (count + 7) / 8 : count * 2)];
        payload[0] = (byte)_unit; payload[1] = fc; payload[2] = (byte)(payload.Length - 3);
        for (var i = 0; i < count; i++)
            if (fc <= 2) payload[3 + i / 8] |= (byte)(values[i] << (i % 8));
            else BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(3 + i * 2), values[i]);
        return Finish(payload);
    }
    private byte[]? MaskWrite(ReadOnlySpan<byte> request, bool broadcast)
    {
        if (request.Length != 10) return broadcast ? null : Error(22, 3);
        var address = BinaryPrimitives.ReadUInt16BigEndian(request[2..]);
        if (!_cells.TryGetValue((3, address), out var cell) || !cell.Writable)
            return broadcast ? null : Error(22, 2);
        var andMask = BinaryPrimitives.ReadUInt16BigEndian(request[4..]);
        var orMask = BinaryPrimitives.ReadUInt16BigEndian(request[6..]);
        _cells[(3, address)] = ((ushort)((cell.Value & andMask) | (orMask & ~andMask)), true);
        return broadcast ? null : request.ToArray();
    }

    private byte[] ReadWrite(ReadOnlySpan<byte> request)
    {
        if (request.Length < 13) return Error(23, 3);
        int readAddress = BinaryPrimitives.ReadUInt16BigEndian(request[2..]);
        int readCount = BinaryPrimitives.ReadUInt16BigEndian(request[4..]);
        int writeAddress = BinaryPrimitives.ReadUInt16BigEndian(request[6..]);
        int writeCount = BinaryPrimitives.ReadUInt16BigEndian(request[8..]);
        if (readCount is < 1 or > 125 || writeCount is < 1 or > 121 ||
            request[10] != writeCount * 2 || request.Length != 13 + writeCount * 2) return Error(23, 3);
        // Reject either invalid range before writing. Overlapping reads observe the new values.
        for (var i = 0; i < readCount; i++)
            if (!_cells.ContainsKey((3, readAddress + i))) return Error(23, 2);
        for (var i = 0; i < writeCount; i++)
            if (!_cells.TryGetValue((3, writeAddress + i), out var cell) || !cell.Writable) return Error(23, 2);
        for (var i = 0; i < writeCount; i++)
            _cells[(3, writeAddress + i)] = (BinaryPrimitives.ReadUInt16BigEndian(request[(11 + i * 2)..]), true);
        var payload = new byte[3 + readCount * 2];
        payload[0] = (byte)_unit; payload[1] = 23; payload[2] = (byte)(readCount * 2);
        for (var i = 0; i < readCount; i++)
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(3 + i * 2), _cells[(3, readAddress + i)].Value);
        return Finish(payload);
    }

    private byte[] Error(byte function, byte code) => Finish([(byte)_unit, (byte)(function | 128), code]);

    private static byte[] Finish(byte[] payload)
    {
        var packet = new byte[payload.Length + 2]; payload.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(payload.Length), ModbusRtuCrc.Compute(payload));
        return packet;
    }
}

public sealed class ModbusRequestFramer
{
    private readonly List<byte> _buffer = new(256);
    public void Clear() => _buffer.Clear();
    public IEnumerable<byte[]> Feed(byte[] chunk, bool endsAtIdleBoundary = false)
    {
        foreach (var value in chunk)
        {
            _buffer.Add(value);
            foreach (var frame in Drain(endOfFrame: false)) yield return frame;
        }
        if (!endsAtIdleBoundary) yield break;
        // At a known idle boundary a truncated variable-length prefix cannot receive
        // more bytes. Search its tail for complete CRC-valid requests before
        // discarding it; otherwise line noise can hide an entire good request.
        foreach (var frame in Drain(endOfFrame: true)) yield return frame;
        Clear();
    }
    private IEnumerable<byte[]> Drain(bool endOfFrame)
    {
        while (_buffer.Count >= 8)
        {
            if (_buffer[1] == 23 && _buffer.Count < 11)
            {
                if (!endOfFrame) yield break;
                _buffer.RemoveAt(0); continue;
            }
            var length = _buffer[1] switch { 15 or 16 => 9 + _buffer[6], 22 => 10, 23 => 13 + _buffer[10], _ => 8 };
            if (length > 256) { _buffer.RemoveAt(0); continue; }
            if (_buffer.Count < length)
            {
                if (!endOfFrame) yield break;
                _buffer.RemoveAt(0); continue;
            }
            var candidate = _buffer.Take(length).ToArray();
            if (ModbusRtuCrc.Compute(candidate.AsSpan(0, length - 2)) == BinaryPrimitives.ReadUInt16LittleEndian(candidate.AsSpan(length - 2)))
            { _buffer.RemoveRange(0, length); yield return candidate; }
            else _buffer.RemoveAt(0);
        }
    }
}
