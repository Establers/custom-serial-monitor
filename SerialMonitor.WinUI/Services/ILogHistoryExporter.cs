using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public interface ILogHistoryExporter
{
    Task ExportAsync(string path, IReadOnlyList<LogLine> snapshot, CancellationToken cancellationToken,
        IProgress<LogHistoryExportProgress>? progress = null);
}
