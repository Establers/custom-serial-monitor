using System.Diagnostics;
using System.Globalization;
using System.Text;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed class LogHistoryExporter : ILogHistoryExporter
{
    public Task ExportAsync(string path, IReadOnlyList<LogLine> snapshot, CancellationToken cancellationToken,
        IProgress<LogHistoryExportProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);

        // Opening, formatting and committing a large history must stay off the UI thread.
        return Task.Run(() => ExportCoreAsync(path, snapshot, cancellationToken, progress), cancellationToken);
    }

    private static async Task ExportCoreAsync(string path, IReadOnlyList<LogLine> snapshot, CancellationToken cancellationToken,
        IProgress<LogHistoryExportProgress>? progress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(path);
        var temporaryPath = Path.Combine(Path.GetDirectoryName(destination)!, $".serial-history-{Guid.NewGuid():N}.tmp");
        var totalRecords = snapshot.Count;
        var writtenRecords = 0;
        var reportedAt = Stopwatch.GetTimestamp();
        progress?.Report(new(0, totalRecords));
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 64 * 1024))
            {
                var prefixBuffer = new char[64];
                foreach (var line in snapshot)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Match the live file logger, including RX fragments and their terminators.
                    // Reuse the prefix buffer and stream the existing payload string directly:
                    // one million records must not allocate one million concatenated log strings.
                    var prefixLength = FormatPrefix(line, prefixBuffer);
                    await writer.WriteAsync(prefixBuffer.AsMemory(0, prefixLength), cancellationToken).ConfigureAwait(false);
                    await writer.WriteLineAsync(line.FileText.AsMemory(), cancellationToken).ConfigureAwait(false);
                    writtenRecords++;
                    if (progress is not null && Stopwatch.GetElapsedTime(reportedAt) >= TimeSpan.FromMilliseconds(100))
                    {
                        progress.Report(new(writtenRecords, totalRecords));
                        reportedAt = Stopwatch.GetTimestamp();
                    }
                }

                progress?.Report(new(writtenRecords, totalRecords));
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            // A failed or canceled export never replaces a completed destination with a partial file.
            File.Move(temporaryPath, destination, overwrite: true);
            progress?.Report(new(writtenRecords, totalRecords, IsCompleted: true));
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static int FormatPrefix(LogLine line, char[] buffer)
    {
        buffer[0] = '[';
        if (!line.Timestamp.LocalDateTime.TryFormat(buffer.AsSpan(1), out var timestampLength,
            LogLine.GetTimestampFormatPattern(TimestampDisplayFormat.DateTimeMilliseconds), CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("Log timestamp exceeds the export prefix buffer.");
        }

        var offset = 1 + timestampLength;
        buffer[offset++] = ']';
        buffer[offset++] = ' ';
        var direction = line.DirectionText;
        direction.AsSpan().CopyTo(buffer.AsSpan(offset));
        offset += direction.Length;
        buffer[offset++] = ' ';
        return offset;
    }
}
