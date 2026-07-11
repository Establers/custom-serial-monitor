using System.Text;

namespace SerialMonitor.WinUI.Infrastructure;

public static class RuntimeDiagnostics
{
    private static readonly object Gate = new();

    public static string DirectoryPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerialMonitor", "diagnostics");

    public static string LastErrorPath => Path.Combine(DirectoryPath, "last_runtime_error.txt");

    public static string StartupPath => Path.Combine(DirectoryPath, "last_startup.txt");

    public static string LastShutdownPath => Path.Combine(DirectoryPath, "last_shutdown.txt");

    public static void RecordStartup()
    {
        ClearLastError();
        WriteText(StartupPath, $"Started: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}{Environment.NewLine}");
    }

    public static void ClearLastError()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(LastErrorPath))
                {
                    File.Delete(LastErrorPath);
                }
            }
            catch
            {
            }
        }
    }

    public static void RecordError(string source, Exception exception)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
        builder.AppendLine($"Source: {source}");
        builder.AppendLine(exception.ToString());
        WriteText(LastErrorPath, builder.ToString());
    }

    public static string ReadLastError()
    {
        try
        {
            return File.Exists(LastErrorPath) ? File.ReadAllText(LastErrorPath) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static void RecordShutdown(string text)
    {
        WriteText(LastShutdownPath, text);
    }

    public static string ReadLastShutdown()
    {
        try
        {
            return File.Exists(LastShutdownPath) ? File.ReadAllText(LastShutdownPath) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void WriteText(string path, string text)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(path, text);
        }
    }
}
