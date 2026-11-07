using System.Buffers.Binary;
using System.Text;

namespace SerialMonitor.Core.Protocols;

public enum ProtocolDirection { Rx, Tx }
public enum ModbusFrameKind { Request, Response, RequestOrResponse, Exception, Undecoded, Incomplete }
public enum ModbusCrcStatus { Valid, Invalid, Unavailable }
public enum ModbusFrameStatus { Decoded, CrcError, Incomplete, Undecoded, Exception }

// Owns at most one RTU ADU. Text and register expansion are produced only for a
// selected result; the receive queue and retained history hold binary data.
public sealed record ModbusRtuFrame(
    long Id, long Session, DateTimeOffset Timestamp, ProtocolDirection Direction,
    ReadOnlyMemory<byte> Bytes, ModbusFrameKind Kind, ModbusCrcStatus CrcStatus) : IProtocolFrame
{
    public const int MaximumBytes = 256;
    public byte? UnitId => Bytes.Length > 0 ? Bytes.Span[0] : null;
    public byte? FunctionCode => Bytes.Length > 1 ? Bytes.Span[1] : null;
    public int Length => Bytes.Length;
    public long RetainedBytes => Length + 256L;
    public byte? Address => UnitId;
    public byte? Code => FunctionCode;
    public string CodeName => Status == ModbusFrameStatus.Exception && Length == 5
        ? $"{FunctionName(FunctionCode)} · {ExceptionName(Bytes.Span[2])}" : FunctionName(FunctionCode);
    ProtocolFrameStatus IProtocolFrame.Status => (ProtocolFrameStatus)(int)Status;
    ProtocolCrcStatus IProtocolFrame.CrcStatus => (ProtocolCrcStatus)(int)CrcStatus;
    public bool MatchesCode(byte code) => FunctionCode is { } actual &&
        (code < 0x80 ? (actual & 0x7F) == code : actual == code);
    // CRC validity is independent of request/response role. A complete-length
    // candidate with a bad CRC must not be presented as missing bytes.
    public ModbusFrameStatus Status => CrcStatus switch
    {
        ModbusCrcStatus.Invalid => ModbusFrameStatus.CrcError,
        ModbusCrcStatus.Unavailable => ModbusFrameStatus.Incomplete,
        _ => Kind switch
        {
            ModbusFrameKind.Exception => ModbusFrameStatus.Exception,
            ModbusFrameKind.Incomplete => ModbusFrameStatus.Incomplete,
            ModbusFrameKind.Undecoded => ModbusFrameStatus.Undecoded,
            _ => ModbusFrameStatus.Decoded
        }
    };

    public string Describe()
    {
        var b = Bytes.Span;
        var text = new StringBuilder();
        var heading = Status switch
        {
            ModbusFrameStatus.CrcError => "CRC error",
            ModbusFrameStatus.Incomplete => "Incomplete data / unestablished boundary",
            ModbusFrameStatus.Exception => "Exception response",
            ModbusFrameStatus.Undecoded => "Undecoded frame",
            _ => Kind.ToString()
        };
        text.AppendLine($"{Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} · {Direction} · {heading}");
        text.AppendLine($"Session: {Session}; unit: {UnitId}; function: {FunctionCode:X2}; bytes: {Length}");
        if (b.Length >= 4)
            text.AppendLine($"CRC: {CrcStatus}; received: {BinaryPrimitives.ReadUInt16LittleEndian(b[^2..]):X4}; calculated: {ModbusRtuCrc.Compute(b[..^2]):X4}");
        else text.AppendLine("CRC: unavailable (incomplete frame)");
        if (Status == ModbusFrameStatus.Exception && b.Length == 5)
            text.AppendLine($"Exception {b[2]:X2}: {ExceptionName(b[2])}");
        else if (CrcStatus == ModbusCrcStatus.Valid && Kind == ModbusFrameKind.Request && b.Length >= 8 && b[1] is 1 or 2 or 3 or 4 or 15 or 16)
        {
            var address = BinaryPrimitives.ReadUInt16BigEndian(b[2..]);
            text.AppendLine($"Start address (base 0): {address}; quantity: {BinaryPrimitives.ReadUInt16BigEndian(b[4..])}");
            if (b[1] == 3) text.AppendLine($"Holding-register reference (base 1): {40001 + address}");
            if (b[1] == 4) text.AppendLine($"Input-register reference (base 1): {30001 + address}");
        }
        else if (CrcStatus == ModbusCrcStatus.Valid && Kind == ModbusFrameKind.Response && b.Length >= 5 && b[1] is 3 or 4 or 23 && b[2] % 2 == 0)
        {
            text.AppendLine("16-bit register values (offsets within this response; start address is not carried in the response):");
            for (var offset = 0; offset < b[2] && offset + 4 < b.Length - 1; offset += 2)
            {
                var value = BinaryPrimitives.ReadUInt16BigEndian(b[(3 + offset)..]);
                text.AppendLine($"+{offset / 2}: {value} / signed {unchecked((short)value)} / 0x{value:X4}");
            }
        }
        else if (CrcStatus == ModbusCrcStatus.Valid && b.Length == 8 && b[1] is 5 or 6)
            text.AppendLine($"Address (base 0): {BinaryPrimitives.ReadUInt16BigEndian(b[2..])}; value: 0x{BinaryPrimitives.ReadUInt16BigEndian(b[4..]):X4}");
        if (CrcStatus == ModbusCrcStatus.Valid && Kind == ModbusFrameKind.RequestOrResponse)
            text.AppendLine("Request and response have the same valid layout; role is not inferred from RX alone.");
        if (Status == ModbusFrameStatus.CrcError)
            text.AppendLine("The candidate length is complete, but its CRC does not match. No register interpretation is applied.");
        else if (Status == ModbusFrameStatus.Incomplete)
            text.AppendLine("Frame boundary or function layout could not be established. No register interpretation is applied.");
        else if (Status == ModbusFrameStatus.Undecoded)
            text.AppendLine("CRC matches, but the address or function layout is not decoded. No register interpretation is applied.");
        text.AppendLine();
        text.Append(string.Join(" ", Bytes.ToArray().Select(value => value.ToString("X2"))));
        return text.ToString();
    }

    public static string FunctionName(byte? code) => (code is null ? 0 : code.Value & 0x7F) switch
    {
        1 => "Read coils", 2 => "Read discrete inputs", 3 => "Read holding registers", 4 => "Read input registers",
        5 => "Write single coil", 6 => "Write single register", 7 => "Read exception status", 8 => "Diagnostics",
        11 => "Get comm event counter", 12 => "Get comm event log", 15 => "Write multiple coils",
        16 => "Write multiple registers", 17 => "Report server ID", 22 => "Mask write register",
        23 => "Read/write multiple registers", 24 => "Read FIFO queue", 43 => "Encapsulated interface",
        _ => "Undecoded function"
    };

    public static string ExceptionName(byte code) => code switch
    {
        1 => "Illegal function", 2 => "Illegal data address", 3 => "Illegal data value", 4 => "Server device failure",
        5 => "Acknowledge", 6 => "Server device busy", 8 => "Memory parity error",
        10 => "Gateway path unavailable", 11 => "Gateway target failed to respond", _ => "Unknown exception code"
    };
}

public static class ModbusRtuCrc
{
    // Generated locally from the Modbus polynomial, not a copied lookup table.
    private static readonly ushort[] Table = CreateTable();
    public static ushort Compute(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xFFFF;
        foreach (var value in bytes) crc = Append(crc, value);
        return crc;
    }
    internal static ushort Append(ushort crc, byte value) => (ushort)((crc >> 8) ^ Table[(crc ^ value) & 255]);
    private static ushort[] CreateTable()
    {
        var table = new ushort[256];
        for (var i = 0; i < table.Length; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
            table[i] = (ushort)crc;
        }
        return table;
    }
}
