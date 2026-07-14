using System.Text.Json.Serialization;

namespace SerialMonitor.WinUI.Models;

public sealed class LogSettings
{
    [JsonIgnore]
    public bool FileLoggingEnabled { get; set; }

    public string SaveDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerialMonitor", "logs");

    public bool SizeRotationEnabled { get; set; }

    public long? SizeRotationBytes { get; set; }

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
