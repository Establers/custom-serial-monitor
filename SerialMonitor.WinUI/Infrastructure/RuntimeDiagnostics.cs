using System.Text;

namespace SerialMonitor.WinUI.Infrastructure;

public static class RuntimeDiagnostics
{
    private const int FileWriterIncidentQueueCapacity = 256;
    private static readonly DiagnosticOperationGates OperationGates = new();
    private static readonly object FileWriterIncidentSessionGate = new();
    private static BoundedIncidentWriter? _fileWriterIncidentWriter;
    private static bool _fileWriterIncidentSessionActive;

    public static string DirectoryPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerialMonitor", "diagnostics");

    public static string LastErrorPath => Path.Combine(DirectoryPath, "last_runtime_error.txt");

    public static string StartupPath => Path.Combine(DirectoryPath, "last_startup.txt");

    public static string LastShutdownPath => Path.Combine(DirectoryPath, "last_shutdown.txt");

    public static string FileWriterIncidentPath => Path.Combine(DirectoryPath, "file_writer_incidents.log");

    public static void RecordStartup()
    {
        WriteText(StartupPath, $"Started: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}{Environment.NewLine}");
    }

    public static void ClearLastError()
    {
        OperationGates.RunGeneral(() =>
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
        });
    }

    public static void RecordError(string source, Exception exception)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
        builder.AppendLine($"Source: {source}");
        builder.AppendLine(exception.ToString());
        WriteText(LastErrorPath, builder.ToString());
    }

    public static void RecordFileWriterIncident(string message)
    {
        BoundedIncidentWriter? writer;
        lock (FileWriterIncidentSessionGate)
        {
            if (!_fileWriterIncidentSessionActive)
            {
                return;
            }

            writer = GetOrCreateFileWriterIncidentWriterLocked();
        }

        writer?.TryEnqueue(message);
    }

    public static void StartFileWriterIncidentSession()
    {
        lock (FileWriterIncidentSessionGate)
        {
            _fileWriterIncidentSessionActive = true;
            _ = GetOrCreateFileWriterIncidentWriterLocked();
        }
    }

    public static async Task<bool> CompleteFileWriterIncidentSessionAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        BoundedIncidentWriter? writer;
        lock (FileWriterIncidentSessionGate)
        {
            _fileWriterIncidentSessionActive = false;
            writer = _fileWriterIncidentWriter;
        }

        return writer is null ||
            await writer.CompleteAndDrainAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteFileWriterIncident(string message)
    {
        const long maximumIncidentBytes = 256 * 1024;
        OperationGates.RunFileWriterIncident(() =>
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                if (File.Exists(FileWriterIncidentPath) &&
                    new FileInfo(FileWriterIncidentPath).Length >= maximumIncidentBytes)
                {
                    File.Move(
                        FileWriterIncidentPath,
                        FileWriterIncidentPath + ".previous",
                        overwrite: true);
                }

                File.AppendAllText(
                    FileWriterIncidentPath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}{Environment.NewLine}");
            }
            catch
            {
            }
        });
    }

    private static BoundedIncidentWriter? GetOrCreateFileWriterIncidentWriterLocked()
    {
        if (_fileWriterIncidentWriter is null ||
            (_fileWriterIncidentWriter.IsCompleted && _fileWriterIncidentWriter.Completion.IsCompleted))
        {
            _fileWriterIncidentWriter = new BoundedIncidentWriter(
                FileWriterIncidentQueueCapacity,
                WriteFileWriterIncident);
        }

        return _fileWriterIncidentWriter.IsCompleted ? null : _fileWriterIncidentWriter;
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
        OperationGates.RunGeneral(() =>
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(path, text);
        });
    }
}

internal sealed class DiagnosticOperationGates
{
    private readonly object _generalGate = new();
    private readonly object _fileWriterIncidentGate = new();

    public void RunGeneral(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_generalGate)
        {
            operation();
        }
    }

    public void RunFileWriterIncident(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_fileWriterIncidentGate)
        {
            operation();
        }
    }
}
