using System.Text;

namespace SerialMonitor.WinUI.Infrastructure;

public static class RuntimeDiagnostics
{
    private static readonly DiagnosticFileWriter Writer = new(WriteTextAsync);

    public static string DirectoryPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerialMonitor", "diagnostics");

    public static string LastErrorPath => Path.Combine(DirectoryPath, "last_runtime_error.txt");

    public static string StartupPath => Path.Combine(DirectoryPath, "last_startup.txt");

    public static string LastShutdownPath => Path.Combine(DirectoryPath, "last_shutdown.txt");

    public static void RecordStartup()
    {
        ClearLastError();
        Writer.Enqueue(DiagnosticFile.Startup, $"Started: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}{Environment.NewLine}");
    }

    public static void ClearLastError()
    {
        Writer.Enqueue(DiagnosticFile.Error, null);
    }

    public static void RecordError(string source, Exception exception)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
        builder.AppendLine($"Source: {source}");
        builder.AppendLine(exception.ToString());
        Writer.Enqueue(DiagnosticFile.Error, builder.ToString());
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
        Writer.Enqueue(DiagnosticFile.Shutdown, text);
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

    public static Task FlushAsync(TimeSpan timeout) => Writer.FlushAsync(timeout);

    // Fatal handlers cannot await before the process exits. Bound their final wait.
    public static void FlushBeforeFatalExit() =>
        FlushAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

    private static async Task WriteTextAsync(DiagnosticFile file, string? text)
    {
        var path = file switch
        {
            DiagnosticFile.Error => LastErrorPath,
            DiagnosticFile.Startup => StartupPath,
            DiagnosticFile.Shutdown => LastShutdownPath,
            _ => throw new ArgumentOutOfRangeException(nameof(file))
        };
        if (text is null) { File.Delete(path); return; }
        Directory.CreateDirectory(DirectoryPath);
        await File.WriteAllTextAsync(path, text).ConfigureAwait(false);
    }
}
