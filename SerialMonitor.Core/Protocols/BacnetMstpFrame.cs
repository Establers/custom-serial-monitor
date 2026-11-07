using System.Buffers.Binary;
using System.Text;

namespace SerialMonitor.Core.Protocols;

public static class BacnetMstpCrc
{
    // BACnet Annex G: reflected polynomials, initial ones and final complement.
    public static byte Header(ReadOnlySpan<byte> bytes)
    {
        byte crc = 0xFF;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (byte)((crc >> 1) ^ ((crc & 1) != 0 ? 0x81 : 0));
        }
        return (byte)~crc;
    }
    public static ushort Data(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xFFFF;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (ushort)((crc >> 1) ^ ((crc & 1) != 0 ? 0x8408 : 0));
        }
        return (ushort)~crc;
    }
}

public sealed record BacnetMstpFrame(long Id, long Session, DateTimeOffset Timestamp, ProtocolDirection Direction,
    ReadOnlyMemory<byte> Bytes, ProtocolFrameStatus Status, ProtocolCrcStatus HeaderCrc, ProtocolCrcStatus DataCrc) : IProtocolFrame
{
    public const int MaximumClassicDataBytes = 501;
    public const int MaximumBytes = 2048; // Also bounds unsupported extended frames/noise.
    public int Length => Bytes.Length;
    public long RetainedBytes => Length + 256L;
    public byte? FrameType => HeaderCrc == ProtocolCrcStatus.Valid && Length >= 8 ? Bytes.Span[2] : null;
    public byte? Destination => HeaderCrc == ProtocolCrcStatus.Valid && Length >= 8 ? Bytes.Span[3] : null;
    public byte? Source => HeaderCrc == ProtocolCrcStatus.Valid && Length >= 8 ? Bytes.Span[4] : null;
    public byte? Address => Source;
    public byte? Code => FrameType;
    public string CodeName => HeaderCrc == ProtocolCrcStatus.Invalid ? "Untrusted header" : TypeName(FrameType);
    public ProtocolCrcStatus CrcStatus => HeaderCrc == ProtocolCrcStatus.Invalid || DataCrc == ProtocolCrcStatus.Invalid
        ? ProtocolCrcStatus.Invalid : Status == ProtocolFrameStatus.Incomplete || HeaderCrc == ProtocolCrcStatus.Unavailable
        ? ProtocolCrcStatus.Unavailable : DataCrc == ProtocolCrcStatus.Valid ? ProtocolCrcStatus.Valid
        : Length == 8 && Bytes.Span[5] == 0 && Bytes.Span[6] == 0 ? ProtocolCrcStatus.Valid : ProtocolCrcStatus.Unavailable;
    public bool MatchesCode(byte code) => FrameType == code;
    public static string TypeName(byte? type) => type switch
    {
        0 => "Token", 1 => "Poll For Master", 2 => "Reply To Poll For Master",
        3 => "Test Request", 4 => "Test Response", 5 => "BACnet Data Expecting Reply",
        6 => "BACnet Data Not Expecting Reply", 7 => "Reply Postponed",
        >= 32 and <= 127 => "Extended frame (not decoded)", _ => "Undecoded frame"
    };
    public string Describe()
    {
        var text = new StringBuilder();
        text.AppendLine($"{Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} · {Direction} · BACnet MS/TP · {Status}");
        text.AppendLine($"Session: {Session}; bytes: {Length}; header CRC: {HeaderCrc}; data CRC: {DataCrc}");
        if (HeaderCrc == ProtocolCrcStatus.Valid)
        {
            int size = BinaryPrimitives.ReadUInt16BigEndian(Bytes.Span[5..]);
            text.AppendLine($"Source MAC: {Source}; destination MAC: {Destination}; frame type: {FrameType:X2} ({CodeName}); declared data bytes: {size}");
            if (size == 0) text.AppendLine("Zero-length control frame: no data CRC field.");
            if (Status == ProtocolFrameStatus.Decoded && FrameType is 5 or 6)
                text.AppendLine("MS/TP integrity verified. NPDU/APDU service contents are not decoded.");
        }
        else text.AppendLine("Header fields are untrusted; no addresses, type or payload interpretation is applied.");
        if (Status == ProtocolFrameStatus.CrcError) text.AppendLine("CRC error; payload is not interpreted as successful BACnet data.");
        if (Status == ProtocolFrameStatus.Undecoded) text.AppendLine("Reserved/vendor-specific/extended frame or invalid length. No unsupported CRC variant is claimed as verified.");
        text.AppendLine(); text.Append(string.Join(" ", Bytes.ToArray().Select(value => value.ToString("X2"))));
        return text.ToString();
    }
}
