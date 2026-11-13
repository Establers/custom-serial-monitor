using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

// Independent bit-loop CRC and byte layouts. Never use the application's CRC,
// request builder or response decoder to decide whether live wire bytes pass.
internal static class Oracle
{
    public static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    public static ushort Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFF;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xA001;
        }
        return (ushort)crc;
    }
    public static byte[] Adu(params byte[] body)
    {
        var crc = Crc(body);
        return [.. body, (byte)crc, (byte)(crc >> 8)];
    }
    public static byte[] Read(byte function, int address, int count) =>
        Adu(1, function, (byte)(address >> 8), (byte)address, (byte)(count >> 8), (byte)count);
    public static byte[] Single(byte function, int address, ushort value)
    {
        var wire = function == 5 ? (ushort)(value == 0 ? 0 : 0xFF00) : value;
        return Adu(1, function, (byte)(address >> 8), (byte)address, (byte)(wire >> 8), (byte)wire);
    }
    public static byte[] Multiple(byte function, int address, IReadOnlyList<ushort> values)
    {
        var data = new byte[function == 15 ? (values.Count + 7) / 8 : values.Count * 2];
        for (var index = 0; index < values.Count; index++)
            if (function == 15) data[index / 8] |= (byte)(values[index] << (index % 8));
            else BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(index * 2), values[index]);
        return Adu([1, function, (byte)(address >> 8), (byte)address, (byte)(values.Count >> 8), (byte)values.Count, (byte)data.Length, .. data]);
    }
    public static byte[] Reply(byte function, params ushort[] values)
    {
        if (function <= 2)
        {
            var data = new byte[(values.Length + 7) / 8];
            for (var index = 0; index < values.Length; index++) data[index / 8] |= (byte)(values[index] << (index % 8));
            return Adu([1, function, (byte)data.Length, .. data]);
        }
        var words = new byte[values.Length * 2];
        for (var index = 0; index < values.Length; index++) BinaryPrimitives.WriteUInt16BigEndian(words.AsSpan(index * 2), values[index]);
        return Adu([1, function, (byte)words.Length, .. words]);
    }
    public static byte[] Ack(byte[] request) => request[1] is 5 or 6 ? request.ToArray() : Adu(request[..6]);
    public static ushort[] Values(byte[] request, byte[] reply)
    {
        Require(Crc(request) == 0 && Crc(reply) == 0, "Independent RTU CRC check failed.");
        Require(reply[0] == request[0] && reply[1] == request[1], "Response unit/function differs from request.");
        var quantity = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(4));
        var bytes = request[1] <= 2 ? (quantity + 7) / 8 : quantity * 2;
        Require(reply.Length == bytes + 5 && reply[2] == bytes, "Response length/byte count differs from request.");
        if (request[1] <= 2 && quantity % 8 != 0)
            Require((reply[^3] & (0xFF << (quantity % 8))) == 0, "Unused response coil/discrete bits must be zero.");
        var result = new ushort[quantity];
        for (var index = 0; index < result.Length; index++) result[index] = request[1] <= 2
            ? (ushort)((reply[3 + index / 8] >> (index % 8)) & 1)
            : BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(3 + index * 2));
        return result;
    }
    public static List<byte[]> Frames(byte[] bytes)
    {
        var frames = new List<byte[]>();
        for (var offset = 0; offset < bytes.Length;)
        {
            Require(bytes.Length - offset >= 5, "Incomplete RTU response in evidence.");
            var function = bytes[offset + 1];
            var length = (function & 128) != 0 ? 5 : function is >= 1 and <= 4 ? bytes[offset + 2] + 5 : 8;
            Require(length <= bytes.Length - offset && length <= 256, "Invalid response extent in evidence.");
            var frame = bytes.AsSpan(offset, length).ToArray();
            Require(Crc(frame) == 0, "Bad CRC in normal exchange evidence.");
            frames.Add(frame); offset += length;
        }
        return frames;
    }
    public static uint UInt32(ushort[] words, int offset) => ((uint)words[offset] << 16) | words[offset + 1];
    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

internal sealed record BuildManifest(string Version, string BuildId, string Sha256, string Path)
{
    public static BuildManifest Load(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        var version = root.GetProperty("firmwareVersion").GetString() ?? "";
        var build = root.GetProperty("buildId").GetString() ?? "";
        var hash = root.GetProperty("sha256").GetString() ?? "";
        Oracle.Require(version.Length > 0 && build.Length == 16 && build.All(Uri.IsHexDigit) && hash.Length == 64 && hash.All(Uri.IsHexDigit), "Invalid manifest identity.");
        Oracle.Require(root.GetProperty("board").GetString() == "NUCLEO-F103RB" && root.GetProperty("baudRate").GetInt32() == 115200, "Unexpected board/baud in manifest.");
        var binary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "serial-lab.bin");
        Oracle.Require(File.Exists(binary) && string.Equals(Oracle.Hash(binary), hash, StringComparison.OrdinalIgnoreCase), "Manifest image SHA-256 mismatch.");
        Oracle.Require(new FileInfo(binary).Length == root.GetProperty("binaryBytes").GetInt64(), "Manifest image size mismatch.");
        return new(version, build, hash, path);
    }
}
