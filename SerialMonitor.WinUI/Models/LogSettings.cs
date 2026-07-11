namespace SerialMonitor.WinUI.Models;

public sealed class LogSettings
{
    public bool FileLoggingEnabled { get; set; }

    public string SaveDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerialMonitor", "logs");

    public bool DailyRotationEnabled { get; set; } = true;

    public bool SizeRotationEnabled { get; set; }

    public long? SizeRotationBytes { get; set; }

    public bool RawBinaryLoggingEnabled { get; set; }

    public bool UseSessionNameInFileName { get; set; }

    public LogSettings Clone()
    {
        return new LogSettings
        {
            FileLoggingEnabled = FileLoggingEnabled,
            SaveDirectory = SaveDirectory,
            DailyRotationEnabled = DailyRotationEnabled,
            SizeRotationEnabled = SizeRotationEnabled,
            SizeRotationBytes = SizeRotationBytes,
            RawBinaryLoggingEnabled = RawBinaryLoggingEnabled,
            UseSessionNameInFileName = UseSessionNameInFileName
        };
    }
}
