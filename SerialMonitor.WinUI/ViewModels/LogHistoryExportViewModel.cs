using Microsoft.UI.Xaml;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

public sealed class LogHistoryExportViewModel : ViewModelBase, IProgress<LogHistoryExportProgress>
{
    private CancellationTokenSource? _cancellation;
    private LogHistoryExportProgress? _pendingProgress;
    private LogHistoryExportProgress? _displayedProgress;
    private bool _isVisible;
    private bool _isCancelling;

    public LogHistoryExportViewModel()
    {
        CancelCommand = new AsyncRelayCommand(() =>
        {
            _cancellation?.Cancel();
            _isCancelling = true;
            NotifyStateChanged();
            return Task.CompletedTask;
        }, () => _isVisible && !_isCancelling);
    }

    public AsyncRelayCommand CancelCommand { get; }
    public Visibility Visibility => _isVisible ? Visibility.Visible : Visibility.Collapsed;
    public bool IsIndeterminate => _displayedProgress is null || _isCancelling;
    public int Percent => _displayedProgress?.Percent ?? 0;
    public string DetailText => _isCancelling
        ? UiText.Get("LogHistoryCanceling", "Canceling export...")
        : _displayedProgress is not { } progress
            ? UiText.Get("LogHistoryPreparing", "Preparing current logs...")
            : progress.WrittenRecords >= progress.TotalRecords && !progress.IsCompleted
                ? UiText.Get("LogHistoryFinalizing", "99% · Finishing log export...")
                : UiText.Format("LogHistoryProgress", "{0}% · {1:N0} / {2:N0} records",
                    progress.Percent, progress.WrittenRecords, progress.TotalRecords);

    internal CancellationToken Begin(CancellationToken lifetimeToken)
    {
        if (_cancellation is not null) throw new InvalidOperationException("A log export is already running.");
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        _displayedProgress = null;
        Volatile.Write(ref _pendingProgress, null);
        _isVisible = true;
        _isCancelling = false;
        NotifyStateChanged();
        return _cancellation.Token;
    }

    // The worker only replaces one immutable value. UI updates are polled in batches,
    // so a busy dispatcher cannot accumulate an unbounded queue of progress callbacks.
    public void Report(LogHistoryExportProgress value) => Volatile.Write(ref _pendingProgress, value);

    internal void RefreshProgress()
    {
        var latest = Volatile.Read(ref _pendingProgress);
        if (latest is null || latest == _displayedProgress) return;
        _displayedProgress = latest;
        NotifyStateChanged();
    }

    internal void Finish()
    {
        RefreshProgress();
        _isVisible = false;
        _cancellation?.Dispose();
        _cancellation = null;
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(Visibility));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(DetailText));
        CancelCommand.NotifyCanExecuteChanged();
    }
}
