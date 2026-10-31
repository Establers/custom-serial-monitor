using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IEventEvidenceStore _eventEvidenceStore;
    private readonly SemaphoreSlim _eventContextReadGate = new(1, 1);
    private readonly CancellationTokenSource _eventPresentationStop = new();
    private CancellationTokenSource? _eventContextReadCancellation;
    private Task? _eventContextReadTask;
    private long _eventContextReadGeneration;
    private bool _isEventContextLoading;
    private readonly List<long> _eventContextPageStarts = [0];
    private EventEvidencePage? _eventContextPage;
    private Task? _eventContextExportTask;
    private bool _isEventContextExporting;
    public AsyncRelayCommand PreviousEventContextPageCommand { get; }
    public AsyncRelayCommand NextEventContextPageCommand { get; }

    private EventEvidenceReference? SelectedEvidence => SelectedEvent is null ? null :
        _eventContextsById.GetValueOrDefault(SelectedEvent.Id) ?? SelectedEvent.Evidence;

    private void RefreshSelectedEventContextText()
    {
        _eventContextReadCancellation?.Cancel();
        _eventContextPageStarts.Clear(); _eventContextPageStarts.Add(0);
        _eventContextPage = null;
        if (SelectedEvent is null)
        {
            SelectedEventContextText = "Select an event.";
            SelectedEventContextStatusText = "Select an event.";
            NotifyContextPageCommands();
            return;
        }
        if (SelectedEvidence is null)
        {
            SelectedEventContextText = CreateMissingContextStatus();
            SelectedEventContextStatusText = CreateMissingContextStatus();
            NotifyContextPageCommands();
            return;
        }
        _eventContextReadTask = LoadEventContextPageAsync();
    }
    public Task RefreshCurrentEventContextAsync()
    {
        if (IsShuttingDown) return Task.CompletedTask;
        RefreshSelectedEventContextText();
        return _eventContextReadTask ?? Task.CompletedTask;
    }

    private Func<LogLine, string> CaptureContextFormatter()
    {
        var timestamp = ShowTimestampInLogView;
        var format = _currentUiSettings.TimestampDisplayFormat;
        return line =>
        {
            var content = line.ContentMode == LogRuleMatchMode.Hex && line.RawBytes is { } bytes
                ? HexFormatter.Format(bytes) : line.DisplayText;
            return timestamp ? $"[{line.FormatTimestamp(format)}] {line.DirectionText} {content}"
                : $"{line.DirectionText} {content}";
        };
    }

    private async Task LoadEventContextPageAsync()
    {
        var evidence = SelectedEvidence;
        if (evidence is null) return;
        _eventContextReadCancellation?.Cancel();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_eventPresentationStop.Token);
        _eventContextReadCancellation = stop;
        var token = stop.Token;
        var generation = ++_eventContextReadGeneration;
        var offset = _eventContextPageStarts[^1];
        var format = CaptureContextFormatter();
        var expectedVersion = _eventContextPage?.Version;
        _isEventContextLoading = true;
        SelectedEventContextText = "Loading event context...";
        NotifyContextPageCommands();
        try
        {
            await _eventContextReadGate.WaitAsync(token);
            EventEvidencePage page;
            try { page = await Task.Run(() => _eventEvidenceStore.ReadPageAsync(evidence, offset, format, token, expectedVersion), token); }
            finally { _eventContextReadGate.Release(); }
            if (token.IsCancellationRequested || generation != _eventContextReadGeneration || SelectedEvent?.Id != evidence.EventId) return;
            _eventContextPage = page;
            if (page.StartOffset != offset) { _eventContextPageStarts.Clear(); _eventContextPageStarts.Add(0); }
            SelectedEventContextText = page.Text;
            SelectedEventContextStatusText = $"before {evidence.BeforeCount:N0} / after {evidence.AfterCount:N0} | page {_eventContextPageStarts.Count}" +
                (page.HasMore ? " | more" : "") + (page.IsComplete ? "" : " | context pending");
            Interlocked.Increment(ref _contextRefreshCount);
            _lastContextRefreshError = string.Empty;
            OnPropertyChanged(nameof(ContextRefreshCount));
            OnPropertyChanged(nameof(LastContextRefreshError));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation != _eventContextReadGeneration) return;
            SelectedEventContextText = "Event context unavailable: " + ex.Message;
            SelectedEventContextStatusText = "Context unavailable";
            Interlocked.Increment(ref _contextRefreshErrorCount);
            _lastContextRefreshError = ex.Message;
            RecordEventContextUiError("Event context display failed: " + ex.Message);
        }
        finally
        {
            if (generation == _eventContextReadGeneration)
            {
                _isEventContextLoading = false;
                _eventContextReadCancellation = null;
                NotifyContextPageCommands();
                OnPropertyChanged(nameof(SelectedEventContextLineCount));
                OnPropertyChanged(nameof(SelectedEventContextHeaderText));
            }
        }
    }

    private Task PreviousEventContextPageAsync()
    {
        if (_eventContextPageStarts.Count <= 1) return Task.CompletedTask;
        _eventContextPageStarts.RemoveAt(_eventContextPageStarts.Count - 1);
        return _eventContextReadTask = LoadEventContextPageAsync();
    }
    private Task NextEventContextPageAsync()
    {
        if (_eventContextPage?.HasMore != true) return Task.CompletedTask;
        _eventContextPageStarts.Add(_eventContextPage.NextOffset);
        return _eventContextReadTask = LoadEventContextPageAsync();
    }
    private void NotifyContextPageCommands()
    {
        PreviousEventContextPageCommand?.NotifyCanExecuteChanged();
        NextEventContextPageCommand?.NotifyCanExecuteChanged();
        CopyEventContextCommand?.NotifyCanExecuteChanged();
    }
    public async Task ExportSelectedEventContextAsync(Func<string, Task<string?>> selectPath)
    {
        var reference = SelectedEvidence;
        if (reference is null || IsShuttingDown || _isEventContextExporting) return;
        _isEventContextExporting = true;
        var formatter = CaptureContextFormatter();
        try
        {
            var path = await selectPath($"event-{reference.EventId:N}.log");
            if (string.IsNullOrWhiteSpace(path)) return;
            if (_fileLogWriter.IsRunning && !string.IsNullOrEmpty(_fileLogWriter.CurrentLogFilePath) &&
                string.Equals(Path.GetFullPath(path), Path.GetFullPath(_fileLogWriter.CurrentLogFilePath), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Cannot overwrite the active serial log file.");
            _eventContextExportTask = Task.Run(() => _eventEvidenceStore.ExportAsync(reference, path, formatter, _eventPresentationStop.Token));
            await _eventContextExportTask;
            SetStatus("Full event context exported.");
        }
        catch (OperationCanceledException) when (_eventPresentationStop.IsCancellationRequested) { }
        catch (Exception ex) { RecordEventContextUiError("Event context export failed: " + ex.Message); }
        finally { _isEventContextExporting = false; }
    }
    private static async Task RecordEventExportCompletionAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { RuntimeDiagnostics.RecordError("Event context export shutdown", ex); }
    }
}
