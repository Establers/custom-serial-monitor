using System.ComponentModel;
using Microsoft.UI.Dispatching;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    private readonly ILogHistoryExporter _logHistoryExporter;
    private readonly CancellationTokenSource _logHistoryExportCancellation = new();
    private Task? _logHistoryExportTask;
    private DispatcherQueueTimer? _logHistoryExportProgressTimer;
    private bool _isLogHistoryExporting;

    public LogHistoryExportViewModel LogHistoryExport { get; } = new();

    public bool CanExportLogHistory => !_isLogHistoryExporting && Log.TotalRetainedLineCount > 0 &&
        Volatile.Read(ref _shutdownStarted) == 0 && !_logHistoryExportCancellation.IsCancellationRequested;

    public async Task ExportLogHistoryAsync(Func<string, Task<string?>> selectPath)
    {
        if (!CanExportLogHistory) return;

        _isLogHistoryExporting = true;
        OnPropertyChanged(nameof(CanExportLogHistory));
        var cancellationToken = _logHistoryExportCancellation.Token;
        try
        {
            // Freeze the UI-owned history synchronously at the button click, before
            // opening the picker or yielding to reception/retention updates.
            var capturedAt = DateTimeOffset.Now;
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = Log.GetRetainedLinesSnapshot();
            if (snapshot.Count == 0)
            {
                RecordLogFileActionError(UiText.Get("LogHistoryEmpty", "No current logs to export."));
                return;
            }

            var suggestedName = LogHistoryExportFileName.Create(snapshot[0].Timestamp, capturedAt);
            var path = await selectPath(suggestedName);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) return;

            cancellationToken = LogHistoryExport.Begin(cancellationToken);

            var currentLogFilePath = _fileLogWriter.CurrentLogFilePath;
            if (_fileLogWriter.IsRunning && !string.IsNullOrWhiteSpace(currentLogFilePath) &&
                string.Equals(Path.GetFullPath(path), Path.GetFullPath(currentLogFilePath), StringComparison.OrdinalIgnoreCase))
            {
                RecordLogFileActionError(UiText.Get("LogHistoryActiveFile", "Choose a different file; this file is being used for live logging."));
                return;
            }

            RecordLogFileActionSuccess(UiText.Format("LogHistorySaving", "Exporting {0:N0} log records...", snapshot.Count));
            if (_logHistoryExportProgressTimer is null)
            {
                _logHistoryExportProgressTimer = _dispatcherQueue.CreateTimer();
                _logHistoryExportProgressTimer.Interval = TimeSpan.FromMilliseconds(100);
                _logHistoryExportProgressTimer.Tick += OnLogHistoryExportProgressTick;
            }
            _logHistoryExportProgressTimer.Start();
            _logHistoryExportTask = _logHistoryExporter.ExportAsync(path, snapshot, cancellationToken, LogHistoryExport);
            await _logHistoryExportTask;
            // Successful return means the file has already been committed. A late
            // Cancel must not claim that an existing file was preserved at this point.
            RecordLogFileActionSuccess(UiText.Format("LogHistorySaved", "Exported {0:N0} log records: {1}", snapshot.Count, path));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!_logHistoryExportCancellation.IsCancellationRequested)
                RecordLogFileActionSuccess(UiText.Get("LogHistoryCanceled", "Log export was canceled."));
        }
        catch (Exception ex)
        {
            RecordLogFileActionError(UiText.Format("LogHistorySaveFailed", "Log export failed: {0}", ex.Message));
        }
        finally
        {
            _logHistoryExportProgressTimer?.Stop();
            LogHistoryExport.Finish();
            _logHistoryExportTask = null;
            _isLogHistoryExporting = false;
            OnPropertyChanged(nameof(CanExportLogHistory));
        }
    }

    private void OnLogHistoryExportProgressTick(DispatcherQueueTimer sender, object args) =>
        LogHistoryExport.RefreshProgress();

    private void OnLogHistoryPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(LogViewModel.TotalRetainedLineCount))
            OnPropertyChanged(nameof(CanExportLogHistory));
    }

    private void CancelLogHistoryExport()
    {
        _logHistoryExportCancellation.Cancel();
        OnPropertyChanged(nameof(CanExportLogHistory));
    }
}
