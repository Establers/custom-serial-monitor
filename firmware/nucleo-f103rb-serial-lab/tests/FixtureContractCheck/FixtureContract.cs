using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Expected data is authored independently of the C generator, using standard encodings.
internal static class FixtureContract
{
    public static readonly string[] Names = ["TEXT","UTF8","CP949","BYTES","NEWLINES","EVENTS","JSON","ANSI","CONTROL"];
    public static byte[] Payload(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return name switch {
            "TEXT" => Encoding.ASCII.GetBytes("alpha  beta\r\nGamma 123 !@#\r\n"),
            "UTF8" => Encoding.UTF8.GetBytes("안녕하세요 日本語 🚀\r\n"),
            "CP949" => Encoding.GetEncoding(949,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetBytes("안녕하세요\r\n"),
            "BYTES" => Enumerable.Range(0,256).Select(i => (byte)i).ToArray(),
            "NEWLINES" => Encoding.ASCII.GetBytes("CRLF\r\nLF\nCR\rEND\r\n"),
            "EVENTS" => Encoding.ASCII.GetBytes("[INFO] before\r\n[WARN] sensor=mock\r\n[ERROR] fault=mock\r\n[INFO] after\r\n"),
            "JSON" => Encoding.ASCII.GetBytes("{\"sensor\":\"mock\",\"seq\":1,\"ok\":true}\r\n"),
            "ANSI" => Encoding.ASCII.GetBytes("\x1b[32mGREEN\x1b[0m \x1b[31mRED\x1b[0m\r\n"),
            "CONTROL" => [65,9,66,8,67,0,68,13,10],
            _ => throw new ArgumentException("Unknown reference fixture: " + name)
        };
    }

    public static void AssertResponse(byte[] received,string name,string run,string build)
    {
        byte[] payload = Payload(name);
        ushort crc = SerialLabWire.Crc16(payload,0,payload.Length);
        byte[] prefix = Encoding.ASCII.GetBytes($"BEGIN run={run} case={name} len={payload.Length} crc16={crc:X4} build={build}\r\n");
        byte[] suffix = Encoding.ASCII.GetBytes($"\r\nEND run={run} case={name}\r\n");
        SerialLabWire.AssertBytes(received,[..prefix,..payload,..suffix]);
    }

    public static (string Build,string Version) Manifest(string path)
    {
        path = Path.GetFullPath(path);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var manifest = json.RootElement;
        if (manifest.GetProperty("schemaVersion").GetInt32() != 2) throw new Exception("Need candidate manifest schemaVersion=2.");
        string binary = Path.Combine(Path.GetDirectoryName(path)!,"serial-lab.bin");
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binary))) != manifest.GetProperty("sha256").GetString())
            throw new Exception("Binary SHA-256 differs from selected manifest.");
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!,"..")) + Path.DirectorySeparatorChar;
        foreach (var source in manifest.GetProperty("sources").EnumerateArray())
        {
            string file = Path.GetFullPath(Path.Combine(root,source.GetProperty("path").GetString()!));
            if (!file.StartsWith(root,StringComparison.OrdinalIgnoreCase)) throw new Exception("Source path outside firmware root.");
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) != source.GetProperty("sha256").GetString())
                throw new Exception("Source changed since build: " + source.GetProperty("path").GetString());
        }
        return (manifest.GetProperty("buildId").GetString()!,manifest.GetProperty("firmwareVersion").GetString()!);
    }

    public static void AssertBuild(byte[] received,string build,string version) =>
        SerialLabWire.AssertBytes(received,Encoding.ASCII.GetBytes($"BUILD version={version} protocol=1 id={build}\r\n"));
    public static void AssertSelftest(byte[] received,string build) =>
        SerialLabWire.AssertBytes(received,Encoding.ASCII.GetBytes($"SELFTEST PASS crc=PASS fixtures=PASS limits=PASS build={build}\r\n"));
    public static void AssertCaps(byte[] received,string build,string version) =>
        SerialLabWire.AssertBytes(received,Encoding.ASCII.GetBytes($"CAPS protocol=1 version={version} build={build} line_max=511 payload_max=1024 long_max=2048 fixture_count=9 fixtures={string.Join(',',Names)}\r\n"));
}
