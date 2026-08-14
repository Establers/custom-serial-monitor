using System.Text;

namespace SerialMonitor.WinUI.Infrastructure;

public static class RuntimeDiagnostics
{
    private const int MaximumGeneralDiagnosticTextCharacters = 64 * 1024;
    private const int GeneralDiagnosticQueueCapacity = 128;
    private const int FileWriterIncidentQueueCapacity = 256;
    private static readonly TimeSpan FatalDiagnosticWaitTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly DiagnosticOperationGates OperationGates = new();
    private static readonly BoundedEmergencyDiagnosticWriter EmergencyDiagnosticWriter =
        new(WriteFatalDiagnostic);
    private static readonly object GeneralDiagnosticSessionGate = new();
    private static readonly object FileWriterIncidentSessionGate = new();
    private static BoundedDiagnosticWriter? _generalDiagnosticWriter;
    private static BoundedIncidentWriter? _fileWriterIncidentWriter;
    private static bool _generalDiagnosticSessionActive;
    private static bool _generalRestartContinuationScheduled;
    private static bool _fileWriterIncidentSessionActive;
    private static string _lastErrorSnapshot = string.Empty;
    private static string _lastShutdownSnapshot = string.Empty;
    private static long _lastErrorRecordedAtUtcTicks;

    public static string DirectoryPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerialMonitor", "diagnostics");

    public static string LastErrorPath => Path.Combine(DirectoryPath, "last_runtime_error.txt");

    public static string StartupPath => Path.Combine(DirectoryPath, "last_startup.txt");

    public static string LastShutdownPath => Path.Combine(DirectoryPath, "last_shutdown.txt");

    public static string FileWriterIncidentPath => Path.Combine(DirectoryPath, "file_writer_incidents.log");

    public static string GeneralDiagnosticOverflowPath =>
        Path.Combine(DirectoryPath, "general_diagnostic_overflow.log");

    public static string FatalErrorPath => Path.Combine(DirectoryPath, "fatal_runtime_error.txt");

    public static DateTimeOffset? LastErrorRecordedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastErrorRecordedAtUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public static void StartGeneralDiagnosticSession()
    {
        try
        {
            lock (GeneralDiagnosticSessionGate)
            {
                if (_generalDiagnosticSessionActive)
                {
                    return;
                }

                _generalDiagnosticSessionActive = true;
                if (TryCreateGeneralDiagnosticWriterLocked())
                {
                    QueueLoadExistingLocked();
                    return;
                }

                ScheduleGeneralDiagnosticRestartLocked();
            }
        }
        catch
        {
        }
    }

    public static void RecordStartup()
    {
        try
        {
            var now = DateTimeOffset.Now;
            TryQueueGeneral(new GeneralDiagnosticWork(
                GeneralDiagnosticWorkKind.Startup,
                $"Started: {now:yyyy-MM-dd HH:mm:ss.fff zzz}{Environment.NewLine}",
                now));
        }
        catch
        {
        }
    }

    public static void ClearLastError()
    {
        try
        {
            TryQueueGeneral(new GeneralDiagnosticWork(
                GeneralDiagnosticWorkKind.ClearLastError,
                string.Empty,
                DateTimeOffset.UtcNow));
        }
        catch
        {
        }
    }

    public static void RecordError(string source, Exception exception)
    {
        try
        {
            var now = DateTimeOffset.Now;
            TryQueueGeneral(new GeneralDiagnosticWork(
                GeneralDiagnosticWorkKind.Error,
                string.Empty,
                now,
                LimitText(source ?? string.Empty, 1024),
                exception));
        }
        catch
        {
        }
    }

    public static void RecordFatalError(string source, Exception exception)
    {
        try
        {
            RecordError(source, exception);
        }
        catch
        {
        }

        try
        {
            EmergencyDiagnosticWriter.TryWrite(
                new FatalDiagnosticWork(
                    source ?? string.Empty,
                    exception,
                    DateTimeOffset.Now),
                FatalDiagnosticWaitTimeout);
        }
        catch
        {
        }
    }

    public static string ReadLastError() => Volatile.Read(ref _lastErrorSnapshot) ?? string.Empty;

    public static void RecordShutdown(string text)
    {
        try
        {
            var value = LimitText(text ?? string.Empty, MaximumGeneralDiagnosticTextCharacters);
            lock (GeneralDiagnosticSessionGate)
            {
                if (!_generalDiagnosticSessionActive ||
                    _generalDiagnosticWriter is null ||
                    _generalDiagnosticWriter.IsCompleted)
                {
                    return;
                }

                // Shutdown is the only staged critical diagnostic. It consumes
                // one bounded session slot outside the hot-path queue and the
                // existing pre-close FlushAsync owns both its capacity wait and
                // its durability wait under one absolute deadline.
                _generalDiagnosticWriter.TryStageCriticalWork(new GeneralDiagnosticWork(
                    GeneralDiagnosticWorkKind.Shutdown,
                    value,
                    DateTimeOffset.Now));
            }
        }
        catch
        {
        }
    }

    public static string ReadLastShutdown() => Volatile.Read(ref _lastShutdownSnapshot) ?? string.Empty;

    public static async Task<bool> FlushGeneralDiagnosticSessionAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            BoundedDiagnosticWriter? writer;
            lock (GeneralDiagnosticSessionGate)
            {
                writer = _generalDiagnosticWriter;
            }

            return writer is null ||
                await writer.FlushAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> CompleteGeneralDiagnosticSessionAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            BoundedDiagnosticWriter? writer;
            lock (GeneralDiagnosticSessionGate)
            {
                _generalDiagnosticSessionActive = false;
                writer = _generalDiagnosticWriter;
            }

            return writer is null ||
                await writer.CompleteAndDrainAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    public static void RecordFileWriterIncident(string message)
    {
        try
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
        catch
        {
        }
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

    private static bool TryQueueGeneral(GeneralDiagnosticWork work)
    {
        lock (GeneralDiagnosticSessionGate)
        {
            if (!_generalDiagnosticSessionActive ||
                _generalDiagnosticWriter is null ||
                _generalDiagnosticWriter.IsCompleted)
            {
                return false;
            }

            return _generalDiagnosticWriter.TryEnqueue(work);
        }
    }

    private static bool TryCreateGeneralDiagnosticWriterLocked()
    {
        if (_generalDiagnosticWriter is not null)
        {
            if (!_generalDiagnosticWriter.IsCompleted)
            {
                return true;
            }

            if (!_generalDiagnosticWriter.Completion.IsCompleted)
            {
                return false;
            }
        }

        _generalDiagnosticWriter = new BoundedDiagnosticWriter(
            GeneralDiagnosticQueueCapacity,
            ExecuteGeneralDiagnosticWork);
        return true;
    }

    private static void QueueLoadExistingLocked()
    {
        _generalDiagnosticWriter?.TryEnqueue(new GeneralDiagnosticWork(
            GeneralDiagnosticWorkKind.LoadExisting,
            string.Empty,
            DateTimeOffset.UtcNow));
    }

    private static void ScheduleGeneralDiagnosticRestartLocked()
    {
        if (_generalRestartContinuationScheduled || _generalDiagnosticWriter is null)
        {
            return;
        }

        _generalRestartContinuationScheduled = true;
        _ = _generalDiagnosticWriter.Completion.ContinueWith(
            static _ =>
            {
                lock (GeneralDiagnosticSessionGate)
                {
                    _generalRestartContinuationScheduled = false;
                    if (_generalDiagnosticSessionActive && TryCreateGeneralDiagnosticWriterLocked())
                    {
                        QueueLoadExistingLocked();
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ExecuteGeneralDiagnosticWork(GeneralDiagnosticWork work, long droppedBefore)
    {
        OperationGates.RunGeneral(() =>
        {
            Directory.CreateDirectory(DirectoryPath);
            if (droppedBefore > 0)
            {
                AppendBoundedOverflowSummary(droppedBefore, work.Timestamp);
            }

            switch (work.Kind)
            {
                case GeneralDiagnosticWorkKind.LoadExisting:
                    LoadExistingSnapshots();
                    break;
                case GeneralDiagnosticWorkKind.Startup:
                    File.WriteAllText(StartupPath, work.Text);
                    break;
                case GeneralDiagnosticWorkKind.Error:
                    var errorText = FormatErrorWork(work);
                    File.WriteAllText(LastErrorPath, errorText);
                    Volatile.Write(ref _lastErrorSnapshot, errorText);
                    Interlocked.Exchange(ref _lastErrorRecordedAtUtcTicks, work.Timestamp.UtcTicks);
                    break;
                case GeneralDiagnosticWorkKind.Shutdown:
                    File.WriteAllText(LastShutdownPath, work.Text);
                    Volatile.Write(ref _lastShutdownSnapshot, work.Text);
                    break;
                case GeneralDiagnosticWorkKind.ClearLastError:
                    if (File.Exists(LastErrorPath))
                    {
                        File.Delete(LastErrorPath);
                    }

                    Volatile.Write(ref _lastErrorSnapshot, string.Empty);
                    Interlocked.Exchange(ref _lastErrorRecordedAtUtcTicks, 0);

                    break;
            }
        });
    }

    private static void LoadExistingSnapshots()
    {
        try
        {
            if (File.Exists(LastErrorPath))
            {
                Volatile.Write(ref _lastErrorSnapshot, File.ReadAllText(LastErrorPath));
                Interlocked.Exchange(
                    ref _lastErrorRecordedAtUtcTicks,
                    File.GetLastWriteTimeUtc(LastErrorPath).Ticks);
            }

            if (File.Exists(LastShutdownPath))
            {
                Volatile.Write(ref _lastShutdownSnapshot, File.ReadAllText(LastShutdownPath));
            }
        }
        catch
        {
        }
    }

    private static void AppendBoundedOverflowSummary(long droppedBefore, DateTimeOffset timestamp)
    {
        const long maximumBytes = 64 * 1024;
        try
        {
            if (File.Exists(GeneralDiagnosticOverflowPath) &&
                new FileInfo(GeneralDiagnosticOverflowPath).Length >= maximumBytes)
            {
                File.WriteAllText(GeneralDiagnosticOverflowPath, string.Empty);
            }

            File.AppendAllText(
                GeneralDiagnosticOverflowPath,
                $"[{timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] Dropped {droppedBefore:N0} newer general diagnostic operation(s) while the bounded queue was full.{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static void AppendExceptionSafely(StringBuilder builder, Exception? exception)
    {
        if (exception is null)
        {
            builder.AppendLine("<null exception>");
            return;
        }

        var current = exception;
        var depth = 0;
        while (current is not null && depth < 16 && builder.Length < MaximumGeneralDiagnosticTextCharacters)
        {
            string typeName;
            string message;
            string stackTrace;
            try { typeName = current.GetType().FullName ?? current.GetType().Name; }
            catch { typeName = "<exception type unavailable>"; }
            try { message = current.Message ?? string.Empty; }
            catch { message = "<exception message unavailable>"; }
            try { stackTrace = current.StackTrace ?? string.Empty; }
            catch { stackTrace = "<stack trace unavailable>"; }

            builder.Append(typeName);
            builder.Append(": ");
            builder.AppendLine(LimitText(message, 16 * 1024));
            if (!string.IsNullOrWhiteSpace(stackTrace))
            {
                builder.AppendLine(LimitText(stackTrace, 32 * 1024));
            }

            try { current = current.InnerException; }
            catch { current = null; }
            depth++;
        }
    }

    private static string FormatErrorWork(GeneralDiagnosticWork work)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Time: {work.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}");
        builder.AppendLine($"Source: {work.Source}");
        AppendExceptionSafely(builder, work.Exception);
        return LimitText(builder.ToString(), MaximumGeneralDiagnosticTextCharacters);
    }

    private static void WriteFatalDiagnostic(FatalDiagnosticWork work)
    {
        Directory.CreateDirectory(DirectoryPath);
        var errorText = FormatErrorWork(new GeneralDiagnosticWork(
            GeneralDiagnosticWorkKind.Error,
            string.Empty,
            work.Timestamp,
            LimitText(work.Source ?? string.Empty, 1024),
            work.Exception));
        File.WriteAllText(FatalErrorPath, errorText);
    }

    private static string LimitText(string value, int maximumCharacters)
    {
        if (value.Length <= maximumCharacters)
        {
            return value;
        }

        const string suffix = "\n[diagnostic text truncated]";
        var prefixLength = Math.Max(0, maximumCharacters - suffix.Length);
        return value[..prefixLength] + suffix;
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
