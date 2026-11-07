using System.Buffers.Binary;

namespace SerialMonitor.Core.Protocols.VirtualModbus;

public sealed record VirtualModbusDeviceInfo(int UnitId, string Name, int RegisterCount);
public sealed record VirtualModbusPointSnapshot(int Index, VirtualModbusRegisterDefinition Definition, string Value, ushort[] RawWords);
public sealed record VirtualModbusReply(bool Valid, bool Matched, bool Broadcast, byte[]? Response, bool IsException = false);

// Owns the mutable register memory. Configuration import, UI edits and protocol
// writes are atomic with respect to a complete request; no lock spans an await.
public sealed class VirtualModbusDeviceStore
{
    private sealed class Point(VirtualModbusRegisterDefinition definition, ushort[] words)
    {
        public VirtualModbusRegisterDefinition Definition { get; } = definition;
        public ushort[] Words { get; } = words;
    }
    private sealed class Device(string name, List<Point> points, Dictionary<(string, int), (Point Point, int Offset)> cells)
    {
        public string Name { get; } = name;
        public List<Point> Points { get; } = points;
        public Dictionary<(string, int), (Point Point, int Offset)> Cells { get; } = cells;
    }
    private readonly object _gate = new();
    private readonly Dictionary<int, Device> _devices = new();
    private long _version;
    public long Version { get { lock (_gate) return _version; } }

    public void Add(VirtualModbusDefinition definition)
    {
        if (definition.SchemaVersion != 1) throw new FormatException("schemaVersion must be 1.");
        if (definition.Devices is null || definition.Devices.Count is < 1 or > VirtualModbusJson.MaximumDevices)
            throw new FormatException("devices must contain 1–32 devices.");
        var additions = new Dictionary<int, Device>();
        var total = 0;
        foreach (var device in definition.Devices)
        {
            if (device is null || string.IsNullOrWhiteSpace(device.Name) || device.Name.Length > 128 || device.UnitId is < 1 or > 247)
                throw new FormatException("Each device needs a name (1–128 characters) and unitId (1–247).");
            if (device.Registers is null || device.Registers.Count == 0 || (total += device.Registers.Count) > VirtualModbusJson.MaximumRegisters)
                throw new FormatException("Provide registers; the total limit is 8192 points.");
            var points = new List<Point>();
            var cells = new Dictionary<(string, int), (Point, int)>();
            foreach (var register in device.Registers)
            {
                if (register is null) throw new FormatException("A register cannot be null.");
                Validate(register);
                var words = VirtualModbusValueCodec.InitialWords(register);
                var point = new Point(register with { Value = null, RawWords = null }, words);
                for (var i = 0; i < words.Length; i++)
                    if (!cells.TryAdd((register.Area, register.Address + i), (point, i)))
                        throw new FormatException($"ID {device.UnitId}: overlapping {register.Area} address {register.Address + i}.");
                points.Add(point);
            }
            if (!additions.TryAdd(device.UnitId, new Device(device.Name, points, cells)))
                throw new FormatException($"Duplicate unitId {device.UnitId} in JSON.");
        }
        lock (_gate)
        {
            if (_devices.Count + additions.Count > VirtualModbusJson.MaximumDevices ||
                _devices.Values.Sum(d => d.Points.Count) + total > VirtualModbusJson.MaximumRegisters)
                throw new FormatException("Loaded devices exceed the limit of 32 devices / 8192 points.");
            foreach (var id in additions.Keys)
                if (_devices.ContainsKey(id)) throw new FormatException($"Sensor ID {id} is already loaded. Change the ID or remove that device first.");
            // Reserve the largest possible ID/raw-word representation, so any
            // accepted workspace remains saveable after UI or protocol writes.
            var budget = ExportDefinition(_devices.Concat(additions), reserveMaximumValues: true);
            if (System.Text.Encoding.UTF8.GetByteCount(VirtualModbusJson.Serialize(budget)) > VirtualModbusJson.MaximumJsonBytes)
                throw new FormatException("Combined device configuration exceeds the 16 MiB save/import limit.");
            foreach (var pair in additions) _devices.Add(pair.Key, pair.Value);
            _version++;
        }
    }

    private static void Validate(VirtualModbusRegisterDefinition d)
    {
        if (string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 128 || d.Unit is null || d.Unit.Length > 32 || d.Description is null || d.Description.Length > 1024)
            throw new FormatException("Register name/unit/description is missing or exceeds its length limit.");
        if (d.Area is not ("holding" or "input" or "coil" or "discrete")) throw new FormatException($"{d.Name}: invalid area '{d.Area}'.");
        var count = VirtualModbusValueCodec.WordCount(d.DataType);
        if (d.Address < 0 || d.Address > 65536 - count) throw new FormatException($"{d.Name}: address exceeds the zero-based 0–65535 range.");
        if ((d.Area is "coil" or "discrete") != (d.DataType == "bool"))
            throw new FormatException($"{d.Name}: coil/discrete require bool; holding/input require a numeric dataType.");
        if (!double.IsFinite(d.Scale) || Math.Abs(d.Scale) is < 1e-12 or > 1e12 || !double.IsFinite(d.Offset) || Math.Abs(d.Offset) > 1e12)
            throw new FormatException($"{d.Name}: scale must be nonzero (absolute range 1e-12–1e12); offset must be finite (±1e12).");
        if (d.DataType == "bool" && (d.Scale != 1 || d.Offset != 0)) throw new FormatException("bool cannot have scale/offset.");
        if (d.ByteOrder is not ("bigEndian" or "littleEndian") || d.WordOrder is not ("highFirst" or "lowFirst"))
            throw new FormatException($"{d.Name}: invalid byteOrder/wordOrder.");
    }

    public IReadOnlyList<VirtualModbusDeviceInfo> Devices()
    {
        lock (_gate) return _devices.OrderBy(p => p.Key).Select(p => new VirtualModbusDeviceInfo(p.Key, p.Value.Name, p.Value.Points.Count)).ToArray();
    }

    public IReadOnlyList<VirtualModbusPointSnapshot> Page(int unitId, int offset, int count = 128)
    {
        if (offset < 0 || count is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(count));
        lock (_gate)
        {
            if (!_devices.TryGetValue(unitId, out var device)) return [];
            return device.Points.Skip(offset).Take(count).Select((p, i) => new VirtualModbusPointSnapshot(offset + i,
                p.Definition, VirtualModbusValueCodec.Decode(p.Definition, p.Words), p.Words.ToArray())).ToArray();
        }
    }

    public void SetValue(int unitId, int index, string value)
    {
        lock (_gate)
        {
            var point = _devices[unitId].Points[index];
            VirtualModbusValueCodec.Encode(point.Definition, value).CopyTo(point.Words, 0);
            _version++;
        }
    }

    public void ChangeUnitId(int oldId, int newId)
    {
        if (newId is < 1 or > 247) throw new FormatException("Sensor ID must be 1–247.");
        lock (_gate)
        {
            if (oldId == newId) return;
            if (_devices.ContainsKey(newId)) throw new FormatException($"Sensor ID {newId} is already loaded.");
            var device = _devices[oldId];
            _devices.Remove(oldId);
            _devices.Add(newId, device);
            _version++;
        }
    }

    public void Remove(int unitId) { lock (_gate) { if (_devices.Remove(unitId)) _version++; } }

    public VirtualModbusDefinition Export()
    {
        lock (_gate) return ExportDefinition(_devices, reserveMaximumValues: false);
    }

    private static VirtualModbusDefinition ExportDefinition(IEnumerable<KeyValuePair<int, Device>> devices, bool reserveMaximumValues) => new()
        {
            SchemaVersion = 1,
            Devices = devices.OrderBy(p => p.Key).Select(pair => new VirtualModbusDeviceDefinition
            {
                Name = pair.Value.Name, UnitId = reserveMaximumValues ? 247 : pair.Key,
                Registers = pair.Value.Points.Select(p => p.Definition with
                {
                    RawWords = reserveMaximumValues ? Enumerable.Repeat((ushort)(p.Definition.DataType == "bool" ? 1 : 65535), p.Words.Length).ToArray() : p.Words.ToArray(),
                    Value = null
                }).ToList()
            }).ToList()
        };

    public VirtualModbusReply ProcessRequest(ReadOnlySpan<byte> request)
    {
        if (request.Length is < 4 or > 256 || request[0] > 247 || request[1] is 0 or >= 128 || ModbusRtuCrc.Compute(request) != 0)
            return new(false, false, false, null);
        var id = request[0];
        var function = request[1];
        lock (_gate)
        {
            if (id == 0)
            {
                if (function is 5 or 6 or 15 or 16)
                    foreach (var device in _devices.Values) Execute(device, request);
                return new(true, false, true, null);
            }
            if (!_devices.TryGetValue(id, out var target)) return new(true, false, false, null);
            var (pdu, exception) = Execute(target, request);
            var response = new byte[pdu.Length + 3];
            response[0] = id;
            pdu.CopyTo(response, 1);
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(response.Length - 2), ModbusRtuCrc.Compute(response.AsSpan(0, response.Length - 2)));
            return new(true, true, false, response, exception);
        }
    }

    private (byte[] Pdu, bool Exception) Execute(Device device, ReadOnlySpan<byte> request)
    {
        var fc = request[1];
        (byte[], bool) Error(byte code) => ([(byte)(fc | 0x80), code], true);
        if (fc is not (1 or 2 or 3 or 4 or 5 or 6 or 15 or 16)) return Error(1);
        if (request.Length < 8) return Error(3);
        var address = BinaryPrimitives.ReadUInt16BigEndian(request[2..]);
        var argument = BinaryPrimitives.ReadUInt16BigEndian(request[4..]);
        var area = fc switch { 1 or 5 or 15 => "coil", 2 => "discrete", 4 => "input", _ => "holding" };
        var write = fc is 5 or 6 or 15 or 16;
        var quantity = fc is 5 or 6 ? 1 : argument;
        var maximum = fc switch { 1 or 2 => 2000, 3 or 4 => 125, 15 => 1968, 16 => 123, _ => 1 };
        if (quantity < 1 || quantity > maximum) return Error(3);
        if (address + quantity > 65536) return Error(2);
        if (fc is 15 or 16)
        {
            var expected = fc == 15 ? (quantity + 7) / 8 : quantity * 2;
            if (request.Length != 9 + expected || request[6] != expected) return Error(3);
        }
        else if (request.Length != 8) return Error(3);
        if (fc == 5 && argument is not (0 or 0xFF00)) return Error(3);
        // Validate the whole span before any mutation, including broadcast writes.
        for (var i = 0; i < quantity; i++)
            if (!device.Cells.TryGetValue((area, address + i), out var cell) || write && !cell.Point.Definition.Writable)
                return Error(2);
        if (write)
        {
            for (var i = 0; i < quantity; i++)
            {
                var value = fc switch
                {
                    5 => (ushort)(argument == 0 ? 0 : 1), 6 => argument,
                    15 => (ushort)((request[7 + i / 8] >> (i % 8)) & 1),
                    _ => BinaryPrimitives.ReadUInt16BigEndian(request[(7 + 2 * i)..])
                };
                var cell = device.Cells[(area, address + i)];
                cell.Point.Words[cell.Offset] = value;
            }
            _version++;
            return (request.Slice(1, 5).ToArray(), false);
        }
        var bitArea = fc is 1 or 2;
        var byteCount = bitArea ? (quantity + 7) / 8 : quantity * 2;
        var result = new byte[byteCount + 2];
        result[0] = fc; result[1] = (byte)byteCount;
        for (var i = 0; i < quantity; i++)
        {
            var cell = device.Cells[(area, address + i)];
            var value = cell.Point.Words[cell.Offset];
            if (bitArea) result[2 + i / 8] |= (byte)((value & 1) << (i % 8));
            else BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2 + 2 * i), value);
        }
        return (result, false);
    }
}
