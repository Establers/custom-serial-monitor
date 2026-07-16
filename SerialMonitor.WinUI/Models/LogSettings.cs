using System.Text.Json.Serialization;

namespace SerialMonitor.WinUI.Models;

public sealed class LogSettings
{
    public const long BytesPerMegabyte = 1024L * 1024;
    public const long DefaultSizeRotationMegabytes = 10;
    public const long DefaultSizeRotationBytes = DefaultSizeRotationMegabytes * BytesPerMegabyte;

    [JsonIgnore]
    public bool FileLoggingEnabled { get; set; }

    public string SaveDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerialMonitor", "logs");

    public bool SizeRotationEnabled { get; set; }

    public long? SizeRotationBytes { get; set; } = DefaultSizeRotationBytes;

    public LogSettings Clone()
    {
        return new LogSettings
        {
            FileLoggingEnabled = FileLoggingEnabled,
            SaveDirectory = SaveDirectory,
            SizeRotationEnabled = SizeRotationEnabled,
            SizeRotationBytes = SizeRotationBytes
        };
    }
}
