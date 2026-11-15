using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI;

public sealed partial class MainWindow
{
    private readonly IInspectorLayoutStore _inspectorLayoutStore = new InspectorLayoutStore();
    private readonly CancellationTokenSource _inspectorLayoutLifetime = new();
    private Task? _inspectorLayoutLoadTask;
    private Task _inspectorLayoutSaveTask = Task.CompletedTask;
    private InspectorLayoutState? _pendingInspectorLayout;
    private long _inspectorLayoutRevision;

    private Task InitializeInspectorLayoutAsync() => _inspectorLayoutLoadTask ??= LoadInspectorLayoutAsync();

    private async Task LoadInspectorLayoutAsync()
    {
        var revision = _inspectorLayoutRevision;
        try
        {
            var state = await _inspectorLayoutStore.LoadAsync(_inspectorLayoutLifetime.Token);
            // A late disk read must never undo a gesture made during startup.
            if (revision != 0 || revision != _inspectorLayoutRevision || IsClosingOrClosed ||
                _activeDockPointerId is not null || _isInspectorResizeDragging) return;
            _inspectorDockPosition = state.Position;
            _lastExpandedInspectorHeight = state.BottomHeight ?? double.NaN;
            _lastExpandedInspectorWidth = state.SideWidth ?? double.NaN;
            _isInspectorCollapsed = state.IsCollapsed;
            ApplyInspectorLayout();
            QueueXtermFit();
        }
        catch (OperationCanceledException) when (_inspectorLayoutLifetime.IsCancellationRequested) { }
        catch (Exception ex) { ReportInspectorPersistenceError("InspectorLayout.Load", ex); }
    }

    private void QueueInspectorLayoutSave()
    {
        _inspectorLayoutRevision++;
        _pendingInspectorLayout = new InspectorLayoutState(_inspectorDockPosition,
            double.IsFinite(_lastExpandedInspectorHeight) ? _lastExpandedInspectorHeight : ExpandedInspectorExtent(InspectorDockPosition.Bottom),
            double.IsFinite(_lastExpandedInspectorWidth) ? _lastExpandedInspectorWidth : ExpandedInspectorExtent(InspectorDockPosition.Right),
            _isInspectorCollapsed);
        // A single writer and one replaceable pending snapshot bound memory and
        // prevent a slow older write from overwriting a newer drag/resize.
        if (_inspectorLayoutSaveTask.IsCompleted) _inspectorLayoutSaveTask = SavePendingInspectorLayoutAsync();
    }

    private async Task SavePendingInspectorLayoutAsync()
    {
        await InitializeInspectorLayoutAsync();
        while (_pendingInspectorLayout is { } state && !_inspectorLayoutLifetime.IsCancellationRequested)
        {
            _pendingInspectorLayout = null;
            try { await _inspectorLayoutStore.SaveAsync(state, _inspectorLayoutLifetime.Token); }
            catch (OperationCanceledException) when (_inspectorLayoutLifetime.IsCancellationRequested) { return; }
            catch (Exception ex) { ReportInspectorPersistenceError("InspectorLayout.Save", ex); }
        }
    }

    private async Task FlushInspectorLayoutAsync()
    {
        try { await _inspectorLayoutSaveTask.WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (Exception ex)
        {
            _inspectorLayoutLifetime.Cancel();
            ReportInspectorPersistenceError("InspectorLayout.Flush", ex);
        }
    }

    private void ReportInspectorPersistenceError(string source, Exception error)
    {
        RuntimeDiagnostics.RecordError(source, error);
        _viewModel.RecordInspectorTabLayoutError(UiText.Get("InspectorLayoutStorageError",
            "Panel placement could not be saved or restored. Changes apply to this session."));
    }
}
