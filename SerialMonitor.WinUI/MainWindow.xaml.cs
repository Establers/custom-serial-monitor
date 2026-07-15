using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;

namespace SerialMonitor.WinUI;

public sealed partial class MainWindow : Window
{
    private const string LineEndingHelpText = @"Global = use the TX ending selected in the main TX area; None = send without a line ending; CR = \r; LF = \n; CRLF = \r\n.";
    private const int LogRestoreOverlayLineThreshold = 1_000;
    private const int XtermFullRenderTransportMaxChars = 64 * 1024;
    private const int XtermLiveAppendMaxLines = 2_000;
    private const int XtermLiveAppendMaxChars = 256 * 1024;
    private const int XtermBackpressureHighLines = 5_000;
    private const int XtermBackpressureLowLines = 1_000;
    private const long XtermBackpressureHighChars = 8L * 1024 * 1024;
    private const long XtermBackpressureLowChars = 2L * 1024 * 1024;
    private const long XtermSuspendedBatchMaxChars = 32L * 1024 * 1024;
    private static readonly TimeSpan XtermLiveAppendAckTimeout = TimeSpan.FromSeconds(30);
    private static readonly string BundledCuteBackgroundPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "FunBackgrounds", "default_cute_bg.jpg");
    private static readonly string AppIconPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon", "SerialMonitor.ico");

    private readonly MainViewModel _viewModel;
    private readonly WindowsTrayNotifier _trayNotifier = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _eventPopupTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _trayIconTimer;
    private readonly SemaphoreSlim _xtermAppendGate = new(1, 1);
    private readonly object _xtermLiveAppendQueueGate = new();
    private readonly LinkedList<(LogTextBatch Batch, long Generation)> _xtermLiveAppendQueue = new();
    private readonly object _xtermAppendAckGate = new();
    private readonly Dictionary<long, TaskCompletionSource<bool>> _xtermAppendAcknowledgements = new();
    private bool _xtermLiveAppendPumpRunning;
    private bool _xtermAppendRecoveryPending;
    private bool _xtermAppendRecoveryRetryQueued;
    private long _nextXtermAppendRequestId;
    private int _pendingLiveXtermLines;
    private bool _isXtermReady;
    private bool _eventAutoScrollQueued;
    private bool _isPointerOverEventList;
    private bool _xtermFitQueued;
    private bool _rulesTableViewportResizeQueued;
    private bool _closeAllowed;
    private bool _closeCleanupStarted;
    private bool _isWindowMinimized;
    private bool _isVisualAppendSuspendedForMinimize;
    private bool _xtermNeedsFullRerenderAfterRestore;
    private bool _restoreRerenderQueued;
    private bool _restoreDeltaCatchUpQueued;
    private int _restoreRerenderRetryCount;
    private string _pendingXtermFullRerenderReason = "full re-render";
    private long _xtermSyncedThroughDisplayedLineCount;
    private long _xtermRenderGeneration;
    private readonly object _suspendedXtermBatchGate = new();
    private readonly Queue<LogTextBatch> _suspendedXtermBatches = new();
    private int _suspendedXtermLineCount;
    private long _suspendedXtermCharacterCount;
    private bool _suspendedXtermClearRequested;
    private readonly object _fullXtermRerenderGate = new();
    private bool _fullXtermRerenderQueued;
    private bool _fullXtermRerenderRunning;
    private bool _fullXtermRerenderRequestedWhileRunning;
    private bool _queuedFullXtermRerenderIsRestore;
    private string _queuedFullXtermRerenderReason = "full re-render";
    private bool _xtermFullRerenderDeferredForBackpressure;
    private string _deferredXtermFullRerenderReason = "full re-render";
    private long _fullXtermRerenderGeneration;
    private bool? _lastAppliedCuteBackgroundEnabled;
    private string? _lastAppliedCuteBackgroundPath;
    private string? _lastAppliedCuteBackgroundCustomPath;
    private string? _lastAppliedCuteBackgroundSource;
    private double? _lastAppliedCuteBackgroundOpacity;
    private string? _cachedCuteBackgroundPath;
    private BitmapImage? _cachedCuteBackgroundImage;
    private bool _isInspectorCollapsed;

    private readonly record struct XtermAppendChunk(string Text, int LineCount);
    private sealed record XtermScrollState(bool Ok, int ViewportY, int BaseY, int Rows, bool AtBottom);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);

    private bool IsClosingOrClosed => _closeCleanupStarted || _closeAllowed;

    public MainWindow()
    {
        InitializeComponent();
        ApplyAppIcon();

        var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _eventPopupTimer = dispatcherQueue.CreateTimer();
        _eventPopupTimer.Interval = TimeSpan.FromSeconds(8);
        _eventPopupTimer.Tick += OnEventPopupTimerTick;
        _trayIconTimer = dispatcherQueue.CreateTimer();
        _trayIconTimer.Interval = TimeSpan.FromSeconds(12);
        _trayIconTimer.Tick += OnTrayIconTimerTick;
        _viewModel = new MainViewModel(
            new SerialService(),
            new LogPipeline(new EncodingDecoder(), new LineParser()),
            new FileLogWriter(),
            new EventDetector(),
            new SerialBridgeService(),
            new BridgeLogProcessor(),
            new ProfileService(),
            dispatcherQueue);

        Root.DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Events.Events.CollectionChanged += OnDetectedEventsCollectionChanged;
        _viewModel.Log.TextBatchAppended += OnLogTextBatchAppended;
        _viewModel.Log.TextCleared += OnLogTextCleared;
        _viewModel.Log.TextRebuilt += OnLogTextRebuilt;
        _viewModel.XtermSearchRequested += OnXtermSearchRequested;
        _viewModel.ConnectFailureDialogRequested += OnConnectFailureDialogRequested;
        _viewModel.EventNotificationRequested += OnEventNotificationRequested;
        _viewModel.ViewPauseDrainRequested += DrainXtermForViewPauseAsync;
        AppWindow.Closing += OnAppWindowClosing;
        AppWindow.Changed += OnAppWindowChanged;
        Activated += OnWindowActivated;
        Closed += OnClosed;
        UpdateCuteBackgroundImage();
        _ = InitializeXtermWebViewAsync();

        AppWindow.Resize(new SizeInt32(1200, 800));
    }

    private void ApplyAppIcon()
    {
        try
        {
            if (File.Exists(AppIconPath))
            {
                AppWindow.SetIcon(AppIconPath);
            }
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("MainWindow.ApplyAppIcon", ex);
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeAllowed)
        {
            return;
        }

        args.Cancel = true;
        if (_closeCleanupStarted)
        {
            return;
        }

        _closeCleanupStarted = true;
        _ = ShutdownAndCloseAsync();
    }

    private async Task ShutdownAndCloseAsync()
    {
        try
        {
            await _viewModel.ShutdownAsync(TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("MainWindow.ShutdownAndCloseAsync", ex);
        }
        finally
        {
            _closeAllowed = true;
            Close();
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        UpdateWindowMinimizedState(IsAppWindowCurrentlyMinimized());
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        UpdateWindowMinimizedState(IsAppWindowCurrentlyMinimized());
        if (!_isWindowMinimized &&
            !_xtermNeedsFullRerenderAfterRestore &&
            !HasSuspendedXtermWork())
        {
            _viewModel.RecordWindowActivationRerenderSuppressed();
        }
    }

    private bool IsAppWindowCurrentlyMinimized()
    {
        return AppWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized;
    }

    private void UpdateWindowMinimizedState(bool isMinimized)
    {
        if (_isWindowMinimized == isMinimized)
        {
            return;
        }

        _isWindowMinimized = isMinimized;
        _isVisualAppendSuspendedForMinimize = isMinimized;
        _viewModel.RecordWindowMinimizeState(isMinimized);
        _viewModel.SetVisualAppendSuspendedForMinimize(isMinimized);

        if (isMinimized)
        {
            _restoreRerenderRetryCount = 0;
            _viewModel.RecordRenderedSequenceState(
                _xtermSyncedThroughDisplayedLineCount,
                _viewModel.Log.DisplayedLineCount - _xtermSyncedThroughDisplayedLineCount);
            return;
        }

        if (_xtermNeedsFullRerenderAfterRestore)
        {
            QueueXtermFullRerenderAfterRestore(_pendingXtermFullRerenderReason);
            return;
        }

        QueueXtermDeltaCatchUpAfterRestore("window restored");
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        CancelPendingXtermAppendAcknowledgements();
        AppWindow.Closing -= OnAppWindowClosing;
        AppWindow.Changed -= OnAppWindowChanged;
        Activated -= OnWindowActivated;
        _viewModel.Log.TextBatchAppended -= OnLogTextBatchAppended;
        _viewModel.Log.TextCleared -= OnLogTextCleared;
        _viewModel.Log.TextRebuilt -= OnLogTextRebuilt;
        _viewModel.XtermSearchRequested -= OnXtermSearchRequested;
        _viewModel.ConnectFailureDialogRequested -= OnConnectFailureDialogRequested;
        _viewModel.EventNotificationRequested -= OnEventNotificationRequested;
        _viewModel.ViewPauseDrainRequested -= DrainXtermForViewPauseAsync;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Events.Events.CollectionChanged -= OnDetectedEventsCollectionChanged;
        XtermLogWebView.NavigationCompleted -= OnXtermNavigationCompleted;
        if (XtermLogWebView.CoreWebView2 is not null)
        {
            XtermLogWebView.CoreWebView2.WebMessageReceived -= OnXtermWebMessageReceived;
        }

        _eventPopupTimer.Stop();
        _eventPopupTimer.Tick -= OnEventPopupTimerTick;
        _trayIconTimer.Stop();
        _trayIconTimer.Tick -= OnTrayIconTimerTick;
        _trayNotifier.Dispose();

        try
        {
            await _viewModel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("MainWindow.OnClosed.Dispose", ex);
        }
    }

    private void OnEventNotificationRequested(object? sender, EventNotificationRequest request)
    {
        if (IsClosingOrClosed)
        {
            return;
        }

        if (request.PlaySound)
        {
            MessageBeep(0x00000030);
        }

        if (request.ShowTray)
        {
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (_trayNotifier.Show(windowHandle, request.Title, request.Message))
            {
                _trayIconTimer.Stop();
                _trayIconTimer.Start();
            }
        }

        if (request.ShowPopup)
        {
            EventNotificationInfoBar.Title = request.Title;
            EventNotificationInfoBar.Message = request.Message;
            EventNotificationInfoBar.IsOpen = true;
            _eventPopupTimer.Stop();
            _eventPopupTimer.Start();
        }
    }

    private void OnEventPopupTimerTick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        EventNotificationInfoBar.IsOpen = false;
    }

    private void OnTrayIconTimerTick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        _trayNotifier.Hide();
    }

    private void CancelPendingXtermAppendAcknowledgements()
    {
        TaskCompletionSource<bool>[] pending;
        lock (_xtermAppendAckGate)
        {
            pending = _xtermAppendAcknowledgements.Values.ToArray();
            _xtermAppendAcknowledgements.Clear();
        }

        foreach (var acknowledgement in pending)
        {
            acknowledgement.TrySetCanceled();
        }
    }

    private async void ConnectionButton_Click(object sender, RoutedEventArgs args)
    {
        if (!_viewModel.IsConnected)
        {
            if (_viewModel.ConnectCommand.CanExecute(null))
            {
                _viewModel.ConnectCommand.Execute(null);
            }

            return;
        }

        if (!_viewModel.ConfirmBeforeDisconnect)
        {
            _viewModel.RecordDisconnectConfirmationResult("skipped");
            ExecuteDisconnectCommand();
            return;
        }

        bool confirmed;
        try
        {
            confirmed = await ShowDisconnectConfirmationAsync();
        }
        catch (Exception ex)
        {
            _viewModel.RecordDisconnectConfirmationError($"Disconnect confirmation failed: {ex.Message}");
            return;
        }

        if (!confirmed)
        {
            return;
        }

        ExecuteDisconnectCommand();
    }

    private void Root_Loaded(object sender, RoutedEventArgs args)
    {
        ApplyInspectorLayout();
        UpdateToolbarScrollButtons(ConnectionToolbarScrollViewer);
        UpdateToolbarScrollButtons(LogToolbarScrollViewer);
        UpdateToolbarScrollButtons(TxToolbarScrollViewer);
        UpdateToolbarScrollButtons(QuickToolbarScrollViewer);
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        ApplyInspectorLayout();
        QueueRulesTableViewportResize();
    }

    private void InspectorCollapseButton_Click(object sender, RoutedEventArgs args)
    {
        _isInspectorCollapsed = !_isInspectorCollapsed;
        ApplyInspectorLayout();
        QueueXtermFit();
    }

    private void ToolbarScrollBackButton_Click(object sender, RoutedEventArgs args)
    {
        ScrollToolbar(sender as Button, -1);
    }

    private void ToolbarScrollForwardButton_Click(object sender, RoutedEventArgs args)
    {
        ScrollToolbar(sender as Button, 1);
    }

    private void HorizontalToolbarScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            UpdateToolbarScrollButtons(scrollViewer);
        }
    }

    private void HorizontalToolbarScrollViewer_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            UpdateToolbarScrollButtons(scrollViewer);
        }
    }

    private void QuickCommandPanel_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateToolbarScrollButtons(QuickToolbarScrollViewer);
    }

    private void ScrollToolbar(Button? sourceButton, int direction)
    {
        var scrollViewer = sourceButton switch
        {
            _ when ReferenceEquals(sourceButton, ConnectionToolbarScrollBackButton) ||
                   ReferenceEquals(sourceButton, ConnectionToolbarScrollForwardButton) => ConnectionToolbarScrollViewer,
            _ when ReferenceEquals(sourceButton, LogToolbarScrollBackButton) ||
                   ReferenceEquals(sourceButton, LogToolbarScrollForwardButton) => LogToolbarScrollViewer,
            _ when ReferenceEquals(sourceButton, TxToolbarScrollBackButton) ||
                   ReferenceEquals(sourceButton, TxToolbarScrollForwardButton) => TxToolbarScrollViewer,
            _ when ReferenceEquals(sourceButton, QuickToolbarScrollBackButton) ||
                   ReferenceEquals(sourceButton, QuickToolbarScrollForwardButton) => QuickToolbarScrollViewer,
            _ => null
        };

        if (scrollViewer is null)
        {
            return;
        }

        var step = Math.Max(120, scrollViewer.ViewportWidth * 0.65);
        var targetOffset = Math.Clamp(
            scrollViewer.HorizontalOffset + (direction * step),
            0,
            scrollViewer.ScrollableWidth);
        scrollViewer.ChangeView(targetOffset, null, null, disableAnimation: false);
    }

    private void UpdateToolbarScrollButtons(ScrollViewer scrollViewer)
    {
        Button backButton;
        Button forwardButton;
        if (ReferenceEquals(scrollViewer, ConnectionToolbarScrollViewer))
        {
            backButton = ConnectionToolbarScrollBackButton;
            forwardButton = ConnectionToolbarScrollForwardButton;
        }
        else if (ReferenceEquals(scrollViewer, LogToolbarScrollViewer))
        {
            backButton = LogToolbarScrollBackButton;
            forwardButton = LogToolbarScrollForwardButton;
        }
        else if (ReferenceEquals(scrollViewer, TxToolbarScrollViewer))
        {
            backButton = TxToolbarScrollBackButton;
            forwardButton = TxToolbarScrollForwardButton;
        }
        else if (ReferenceEquals(scrollViewer, QuickToolbarScrollViewer))
        {
            backButton = QuickToolbarScrollBackButton;
            forwardButton = QuickToolbarScrollForwardButton;
        }
        else
        {
            return;
        }

        var reservedButtonWidth = backButton.Visibility == Visibility.Visible
            ? backButton.ActualWidth + forwardButton.ActualWidth
            : 0;
        var hasOverflow = scrollViewer.ScrollableWidth > reservedButtonWidth + 0.5;
        var visibility = hasOverflow ? Visibility.Visible : Visibility.Collapsed;
        backButton.Visibility = visibility;
        forwardButton.Visibility = visibility;
        if (!hasOverflow && scrollViewer.HorizontalOffset > 0.5)
        {
            scrollViewer.ChangeView(0, null, null, disableAnimation: true);
        }

        backButton.IsEnabled = hasOverflow && scrollViewer.HorizontalOffset > 0.5;
        forwardButton.IsEnabled = hasOverflow &&
                                  scrollViewer.HorizontalOffset < scrollViewer.ScrollableWidth - 0.5;
    }

    private void ApplyInspectorLayout()
    {
        InspectorTabView.Visibility = _isInspectorCollapsed
            ? Visibility.Collapsed
            : Visibility.Visible;
        InspectorPanel.MinHeight = _isInspectorCollapsed ? 0 : 160;
        InspectorCollapseGlyph.Glyph = _isInspectorCollapsed ? "\uE70E" : "\uE70D";
        ToolTipService.SetToolTip(
            InspectorCollapseButton,
            _isInspectorCollapsed ? "Expand inspector menu" : "Collapse inspector menu");

        ContentLogRow.Height = new GridLength(_isInspectorCollapsed ? 1 : 2, GridUnitType.Star);
        ContentInspectorRow.Height = _isInspectorCollapsed
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        LogColumn.Width = new GridLength(1, GridUnitType.Star);
        InspectorColumn.Width = new GridLength(0);
        Grid.SetRow(InspectorPanel, 1);
        Grid.SetColumn(InspectorPanel, 0);
        Grid.SetRow(InspectorCollapseButton, _isInspectorCollapsed ? 0 : 1);
        Grid.SetColumn(InspectorCollapseButton, 0);
        InspectorCollapseButton.VerticalAlignment = _isInspectorCollapsed
            ? VerticalAlignment.Bottom
            : VerticalAlignment.Top;
        InspectorCollapseButton.Margin = _isInspectorCollapsed
            ? new Thickness(0, 0, 2, 2)
            : new Thickness(0, 3, 2, 0);
    }

    private void ExecuteDisconnectCommand()
    {
        if (_viewModel.DisconnectCommand.CanExecute(null))
        {
            _viewModel.DisconnectCommand.Execute(null);
        }
    }

    private async Task<bool> ShowDisconnectConfirmationAsync()
    {
        _viewModel.RecordDisconnectConfirmationResult("shown");

        var dontAskAgainBox = new CheckBox
        {
            Content = "Don't ask again",
            IsChecked = false
        };
        ToolTipService.SetToolTip(dontAskAgainBox, "Turn off manual disconnect confirmation.");

        var panel = CreateDialogPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "The serial connection is currently active. Disconnect now?",
            TextWrapping = TextWrapping.WrapWholeWords
        });
        panel.Children.Add(dontAskAgainBox);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Disconnect serial port?",
            Content = panel,
            PrimaryButtonText = "Disconnect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            _viewModel.RecordDisconnectConfirmationResult("canceled");
            return false;
        }

        if (dontAskAgainBox.IsChecked == true)
        {
            _viewModel.ConfirmBeforeDisconnect = false;
        }

        _viewModel.RecordDisconnectConfirmationResult("confirmed");
        return true;
    }

    private async void OnConnectFailureDialogRequested(object? sender, ConnectFailureDialogRequest request)
    {
        try
        {
            await ShowConnectFailureDialogAsync(request);
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("MainWindow.OnConnectFailureDialogRequested", ex);
        }
    }

    private async Task ShowConnectFailureDialogAsync(ConnectFailureDialogRequest request)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Failed to connect",
            Content = new TextBlock
            {
                Text = request.Message,
                TextWrapping = TextWrapping.WrapWholeWords,
                MaxWidth = 420
            },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }

    private void OnLogTextBatchAppended(object? sender, LogTextBatch batch)
    {
        if (IsClosingOrClosed)
        {
            return;
        }

        if (IsXtermVisualAppendSuspended())
        {
            EnqueueSuspendedXtermBatch(batch);
            return;
        }

        if (!DispatcherQueue.HasThreadAccess)
        {
            var generation = Interlocked.Read(ref _xtermRenderGeneration);
            DispatcherQueue.TryEnqueue(() => EnqueueLiveXtermBatch(batch, generation));
            return;
        }

        EnqueueLiveXtermBatch(batch, Interlocked.Read(ref _xtermRenderGeneration));
    }

    private void EnqueueLiveXtermBatch(LogTextBatch batch, long generation)
    {
        _viewModel.RecordXtermAppendQueued(batch.AppendedText.Length);
        Interlocked.Add(ref _pendingLiveXtermLines, batch.LineCount);
        UpdateXtermAppendBackpressure();
        var startPump = false;
        lock (_xtermLiveAppendQueueGate)
        {
            _xtermLiveAppendQueue.AddLast((batch, generation));
            if (!_xtermLiveAppendPumpRunning && !_xtermAppendRecoveryPending)
            {
                _xtermLiveAppendPumpRunning = true;
                startPump = true;
            }
        }

        if (startPump)
        {
            _ = RunLiveXtermAppendPumpAsync();
        }
    }

    private async Task RunLiveXtermAppendPumpAsync()
    {
        try
        {
            while (!IsClosingOrClosed)
            {
                var queuedBatch = DequeueMergedLiveXtermBatch();
                if (queuedBatch is null)
                {
                    return;
                }

                try
                {
                    var appendCompleted = await AppendLogBatchToXtermAsync(
                        queuedBatch.Value.Batch,
                        queuedBatch.Value.Generation);
                    if (!appendCompleted)
                    {
                        BeginXtermAppendRecovery(queuedBatch.Value);
                        return;
                    }
                }
                finally
                {
                    if (!_xtermAppendRecoveryPending)
                    {
                        CompleteLiveXtermBatch(queuedBatch.Value.Batch);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _viewModel.RecordXtermAppendError($"xterm append pump failed: {ex.Message}");
            MarkXtermFullRerenderNeeded("xterm append pump failed");
        }
        finally
        {
            var restartPump = false;
            lock (_xtermLiveAppendQueueGate)
            {
                _xtermLiveAppendPumpRunning = false;
                if (!IsClosingOrClosed &&
                    !_xtermAppendRecoveryPending &&
                    _xtermLiveAppendQueue.Count > 0)
                {
                    _xtermLiveAppendPumpRunning = true;
                    restartPump = true;
                }
            }

            if (restartPump)
            {
                _ = RunLiveXtermAppendPumpAsync();
            }
        }
    }

    private (LogTextBatch Batch, long Generation)? DequeueMergedLiveXtermBatch()
    {
        lock (_xtermLiveAppendQueueGate)
        {
            if (_xtermLiveAppendQueue.Count == 0)
            {
                return null;
            }

            var firstNode = _xtermLiveAppendQueue.First!;
            var first = firstNode.Value;
            _xtermLiveAppendQueue.RemoveFirst();
            var builder = new StringBuilder(first.Batch.AppendedText);
            var lineCount = first.Batch.LineCount;
            var trimCharacterCount = first.Batch.TrimCharacterCount;
            var endDisplayedLineCount = first.Batch.EndDisplayedLineCount;

            while (_xtermLiveAppendQueue.Count > 0)
            {
                var next = _xtermLiveAppendQueue.First!.Value;
                if (next.Generation != first.Generation ||
                    lineCount + next.Batch.LineCount > XtermLiveAppendMaxLines ||
                    builder.Length + next.Batch.AppendedText.Length > XtermLiveAppendMaxChars)
                {
                    break;
                }

                _xtermLiveAppendQueue.RemoveFirst();
                builder.Append(next.Batch.AppendedText);
                lineCount += next.Batch.LineCount;
                trimCharacterCount += next.Batch.TrimCharacterCount;
                endDisplayedLineCount = next.Batch.EndDisplayedLineCount;
            }

            return (
                new LogTextBatch(builder.ToString(), trimCharacterCount, lineCount, endDisplayedLineCount),
                first.Generation);
        }
    }

    private void BeginXtermAppendRecovery((LogTextBatch Batch, long Generation) failedBatch)
    {
        lock (_xtermLiveAppendQueueGate)
        {
            _xtermAppendRecoveryPending = true;
            _xtermLiveAppendQueue.AddFirst(failedBatch);
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            _viewModel.SetXtermAppendBackpressure(true);
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => _viewModel.SetXtermAppendBackpressure(true));
        }
        _viewModel.RecordXtermAppendError(
            "xterm append acknowledgement failed; rebuilding from the retained visible buffer.");
        QueueFullXtermRerender("xterm append acknowledgement recovery", debounce: false);
        QueueXtermAppendRecoveryRetry();
    }

    private void CompleteLiveXtermBatchesCoveredBySnapshot(long displayedLineCount)
    {
        var completed = new List<LogTextBatch>();
        var startPump = false;
        lock (_xtermLiveAppendQueueGate)
        {
            while (_xtermLiveAppendQueue.First is not null &&
                _xtermLiveAppendQueue.First.Value.Batch.EndDisplayedLineCount <= displayedLineCount)
            {
                completed.Add(_xtermLiveAppendQueue.First.Value.Batch);
                _xtermLiveAppendQueue.RemoveFirst();
            }

            _xtermAppendRecoveryPending = false;
            _xtermAppendRecoveryRetryQueued = false;
            if (!_xtermLiveAppendPumpRunning &&
                !IsClosingOrClosed &&
                _xtermLiveAppendQueue.Count > 0)
            {
                _xtermLiveAppendPumpRunning = true;
                startPump = true;
            }
        }

        foreach (var batch in completed)
        {
            CompleteLiveXtermBatch(batch);
        }

        if (startPump)
        {
            _ = RunLiveXtermAppendPumpAsync();
        }
    }

    private void QueueXtermAppendRecoveryRetry()
    {
        lock (_xtermLiveAppendQueueGate)
        {
            if (!_xtermAppendRecoveryPending ||
                _xtermAppendRecoveryRetryQueued ||
                IsClosingOrClosed)
            {
                return;
            }

            _xtermAppendRecoveryRetryQueued = true;
        }

        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(1_000);
            lock (_xtermLiveAppendQueueGate)
            {
                _xtermAppendRecoveryRetryQueued = false;
                if (!_xtermAppendRecoveryPending || IsClosingOrClosed)
                {
                    return;
                }
            }

            QueueFullXtermRerender("xterm append acknowledgement recovery retry", debounce: false);
        });
    }

    private void OnLogTextCleared(object? sender, EventArgs args)
    {
        if (IsClosingOrClosed)
        {
            return;
        }

        if (IsXtermVisualAppendSuspended())
        {
            MarkXtermClearNeededAfterRestore();
            return;
        }

        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => _ = ClearXtermAsync());
            return;
        }

        _ = ClearXtermAsync();
    }

    private void OnLogTextRebuilt(object? sender, EventArgs args)
    {
        if (IsClosingOrClosed)
        {
            return;
        }

        if (IsXtermVisualAppendSuspended())
        {
            var rebuildReason = _viewModel.LastVisibleLogRebuildReason;
            if (ShouldForceFullRerenderWhileMinimized(rebuildReason))
            {
                MarkXtermFullRerenderNeeded(rebuildReason);
            }
            else
            {
                _viewModel.RecordRestoreFullRerenderSuppressed($"minimized rebuild ignored: {rebuildReason}");
            }

            return;
        }

        var reason = _viewModel.LastVisibleLogRebuildReason;
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => QueueOrDeferVisibleLogRerender(reason));
            return;
        }

        QueueOrDeferVisibleLogRerender(reason);
    }

    private void QueueOrDeferVisibleLogRerender(string reason)
    {
        if (_viewModel.IsXtermAppendBackpressureActive &&
            ShouldDeferFullRerenderForBackpressure(reason))
        {
            DeferFullXtermRerenderForBackpressure(reason);
            return;
        }

        QueueFullXtermRerender(reason);
    }

    private static bool ShouldDeferFullRerenderForBackpressure(string? reason)
    {
        return !string.IsNullOrWhiteSpace(reason) &&
            (reason.Contains("rule", StringComparison.OrdinalIgnoreCase) ||
             reason.Contains("filter", StringComparison.OrdinalIgnoreCase));
    }

    private void DeferFullXtermRerenderForBackpressure(string reason)
    {
        _xtermFullRerenderDeferredForBackpressure = true;
        _deferredXtermFullRerenderReason = MergeFullXtermRerenderReason(
            _deferredXtermFullRerenderReason,
            NormalizeFullXtermRerenderReason(reason));
        _viewModel.RecordXtermBackpressureFullRerenderDeferred();
    }

    private void QueueDeferredFullXtermRerenderAfterBackpressure()
    {
        if (!_xtermFullRerenderDeferredForBackpressure ||
            _viewModel.IsXtermAppendBackpressureActive ||
            IsClosingOrClosed)
        {
            return;
        }

        var reason = _deferredXtermFullRerenderReason;
        _xtermFullRerenderDeferredForBackpressure = false;
        _deferredXtermFullRerenderReason = "full re-render";
        QueueFullXtermRerender($"deferred after xterm backlog: {reason}");
    }

    private void OnXtermSearchRequested(object? sender, XtermSearchRequest request)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => _ = SearchXtermAsync(request));
            return;
        }

        _ = SearchXtermAsync(request);
    }

    private bool IsXtermVisualAppendSuspended()
    {
        return _isVisualAppendSuspendedForMinimize;
    }

    private void MarkXtermFullRerenderNeeded(string reason, int coalescedLineCount = 0, int coalescedCharacterCount = 0)
    {
        _xtermNeedsFullRerenderAfterRestore = true;
        _pendingXtermFullRerenderReason = string.IsNullOrWhiteSpace(reason)
            ? "full re-render"
            : reason.Trim();
        _viewModel.SetXtermNeedsFullRerenderAfterRestore(true);
        _viewModel.RecordMinimizedVisualAppendCoalesced(coalescedLineCount, coalescedCharacterCount);
        ClearSuspendedXtermBatches();
    }

    private void MarkXtermClearNeededAfterRestore()
    {
        lock (_suspendedXtermBatchGate)
        {
            _suspendedXtermBatches.Clear();
            _suspendedXtermLineCount = 0;
            _suspendedXtermCharacterCount = 0;
            _suspendedXtermClearRequested = true;
        }
        _viewModel.RecordSuspendedXtermQueueState(0, 0);

        _xtermSyncedThroughDisplayedLineCount = _viewModel.Log.DisplayedLineCount;
        _viewModel.RecordRenderedSequenceState(
            _xtermSyncedThroughDisplayedLineCount,
            pendingDeltaLineCount: 0);
    }

    private void EnqueueSuspendedXtermBatch(LogTextBatch batch)
    {
        if (batch.LineCount <= 0 || string.IsNullOrEmpty(batch.AppendedText))
        {
            return;
        }

        if (_xtermNeedsFullRerenderAfterRestore)
        {
            var pendingAfterFullRender = Math.Max(0, batch.EndDisplayedLineCount - _xtermSyncedThroughDisplayedLineCount);
            _viewModel.RecordMinimizedVisualAppendCoalesced(batch.LineCount, batch.AppendedText.Length);
            _viewModel.RecordRenderedSequenceState(_xtermSyncedThroughDisplayedLineCount, pendingAfterFullRender);
            return;
        }

        var collapseToFullRerender = false;
        var pendingLines = 0;
        long pendingCharacters = 0;
        lock (_suspendedXtermBatchGate)
        {
            var nextLineCount = (long)_suspendedXtermLineCount + batch.LineCount;
            var nextCharacterCount = _suspendedXtermCharacterCount + batch.AppendedText.Length;
            if (nextLineCount > _viewModel.MaxVisibleLogLines ||
                nextCharacterCount > XtermSuspendedBatchMaxChars)
            {
                _suspendedXtermBatches.Clear();
                _suspendedXtermLineCount = 0;
                _suspendedXtermCharacterCount = 0;
                collapseToFullRerender = true;
            }
            else
            {
                _suspendedXtermBatches.Enqueue(batch);
                _suspendedXtermLineCount = (int)nextLineCount;
                _suspendedXtermCharacterCount = nextCharacterCount;
                pendingLines = _suspendedXtermLineCount;
                pendingCharacters = _suspendedXtermCharacterCount;
            }
        }

        if (collapseToFullRerender)
        {
            const string reason = "minimized xterm delta exceeded visible-buffer bound";
            _viewModel.RecordSuspendedXtermQueueState(0, 0);
            _viewModel.RecordSuspendedXtermQueueCollapsed(reason);
            MarkXtermFullRerenderNeeded(reason, batch.LineCount, batch.AppendedText.Length);
            return;
        }

        _viewModel.RecordSuspendedXtermQueueState(pendingLines, pendingCharacters);

        var pendingDelta = Math.Max(0, batch.EndDisplayedLineCount - _xtermSyncedThroughDisplayedLineCount);
        _viewModel.RecordMinimizedVisualAppendCoalesced(batch.LineCount, batch.AppendedText.Length);
        _viewModel.RecordRenderedSequenceState(_xtermSyncedThroughDisplayedLineCount, pendingDelta);
    }

    private void ClearSuspendedXtermBatches()
    {
        lock (_suspendedXtermBatchGate)
        {
            _suspendedXtermBatches.Clear();
            _suspendedXtermLineCount = 0;
            _suspendedXtermCharacterCount = 0;
            _suspendedXtermClearRequested = false;
        }
        _viewModel.RecordSuspendedXtermQueueState(0, 0);
    }

    private (LogTextBatch[] Batches, bool ClearRequested, int LineCount, int CharacterCount) DrainSuspendedXtermBatches()
    {
        lock (_suspendedXtermBatchGate)
        {
            if (_suspendedXtermBatches.Count == 0 && !_suspendedXtermClearRequested)
            {
                return (Array.Empty<LogTextBatch>(), false, 0, 0);
            }

            var batches = _suspendedXtermBatches.ToArray();
            _suspendedXtermBatches.Clear();
            var clearRequested = _suspendedXtermClearRequested;
            _suspendedXtermClearRequested = false;
            var lineCount = _suspendedXtermLineCount;
            var characterCount = (int)Math.Min(int.MaxValue, _suspendedXtermCharacterCount);
            _suspendedXtermLineCount = 0;
            _suspendedXtermCharacterCount = 0;
            _viewModel.RecordSuspendedXtermQueueState(0, 0);

            return (batches, clearRequested, lineCount, characterCount);
        }
    }

    private bool HasSuspendedXtermWork()
    {
        lock (_suspendedXtermBatchGate)
        {
            return _suspendedXtermClearRequested || _suspendedXtermBatches.Count > 0;
        }
    }

    private void QueueXtermFullRerenderAfterRestore(string reason)
    {
        if (_restoreRerenderQueued || IsClosingOrClosed)
        {
            return;
        }

        _restoreRerenderQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _restoreRerenderQueued = false;
            if (IsClosingOrClosed ||
                _isVisualAppendSuspendedForMinimize ||
                _viewModel.IsLogRenderingPaused ||
                !_xtermNeedsFullRerenderAfterRestore)
            {
                return;
            }

            QueueFullXtermRerender(reason, isRestoreRender: true, debounce: false);
        });
    }

    private void QueueXtermDeltaCatchUpAfterRestore(string reason)
    {
        if (_restoreDeltaCatchUpQueued || IsClosingOrClosed || !HasSuspendedXtermWork())
        {
            return;
        }

        if (!_isXtermReady)
        {
            MarkXtermFullRerenderNeeded("xterm not ready during restore delta catch-up");
            return;
        }

        _restoreDeltaCatchUpQueued = true;
        DispatcherQueue.TryEnqueue(async () =>
        {
            _restoreDeltaCatchUpQueued = false;
            await AppendSuspendedXtermDeltaAfterRestoreAsync(reason);
        });
    }

    private static bool ShouldShowLogRestoreOverlay(bool isFullRender, int lineCount)
    {
        return isFullRender || lineCount >= LogRestoreOverlayLineThreshold;
    }

    private async Task ShowLogRestoreOverlayAsync(string title, int lineCount)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var queued = DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await ShowLogRestoreOverlayAsync(title, lineCount);
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
            if (!queued)
            {
                completion.TrySetResult();
            }
            await completion.Task;
            return;
        }

        if (IsClosingOrClosed)
        {
            return;
        }

        LogRestoreOverlayTitle.Text = string.IsNullOrWhiteSpace(title)
            ? "로그 화면 복원 중..."
            : title.Trim();
        LogRestoreOverlayDetail.Text = lineCount > 0
            ? $"{lineCount:N0} lines"
            : "Preparing log view";
        LogRestoreOverlay.Visibility = Visibility.Visible;

        if (_isXtermReady)
        {
            try
            {
                var titleJson = JsonSerializer.Serialize(LogRestoreOverlayTitle.Text);
                var detailJson = JsonSerializer.Serialize(LogRestoreOverlayDetail.Text);
                await XtermLogWebView.ExecuteScriptAsync(
                    $"window.serialMonitorShowRestoreOverlay && window.serialMonitorShowRestoreOverlay({titleJson}, {detailJson});");
                // Give WebView2 a paint opportunity before xterm starts parsing the bulk write.
                await Task.Delay(50);
            }
            catch (Exception ex)
            {
                _viewModel.RecordXtermLayoutError($"xterm restore overlay show failed: {ex.Message}");
            }
        }
    }

    private async Task HideLogRestoreOverlayAsync()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var queued = DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await HideLogRestoreOverlayAsync();
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
            if (!queued)
            {
                completion.TrySetResult();
            }
            await completion.Task;
            return;
        }

        if (_isXtermReady)
        {
            try
            {
                await XtermLogWebView.ExecuteScriptAsync(
                    "window.serialMonitorHideRestoreOverlay && window.serialMonitorHideRestoreOverlay();");
            }
            catch (Exception ex)
            {
                _viewModel.RecordXtermLayoutError($"xterm restore overlay hide failed: {ex.Message}");
            }
        }
        LogRestoreOverlay.Visibility = Visibility.Collapsed;
    }

    private async Task AppendSuspendedXtermDeltaAfterRestoreAsync(string reason)
    {
        if (!_isXtermReady ||
            IsClosingOrClosed ||
            _isVisualAppendSuspendedForMinimize ||
            _viewModel.IsLogRenderingPaused)
        {
            return;
        }

        var restoreStartedAt = DateTimeOffset.Now;
        var drained = DrainSuspendedXtermBatches();
        if (!drained.ClearRequested && drained.Batches.Length == 0)
        {
            _viewModel.RecordRestoreFullRerenderSuppressed("restore delta append; no pending visual delta");
            _viewModel.RecordRenderedSequenceState(
                _xtermSyncedThroughDisplayedLineCount,
                Math.Max(0, _viewModel.Log.DisplayedLineCount - _xtermSyncedThroughDisplayedLineCount));
            return;
        }

        var showRestoreOverlay = ShouldShowLogRestoreOverlay(isFullRender: false, drained.LineCount);
        if (showRestoreOverlay)
        {
            await ShowLogRestoreOverlayAsync("로그 화면 복원 중...", drained.LineCount);
        }

        _viewModel.RecordRestoreFullRerenderSuppressed(
            string.IsNullOrWhiteSpace(reason) ? "restore delta append" : reason);
        _viewModel.RecordRestoreRenderStarted(
            "delta append",
            drained.LineCount,
            _xtermSyncedThroughDisplayedLineCount,
            Math.Max(0, _viewModel.Log.DisplayedLineCount - _xtermSyncedThroughDisplayedLineCount));

        await _xtermAppendGate.WaitAsync();
        try
        {
            if (_isVisualAppendSuspendedForMinimize || _viewModel.IsLogRenderingPaused)
            {
                RequeueSuspendedXtermBatches(drained.Batches, drained.ClearRequested);
                return;
            }

            if (!await WaitForXtermAppendQueueIdleAsync(TimeSpan.FromSeconds(5)))
            {
                RequeueSuspendedXtermBatches(drained.Batches, drained.ClearRequested);
                MarkXtermFullRerenderNeeded("restore delta append queue did not become idle");
                return;
            }
            await SyncXtermScrollbackSizeAsync();

            if (drained.ClearRequested)
            {
                Interlocked.Increment(ref _xtermRenderGeneration);
                await XtermLogWebView.ExecuteScriptAsync("window.serialMonitorClear && window.serialMonitorClear();");
                _xtermSyncedThroughDisplayedLineCount = drained.Batches.Length == 0
                    ? _viewModel.Log.DisplayedLineCount
                    : 0;
            }

            foreach (var batch in drained.Batches)
            {
                if (_isVisualAppendSuspendedForMinimize || _viewModel.IsLogRenderingPaused)
                {
                    RequeueSuspendedXtermBatches(
                        drained.Batches.SkipWhile(item => !ReferenceEquals(item, batch)).ToArray(),
                        clearRequested: false);
                    return;
                }

                if (batch.EndDisplayedLineCount <= _xtermSyncedThroughDisplayedLineCount)
                {
                    continue;
                }

                _viewModel.RecordXtermAppendQueued(batch.AppendedText.Length);
                try
                {
                    var appendCompleted = await ExecuteXtermAppendScriptAsync(
                        batch.AppendedText,
                        batch.LineCount,
                        autoScroll: false);
                    if (!appendCompleted)
                    {
                        MarkXtermFullRerenderNeeded("restore delta append interrupted");
                        return;
                    }

                    _xtermSyncedThroughDisplayedLineCount = batch.EndDisplayedLineCount;
                }
                finally
                {
                    _viewModel.RecordXtermAppendDequeued(batch.AppendedText.Length);
                }
            }

            if (!await WaitForXtermAppendQueueIdleAsync(TimeSpan.FromSeconds(30)))
            {
                MarkXtermFullRerenderNeeded("restore delta append completion timed out");
                return;
            }
            if (_viewModel.IsEffectiveXtermAutoScrollEnabled)
            {
                await ScrollXtermToBottomAsync("Restore delta final scroll");
            }

            var pendingDelta = Math.Max(0, _viewModel.Log.DisplayedLineCount - _xtermSyncedThroughDisplayedLineCount);
            _viewModel.RecordRenderedSequenceState(_xtermSyncedThroughDisplayedLineCount, pendingDelta);
            _viewModel.RecordRestoreRenderCompleted(
                drained.LineCount,
                DateTimeOffset.Now - restoreStartedAt,
                "delta append");
        }
        catch (Exception ex)
        {
            RequeueSuspendedXtermBatches(drained.Batches, drained.ClearRequested);
            _viewModel.RecordXtermAppendError($"restore delta append failed: {ex.Message}");
            MarkXtermFullRerenderNeeded("restore delta append failed");
        }
        finally
        {
            if (showRestoreOverlay)
            {
                await HideLogRestoreOverlayAsync();
            }

            _xtermAppendGate.Release();
        }
    }

    private void RequeueSuspendedXtermBatches(IReadOnlyList<LogTextBatch> batches, bool clearRequested)
    {
        var collapseToFullRerender = false;
        var pendingLines = 0;
        long pendingCharacters = 0;
        lock (_suspendedXtermBatchGate)
        {
            var existing = _suspendedXtermBatches.ToArray();
            _suspendedXtermBatches.Clear();
            _suspendedXtermLineCount = 0;
            _suspendedXtermCharacterCount = 0;

            if (clearRequested)
            {
                _suspendedXtermClearRequested = true;
            }

            foreach (var batch in batches)
            {
                _suspendedXtermBatches.Enqueue(batch);
                _suspendedXtermLineCount += batch.LineCount;
                _suspendedXtermCharacterCount += batch.AppendedText.Length;
            }

            foreach (var batch in existing)
            {
                _suspendedXtermBatches.Enqueue(batch);
                _suspendedXtermLineCount += batch.LineCount;
                _suspendedXtermCharacterCount += batch.AppendedText.Length;
            }

            if (_suspendedXtermLineCount > _viewModel.MaxVisibleLogLines ||
                _suspendedXtermCharacterCount > XtermSuspendedBatchMaxChars)
            {
                _suspendedXtermBatches.Clear();
                _suspendedXtermLineCount = 0;
                _suspendedXtermCharacterCount = 0;
                collapseToFullRerender = true;
            }
            else
            {
                pendingLines = _suspendedXtermLineCount;
                pendingCharacters = _suspendedXtermCharacterCount;
            }
        }

        _viewModel.RecordSuspendedXtermQueueState(pendingLines, pendingCharacters);
        if (collapseToFullRerender)
        {
            const string reason = "requeued xterm delta exceeded visible-buffer bound";
            _viewModel.RecordSuspendedXtermQueueCollapsed(reason);
            MarkXtermFullRerenderNeeded(reason);
        }
    }

    private void QueueFullXtermRerender(
        string reason,
        bool isRestoreRender = false,
        bool debounce = true)
    {
        if (IsClosingOrClosed)
        {
            return;
        }

        var normalizedReason = NormalizeFullXtermRerenderReason(reason);
        if (!_isXtermReady)
        {
            _viewModel.RecordFullXtermRerenderCanceled(
                normalizedReason,
                "xterm is not ready");
            return;
        }

        if (IsXtermVisualAppendSuspended())
        {
            MarkXtermFullRerenderNeeded(normalizedReason);
            _viewModel.RecordFullXtermRerenderCanceled(
                normalizedReason,
                "xterm visual append is suspended");
            return;
        }

        if (_viewModel.IsLogRenderingPaused)
        {
            MarkXtermFullRerenderNeeded(normalizedReason);
            _viewModel.RecordFullXtermRerenderCanceled(
                normalizedReason,
                "rendering is paused");
            return;
        }

        var shouldSchedule = false;
        lock (_fullXtermRerenderGate)
        {
            _viewModel.RecordFullXtermRerenderRequested(normalizedReason);

            if (_fullXtermRerenderRunning)
            {
                _fullXtermRerenderRequestedWhileRunning = true;
                _queuedFullXtermRerenderIsRestore |= isRestoreRender;
                _queuedFullXtermRerenderReason = MergeFullXtermRerenderReason(
                    _queuedFullXtermRerenderReason,
                    normalizedReason);
                _viewModel.RecordFullXtermRerenderCoalesced(normalizedReason);
                return;
            }

            if (_fullXtermRerenderQueued)
            {
                _queuedFullXtermRerenderIsRestore |= isRestoreRender;
                _queuedFullXtermRerenderReason = MergeFullXtermRerenderReason(
                    _queuedFullXtermRerenderReason,
                    normalizedReason);
                _viewModel.RecordFullXtermRerenderCoalesced(normalizedReason);
                return;
            }

            _fullXtermRerenderQueued = true;
            _queuedFullXtermRerenderIsRestore = isRestoreRender;
            _queuedFullXtermRerenderReason = normalizedReason;
            shouldSchedule = true;
        }

        if (shouldSchedule)
        {
            ScheduleQueuedFullXtermRerender(debounce);
        }
    }

    private void ScheduleQueuedFullXtermRerender(bool debounce)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (debounce)
            {
                await Task.Delay(75);
            }

            await DrainQueuedFullXtermRerenderAsync();
        });
    }

    private async Task DrainQueuedFullXtermRerenderAsync()
    {
        string reason;
        bool isRestoreRender;
        lock (_fullXtermRerenderGate)
        {
            if (!_fullXtermRerenderQueued || _fullXtermRerenderRunning)
            {
                return;
            }

            _fullXtermRerenderQueued = false;
            _fullXtermRerenderRunning = true;
            reason = _queuedFullXtermRerenderReason;
            isRestoreRender = _queuedFullXtermRerenderIsRestore;
            _queuedFullXtermRerenderReason = "full re-render";
            _queuedFullXtermRerenderIsRestore = false;
        }

        var renderGeneration = Interlocked.Increment(ref _fullXtermRerenderGeneration);
        try
        {
            await SyncXtermFromVisibleLogAsync(isRestoreRender, reason, renderGeneration);
        }
        finally
        {
            var shouldScheduleAgain = false;
            lock (_fullXtermRerenderGate)
            {
                _fullXtermRerenderRunning = false;
                if (_fullXtermRerenderRequestedWhileRunning)
                {
                    _fullXtermRerenderRequestedWhileRunning = false;
                    _fullXtermRerenderQueued = true;
                    shouldScheduleAgain = true;
                }
            }

            if (shouldScheduleAgain)
            {
                ScheduleQueuedFullXtermRerender(debounce: true);
            }
        }
    }

    private static string NormalizeFullXtermRerenderReason(string? reason)
    {
        return string.IsNullOrWhiteSpace(reason)
            ? "full re-render"
            : reason.Trim();
    }

    private static string MergeFullXtermRerenderReason(string existingReason, string nextReason)
    {
        var existing = NormalizeFullXtermRerenderReason(existingReason);
        var next = NormalizeFullXtermRerenderReason(nextReason);
        if (string.Equals(existing, next, StringComparison.Ordinal))
        {
            return existing;
        }

        if (string.Equals(existing, "full re-render", StringComparison.Ordinal) &&
            !string.Equals(next, "full re-render", StringComparison.Ordinal))
        {
            return next;
        }

        if (existing.Contains(next, StringComparison.Ordinal))
        {
            return existing;
        }

        var merged = $"{existing}; {next}";
        return merged.Length <= 160 ? merged : merged[^160..];
    }

    private static bool ShouldForceFullRerenderWhileMinimized(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        var normalized = reason.Trim();
        return normalized.Contains("RX view", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("timestamp", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("capacity", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("filter", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("profile", StringComparison.OrdinalIgnoreCase);
    }

    private void QueueRestoreRerenderRetry()
    {
        if (_restoreRerenderRetryCount >= 1 || IsClosingOrClosed)
        {
            return;
        }

        _restoreRerenderRetryCount++;
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(750);
            if (!IsClosingOrClosed &&
                !_isVisualAppendSuspendedForMinimize &&
                !_viewModel.IsLogRenderingPaused &&
                _xtermNeedsFullRerenderAfterRestore)
            {
                QueueXtermFullRerenderAfterRestore("restore retry");
            }
        });
    }

    private async Task InitializeXtermWebViewAsync()
    {
        try
        {
            var assetDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "xterm");
            var indexPath = Path.Combine(assetDirectory, "index.html");
            if (!File.Exists(indexPath))
            {
                _viewModel.RecordXtermAppendError($"xterm asset missing: {indexPath}");
                return;
            }

            await XtermLogWebView.EnsureCoreWebView2Async();
            XtermLogWebView.CoreWebView2.WebMessageReceived += OnXtermWebMessageReceived;
            XtermLogWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "serialmonitor.local",
                assetDirectory,
                CoreWebView2HostResourceAccessKind.Allow);
            var loadingAssetDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Loading");
            if (Directory.Exists(loadingAssetDirectory))
            {
                XtermLogWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "serialmonitor-loading.local",
                    loadingAssetDirectory,
                    CoreWebView2HostResourceAccessKind.Allow);
            }
            XtermLogWebView.NavigationCompleted += OnXtermNavigationCompleted;
            XtermLogWebView.Source = new Uri("https://serialmonitor.local/index.html");
        }
        catch (Exception ex)
        {
            _viewModel.RecordXtermAppendError($"xterm WebView2 init failed: {ex.Message}");
        }
    }

    private async void OnXtermNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        FailPendingXtermAppendAcknowledgements();
        if (!args.IsSuccess)
        {
            _viewModel.RecordXtermAppendError($"xterm navigation failed: {args.WebErrorStatus}");
            return;
        }

        _isXtermReady = true;
        _viewModel.SetXtermReady(true);
        await SyncXtermScrollbackSizeAsync();
        QueueXtermFit();
        if (_xtermNeedsFullRerenderAfterRestore)
        {
            QueueXtermFullRerenderAfterRestore("xterm ready");
        }
        else
        {
            QueueFullXtermRerender("xterm ready", debounce: false);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.EffectiveXtermScrollbackSize))
        {
            _ = SyncXtermScrollbackSizeAsync();
        }

        if (args.PropertyName == nameof(MainViewModel.IsAutoScrollEnabled) &&
            _viewModel.IsEffectiveXtermAutoScrollEnabled &&
            !_isVisualAppendSuspendedForMinimize)
        {
            _ = ScrollXtermToBottomAsync("Auto Scroll enabled");
        }

        if (args.PropertyName == nameof(MainViewModel.IsXtermAppendBackpressureActive) &&
            !_viewModel.IsXtermAppendBackpressureActive)
        {
            var hadDeferredRerender = _xtermFullRerenderDeferredForBackpressure;
            QueueDeferredFullXtermRerenderAfterBackpressure();
            if (!hadDeferredRerender &&
                _viewModel.IsEffectiveXtermAutoScrollEnabled &&
                !_isVisualAppendSuspendedForMinimize)
            {
                _ = ScrollXtermToBottomAsync("Auto Scroll resumed after xterm backlog");
            }
        }

        if (args.PropertyName == nameof(MainViewModel.IsLogRenderingPaused) &&
            !_viewModel.IsLogRenderingPaused &&
            !_isVisualAppendSuspendedForMinimize)
        {
            if (_xtermNeedsFullRerenderAfterRestore)
            {
                QueueXtermFullRerenderAfterRestore("rendering resumed");
            }
            else
            {
                QueueXtermDeltaCatchUpAfterRestore("rendering resumed");
            }
        }

        if (args.PropertyName == nameof(MainViewModel.CuteBackgroundMode) ||
            args.PropertyName == nameof(MainViewModel.CuteBackgroundImagePath) ||
            args.PropertyName == nameof(MainViewModel.CuteBackgroundOpacity))
        {
            UpdateCuteBackgroundImage();
        }
    }

    private void UpdateCuteBackgroundImage()
    {
        try
        {
            var enabled = _viewModel.CuteBackgroundMode;
            var customPath = _viewModel.CuteBackgroundImagePath?.Trim() ?? string.Empty;
            var opacity = _viewModel.CuteBackgroundOpacity;
            var bundledPath = BundledCuteBackgroundPath;
            var customExists = false;
            var bundledExists = false;

            if (!string.IsNullOrWhiteSpace(customPath))
            {
                try
                {
                    customExists = File.Exists(customPath);
                }
                catch
                {
                    customExists = false;
                }
            }

            try
            {
                bundledExists = File.Exists(bundledPath);
            }
            catch
            {
                bundledExists = false;
            }

            var resolvedPath = string.Empty;
            var source = "none";
            string? error = null;

            if (enabled)
            {
                if (customExists)
                {
                    resolvedPath = customPath;
                    source = "custom path";
                }
                else if (bundledExists)
                {
                    resolvedPath = bundledPath;
                    if (string.IsNullOrWhiteSpace(customPath))
                    {
                        source = "bundled default";
                    }
                    else
                    {
                        source = "fallback bundled default";
                        error = $"Custom background image not found; using bundled default: {customPath}";
                    }
                }
                else
                {
                    error = string.IsNullOrWhiteSpace(customPath)
                        ? "Bundled background image not found."
                        : "Custom and bundled background images were not found.";
                }
            }

            var stateUnchanged =
                _lastAppliedCuteBackgroundEnabled == enabled &&
                string.Equals(_lastAppliedCuteBackgroundPath, resolvedPath, StringComparison.Ordinal) &&
                string.Equals(_lastAppliedCuteBackgroundCustomPath, customPath, StringComparison.Ordinal) &&
                string.Equals(_lastAppliedCuteBackgroundSource, source, StringComparison.Ordinal) &&
                _lastAppliedCuteBackgroundOpacity.HasValue &&
                Math.Abs(_lastAppliedCuteBackgroundOpacity.Value - opacity) < 0.0001;

            if (stateUnchanged)
            {
                _viewModel.RecordCuteBackgroundApplySkipped();
                return;
            }

            var pathChanged = !string.Equals(_lastAppliedCuteBackgroundPath, resolvedPath, StringComparison.Ordinal);
            _lastAppliedCuteBackgroundEnabled = enabled;
            _lastAppliedCuteBackgroundPath = resolvedPath;
            _lastAppliedCuteBackgroundCustomPath = customPath;
            _lastAppliedCuteBackgroundSource = source;
            _lastAppliedCuteBackgroundOpacity = opacity;

            if (!enabled)
            {
                _viewModel.RecordCuteBackgroundApplyResult(
                    fileExists: false,
                    loaded: false,
                    error: null,
                    source: source,
                    bundledPath: bundledPath);
                return;
            }

            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                if (CuteBackgroundImage.Source is not null)
                {
                    CuteBackgroundImage.Source = null;
                }

                _cachedCuteBackgroundPath = null;
                _cachedCuteBackgroundImage = null;
                _viewModel.RecordCuteBackgroundApplyResult(
                    fileExists: false,
                    loaded: false,
                    error: error,
                    source: source,
                    bundledPath: bundledPath);
                return;
            }

            if (pathChanged ||
                _cachedCuteBackgroundImage is null ||
                !string.Equals(_cachedCuteBackgroundPath, resolvedPath, StringComparison.Ordinal))
            {
                _cachedCuteBackgroundImage = new BitmapImage(new Uri(resolvedPath, UriKind.Absolute));
                _cachedCuteBackgroundPath = resolvedPath;
                _viewModel.RecordCuteBackgroundImageReloaded();
            }

            if (!ReferenceEquals(CuteBackgroundImage.Source, _cachedCuteBackgroundImage))
            {
                CuteBackgroundImage.Source = _cachedCuteBackgroundImage;
            }

            _viewModel.RecordCuteBackgroundApplyResult(
                fileExists: true,
                loaded: true,
                error: error,
                source: source,
                bundledPath: bundledPath);
        }
        catch (Exception ex)
        {
            if (CuteBackgroundImage.Source is not null)
            {
                CuteBackgroundImage.Source = null;
            }

            _cachedCuteBackgroundPath = null;
            _cachedCuteBackgroundImage = null;
            _viewModel.RecordCuteBackgroundApplyResult(
                fileExists: false,
                loaded: false,
                error: $"Background image failed to load: {ex.Message}",
                source: "none",
                bundledPath: BundledCuteBackgroundPath);
        }
    }

    private void OnDetectedEventsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_isPointerOverEventList)
        {
            return;
        }

        if (_viewModel.IsEventAutoScrollSuppressedByXtermBackpressure)
        {
            _viewModel.RecordXtermBackpressureEventAutoScrollSuppressed();
            return;
        }

        if (_viewModel.IsEventAutoScrollEnabled)
        {
            QueueEventAutoScrollToLatest(selectLatest: false);
        }
    }

    private void OnXtermWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (IsClosingOrClosed)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            var messageType = typeElement.GetString();
            if (string.Equals(messageType, "xtermAppendCompleted", StringComparison.Ordinal))
            {
                if (!root.TryGetProperty("requestId", out var requestIdElement) ||
                    !requestIdElement.TryGetInt64(out var requestId))
                {
                    return;
                }

                var success = !root.TryGetProperty("success", out var successElement) ||
                    successElement.ValueKind == JsonValueKind.True;
                CompleteXtermAppendAcknowledgement(requestId, success);
                return;
            }

            if (string.Equals(messageType, "shortcut", StringComparison.Ordinal))
            {
                if (root.TryGetProperty("shortcut", out var shortcutElement))
                {
                    _ = ExecuteSavedCommandShortcutAsync(shortcutElement.GetString());
                }

                return;
            }

            if (string.Equals(messageType, "markerShortcut", StringComparison.Ordinal))
            {
                _ = _viewModel.AddDefaultMarkerAsync();
                return;
            }

            if (string.Equals(messageType, "searchShortcut", StringComparison.Ordinal))
            {
                var action = root.TryGetProperty("action", out var actionElement)
                    ? actionElement.GetString()
                    : string.Empty;
                var source = root.TryGetProperty("source", out var sourceElement)
                    ? sourceElement.GetString()
                    : "xterm";
                _ = HandleSearchShortcutAsync(action, source ?? "xterm");
                return;
            }

            if (string.Equals(messageType, "toggleAutoScroll", StringComparison.Ordinal))
            {
                _viewModel.IsAutoScrollEnabled = !_viewModel.IsAutoScrollEnabled;
                return;
            }

            if (string.Equals(messageType, "copySelection", StringComparison.Ordinal))
            {
                if (!root.TryGetProperty("text", out var textElement))
                {
                    return;
                }

                var selectedText = textElement.GetString();
                if (string.IsNullOrEmpty(selectedText))
                {
                    return;
                }

                var package = new DataPackage();
                package.SetText(selectedText);
                Clipboard.SetContent(package);
                Clipboard.Flush();
                _viewModel.RecordXtermCopySuccess(selectedText.Length);
                return;
            }

            if (string.Equals(messageType, "contextMenuAction", StringComparison.Ordinal))
            {
                var action = root.TryGetProperty("action", out var actionElement)
                    ? actionElement.GetString()
                    : string.Empty;
                var selectedText = root.TryGetProperty("selectedText", out var selectedTextElement)
                    ? selectedTextElement.GetString()
                    : string.Empty;
                _ = HandleXtermContextMenuActionAsync(action, selectedText);
            }
        }
        catch (Exception ex)
        {
            _viewModel.RecordXtermCopyError($"xterm message failed: {ex.Message}");
        }
    }

    private async Task HandleXtermContextMenuActionAsync(string? action, string? selectedText)
    {
        if (IsClosingOrClosed)
        {
            return;
        }

        try
        {
            switch (action)
            {
                case "copy":
                    if (CopyTextToClipboard(selectedText, "Copy"))
                    {
                        _viewModel.RecordXtermCopySuccess(selectedText?.Length ?? 0);
                        _viewModel.RecordXtermContextMenuAction("Copy xterm selection");
                    }
                    break;

                case "copyAllVisible":
                    var visibleText = _viewModel.GetVisibleLogPlainTextSnapshot(out var lineCount);
                    if (CopyTextToClipboard(visibleText, "Copy all visible"))
                    {
                        _viewModel.RecordCopyAllVisibleSuccess(lineCount);
                    }
                    break;

                case "copySinceLastTx":
                    if (!_viewModel.TryGetVisibleLogSinceLastTxPlainTextSnapshot(
                            out var txText,
                            out var txLineCount,
                            out var txCharacterCount))
                    {
                        _viewModel.RecordCopySinceLastTxNoTx();
                        break;
                    }

                    if (CopyTextToClipboard(txText, "Copy since last TX"))
                    {
                        _viewModel.RecordCopySinceLastTxSuccess(txLineCount, txCharacterCount);
                    }
                    break;

                case "copySinceLastMark":
                    if (!_viewModel.TryGetVisibleLogSinceLastMarkPlainTextSnapshot(
                            out var markText,
                            out var markLineCount,
                            out var markCharacterCount))
                    {
                        _viewModel.RecordCopySinceLastMarkNoMark();
                        break;
                    }

                    if (CopyTextToClipboard(markText, "Copy since last MARK"))
                    {
                        _viewModel.RecordCopySinceLastMarkSuccess(markLineCount, markCharacterCount);
                    }
                    break;

                case "clear":
                    await _viewModel.ClearScreenFromXtermContextMenuAsync();
                    break;

                case "quickMarker":
                    await _viewModel.AddDefaultMarkerAsync();
                    _viewModel.RecordXtermContextMenuAction("Quick marker");
                    break;

                case "searchSelected":
                    _viewModel.SearchSelectedTextFromXterm(selectedText);
                    break;

                default:
                    _viewModel.RecordXtermContextMenuError($"Unknown xterm context menu action: {action ?? "(null)"}");
                    break;
            }
        }
        catch (Exception ex)
        {
            if (string.Equals(action, "copySinceLastTx", StringComparison.Ordinal))
            {
                _viewModel.RecordCopySinceLastTxError($"Copy since last TX failed: {ex.Message}");
                return;
            }

            if (string.Equals(action, "copySinceLastMark", StringComparison.Ordinal))
            {
                _viewModel.RecordCopySinceLastMarkError($"Copy since last MARK failed: {ex.Message}");
                return;
            }

            _viewModel.RecordXtermContextMenuError($"xterm context menu action failed: {ex.Message}");
        }
    }

    private bool CopyTextToClipboard(string? text, string label)
    {
        if (string.IsNullOrEmpty(text))
        {
            _viewModel.RecordXtermContextMenuError($"{label} failed: no text available.");
            return false;
        }

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        return true;
    }

    private async Task SyncXtermFromVisibleLogAsync(
        bool isRestoreRender = false,
        string reason = "full re-render",
        long renderGeneration = 0)
    {
        if (!_isXtermReady || IsClosingOrClosed)
        {
            return;
        }

        if (IsXtermVisualAppendSuspended())
        {
            MarkXtermFullRerenderNeeded(reason);
            _viewModel.RecordFullXtermRerenderCanceled(
                reason,
                "xterm visual append is suspended");
            return;
        }

        if (_viewModel.IsLogRenderingPaused)
        {
            MarkXtermFullRerenderNeeded(reason);
            _viewModel.RecordFullXtermRerenderCanceled(
                reason,
                "rendering is paused");
            return;
        }

        var restoreStartedAt = DateTimeOffset.Now;
        var renderStartedAt = restoreStartedAt;
        XtermScrollState? previousScrollState = null;
        var finalScrollAction = "unchanged";
        var scrollRestoreAttempted = false;
        var suppressedIntermediateAutoScrollCount = 0;
        var clearCount = 0;
        var visibilityToggleCount = 0;
        var overlayLineCount = _viewModel.Log.CurrentVisibleLineCount;
        var showRestoreOverlay = ShouldShowLogRestoreOverlay(isRestoreRender, overlayLineCount);
        if (showRestoreOverlay)
        {
            await ShowLogRestoreOverlayAsync(
                isRestoreRender ? "로그 화면 복원 중..." : "로그 화면 다시 그리는 중...",
                overlayLineCount);
        }

        if (renderGeneration <= 0)
        {
            renderGeneration = Interlocked.Increment(ref _fullXtermRerenderGeneration);
        }

        if (isRestoreRender)
        {
            _viewModel.RecordRestoreRenderStarted();
        }

        _viewModel.RecordFullXtermRerenderStarted(reason, renderGeneration);

        await _xtermAppendGate.WaitAsync();
        try
        {
            if (IsXtermVisualAppendSuspended() ||
                _viewModel.IsLogRenderingPaused)
            {
                MarkXtermFullRerenderNeeded("xterm sync suspended");
                _viewModel.RecordFullXtermRerenderEndedAfterError(
                    "suspended",
                    "xterm sync suspended",
                    renderGeneration,
                    canceled: true);
                return;
            }

            if (!await WaitForXtermAppendQueueIdleAsync(TimeSpan.FromSeconds(5)))
            {
                _viewModel.RecordFullXtermRerenderEndedAfterError(
                    "busy",
                    "xterm append queue did not become idle before full re-render",
                    renderGeneration,
                    canceled: true);
                return;
            }
            await SyncXtermScrollbackSizeAsync();
            previousScrollState = await GetXtermScrollStateAsync();
            var shouldScrollToBottomAfterRender =
                !_viewModel.IsXtermAppendBackpressureActive &&
                (_viewModel.IsAutoScrollEnabled || previousScrollState?.AtBottom == true);

            var xtermRenderGeneration = Interlocked.Increment(ref _xtermRenderGeneration);
            var text = _viewModel.Log.GetVisibleTextSnapshot();
            var currentVisibleLineCount = _viewModel.Log.CurrentVisibleLineCount;
            var displayedLineCount = _viewModel.Log.DisplayedLineCount;
            // The bulk-replace bridge does not scroll between transport chunks.
            suppressedIntermediateAutoScrollCount = 0;

            _viewModel.RecordXtermAppendQueued(text.Length);
            var appendCompleted = false;
            try
            {
                appendCompleted = await ReplaceXtermLogAsync(
                    text,
                    autoScroll: false,
                    expectedGeneration: xtermRenderGeneration);
                clearCount = 1;
            }
            finally
            {
                _viewModel.RecordXtermAppendDequeued(text.Length);
            }

            if (!appendCompleted)
            {
                MarkXtermFullRerenderNeeded("xterm sync append interrupted");
                _viewModel.RecordFullXtermRerenderEndedAfterError(
                    "interrupted",
                    "xterm sync append interrupted",
                    renderGeneration,
                    canceled: true);
                return;
            }

            if (!await WaitForXtermAppendQueueIdleAsync(TimeSpan.FromSeconds(30)))
            {
                _viewModel.RecordFullXtermRerenderEndedAfterError(
                    "timeout",
                    "xterm full re-render completion timed out",
                    renderGeneration,
                    canceled: true);
                return;
            }

            _xtermSyncedThroughDisplayedLineCount = displayedLineCount;
            ClearSuspendedXtermBatches();
            CompleteLiveXtermBatchesCoveredBySnapshot(displayedLineCount);
            _viewModel.RecordRenderedSequenceState(displayedLineCount, pendingDeltaLineCount: 0);

            if (shouldScrollToBottomAfterRender)
            {
                await ScrollXtermToBottomAsync("Full re-render final scroll");
                finalScrollAction = "bottom";
            }
            else if (previousScrollState?.Ok == true)
            {
                scrollRestoreAttempted = true;
                finalScrollAction = await RestoreXtermScrollStateAsync(previousScrollState)
                    ? "restored"
                    : "unchanged";
            }

            _viewModel.RecordFullXtermRerenderCompleted(
                _viewModel.Log.CurrentVisibleLineCount,
                DateTimeOffset.Now - renderStartedAt,
                scrollRestoreAttempted,
                finalScrollAction,
                suppressedIntermediateAutoScrollCount,
                renderGeneration,
                clearCount,
                visibilityToggleCount);

            if (isRestoreRender)
            {
                _xtermNeedsFullRerenderAfterRestore = false;
                _pendingXtermFullRerenderReason = "full re-render";
                _restoreRerenderRetryCount = 0;
                _viewModel.SetXtermNeedsFullRerenderAfterRestore(false);
                _viewModel.RecordRestoreRenderCompleted(
                    _viewModel.Log.CurrentVisibleLineCount,
                    DateTimeOffset.Now - restoreStartedAt);
            }
        }
        catch (Exception ex)
        {
            _viewModel.RecordXtermAppendError($"xterm sync failed: {ex.Message}");
            _viewModel.RecordFullXtermRerenderEndedAfterError(
                "error",
                ex.Message,
                renderGeneration);
            if (isRestoreRender)
            {
                MarkXtermFullRerenderNeeded("restore sync failed");
                QueueRestoreRerenderRetry();
            }
        }
        finally
        {
            if (showRestoreOverlay)
            {
                await HideLogRestoreOverlayAsync();
            }

            _xtermAppendGate.Release();
            QueueXtermAppendRecoveryRetry();
        }
    }

    private async Task<bool> AppendLogBatchToXtermAsync(LogTextBatch batch, long generation)
    {
        if (!_isXtermReady || IsClosingOrClosed || string.IsNullOrEmpty(batch.AppendedText))
        {
            return string.IsNullOrEmpty(batch.AppendedText) || IsClosingOrClosed;
        }

        if (IsXtermVisualAppendSuspended())
        {
            EnqueueSuspendedXtermBatch(batch);
            return true;
        }

        await _xtermAppendGate.WaitAsync();
        try
        {
            if (IsXtermVisualAppendSuspended())
            {
                EnqueueSuspendedXtermBatch(batch);
                return true;
            }

            if (generation != Interlocked.Read(ref _xtermRenderGeneration))
            {
                return batch.EndDisplayedLineCount <= _xtermSyncedThroughDisplayedLineCount;
            }

            if (batch.EndDisplayedLineCount <= _xtermSyncedThroughDisplayedLineCount)
            {
                return true;
            }

            var autoScroll = _viewModel.IsEffectiveXtermAutoScrollEnabled;
            if (!autoScroll && _viewModel.IsXtermAppendBackpressureActive && _viewModel.IsAutoScrollEnabled)
            {
                _viewModel.RecordXtermBackpressureAutoScrollSuppressed();
            }

            var appendCompleted = await ExecuteXtermAppendScriptAsync(batch.AppendedText, batch.LineCount, autoScroll);
            if (!appendCompleted)
            {
                return false;
            }

            _xtermSyncedThroughDisplayedLineCount = batch.EndDisplayedLineCount;
            _viewModel.RecordRenderedSequenceState(
                _xtermSyncedThroughDisplayedLineCount,
                Math.Max(0, _viewModel.Log.DisplayedLineCount - _xtermSyncedThroughDisplayedLineCount));
            return true;
        }
        catch (Exception ex)
        {
            _viewModel.RecordXtermAppendError($"xterm append failed: {ex.Message}");
            return false;
        }
        finally
        {
            _xtermAppendGate.Release();
        }
    }

    private void CompleteLiveXtermBatch(LogTextBatch batch)
    {
        _viewModel.RecordXtermAppendDequeued(batch.AppendedText.Length);
        var pendingLines = Interlocked.Add(ref _pendingLiveXtermLines, -batch.LineCount);
        if (pendingLines < 0)
        {
            Interlocked.Exchange(ref _pendingLiveXtermLines, 0);
        }

        UpdateXtermAppendBackpressure();
    }

    private void UpdateXtermAppendBackpressure()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(UpdateXtermAppendBackpressure);
            return;
        }

        var pendingLines = Math.Max(0, Volatile.Read(ref _pendingLiveXtermLines));
        var pendingCharacters = Math.Max(0, _viewModel.XtermPendingCharacterCount);
        var highLineWatermark = Math.Min(
            XtermBackpressureHighLines,
            Math.Max(100, _viewModel.MaxVisibleLogLines / 2));
        var lowLineWatermark = Math.Min(
            XtermBackpressureLowLines,
            Math.Max(25, highLineWatermark / 4));
        if (!_viewModel.IsXtermAppendBackpressureActive)
        {
            if (pendingLines >= highLineWatermark ||
                pendingCharacters >= XtermBackpressureHighChars)
            {
                _viewModel.SetXtermAppendBackpressure(true);
            }

            return;
        }

        if (pendingLines <= lowLineWatermark &&
            pendingCharacters <= XtermBackpressureLowChars)
        {
            _viewModel.SetXtermAppendBackpressure(false);
        }
    }

    private async Task<bool> ExecuteXtermAppendScriptAsync(string text, int lineCount, bool autoScroll)
    {
        return await ExecuteXtermAppendChunksAsync(SplitXtermAppendText(text, lineCount), autoScroll);
    }

    private async Task<bool> ReplaceXtermLogAsync(string text, bool autoScroll, long expectedGeneration)
    {
        if (IsXtermVisualAppendSuspended() ||
            expectedGeneration != Interlocked.Read(ref _xtermRenderGeneration))
        {
            return false;
        }

        var beginResult = await XtermLogWebView.ExecuteScriptAsync(
            "window.serialMonitorBeginReplaceLog ? window.serialMonitorBeginReplaceLog() : false;");
        if (TryParseScriptBoolean(beginResult) != true)
        {
            return false;
        }

        foreach (var chunk in SplitXtermFullRenderTransportText(text))
        {
            if (IsXtermVisualAppendSuspended() ||
                expectedGeneration != Interlocked.Read(ref _xtermRenderGeneration))
            {
                return false;
            }

            var encodedText = JsonSerializer.Serialize(chunk);
            var queuedResult = await XtermLogWebView.ExecuteScriptAsync(
                $"window.serialMonitorQueueReplaceChunk ? window.serialMonitorQueueReplaceChunk({encodedText}) : false;");
            if (TryParseScriptBoolean(queuedResult) != true)
            {
                return false;
            }
        }

        if (IsXtermVisualAppendSuspended() ||
            expectedGeneration != Interlocked.Read(ref _xtermRenderGeneration))
        {
            return false;
        }

        var commitResult = await XtermLogWebView.ExecuteScriptAsync(
            $"window.serialMonitorCommitReplaceLog ? window.serialMonitorCommitReplaceLog({(autoScroll ? "true" : "false")}) : false;");
        return TryParseScriptBoolean(commitResult) == true;
    }

    private async Task<bool> ExecuteXtermAppendChunksAsync(
        IEnumerable<XtermAppendChunk> chunks,
        bool autoScroll,
        long? expectedGeneration = null)
    {
        foreach (var chunk in chunks)
        {
            // A normal live batch is already bounded. Let an in-flight batch finish
            // during minimize so restore can resume from its sequence boundary.
            if (IsXtermVisualAppendSuspended() && expectedGeneration.HasValue)
            {
                MarkXtermFullRerenderNeeded("xterm append chunk suspended", chunk.LineCount, chunk.Text.Length);
                return false;
            }

            if (expectedGeneration.HasValue &&
                expectedGeneration.Value != Interlocked.Read(ref _xtermRenderGeneration))
            {
                return false;
            }

            var encodedText = JsonSerializer.Serialize(chunk.Text);
            var autoScrollLiteral = autoScroll ? "true" : "false";
            var requestId = Interlocked.Increment(ref _nextXtermAppendRequestId);
            var acknowledgement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_xtermAppendAckGate)
            {
                _xtermAppendAcknowledgements[requestId] = acknowledgement;
            }

            var appendStartedAt = Stopwatch.GetTimestamp();
            try
            {
                var queuedResult = await XtermLogWebView.ExecuteScriptAsync(
                    $"window.serialMonitorAppendLog ? window.serialMonitorAppendLog({encodedText}, {autoScrollLiteral}, {requestId}) : false;");
                if (TryParseScriptBoolean(queuedResult) != true)
                {
                    return false;
                }

                var appendCompleted = await acknowledgement.Task.WaitAsync(XtermLiveAppendAckTimeout);
                _viewModel.RecordXtermAppendDuration(Stopwatch.GetElapsedTime(appendStartedAt));
                if (!appendCompleted)
                {
                    return false;
                }

                _viewModel.RecordXtermAppendSuccess(chunk.LineCount, chunk.Text.Length);
            }
            finally
            {
                lock (_xtermAppendAckGate)
                {
                    _xtermAppendAcknowledgements.Remove(requestId);
                }
            }

            if (IsClosingOrClosed)
            {
                return false;
            }

            await Task.Yield();
        }

        return true;
    }

    private void CompleteXtermAppendAcknowledgement(long requestId, bool success)
    {
        TaskCompletionSource<bool>? acknowledgement;
        lock (_xtermAppendAckGate)
        {
            _xtermAppendAcknowledgements.TryGetValue(requestId, out acknowledgement);
        }

        acknowledgement?.TrySetResult(success);
    }

    private void FailPendingXtermAppendAcknowledgements()
    {
        TaskCompletionSource<bool>[] pending;
        lock (_xtermAppendAckGate)
        {
            pending = _xtermAppendAcknowledgements.Values.ToArray();
        }

        foreach (var acknowledgement in pending)
        {
            acknowledgement.TrySetResult(false);
        }
    }

    private async Task<bool> SyncXtermScrollbackSizeAsync()
    {
        if (!_isXtermReady || IsClosingOrClosed || _isVisualAppendSuspendedForMinimize)
        {
            return false;
        }

        try
        {
            var scrollbackSize = Math.Max(1_000, _viewModel.EffectiveXtermScrollbackSize);
            var result = await XtermLogWebView.ExecuteScriptAsync(
                $"window.serialMonitorSetScrollback && window.serialMonitorSetScrollback({scrollbackSize});");
            if (TryParseScriptBoolean(result) != true)
            {
                _viewModel.RecordXtermLayoutError("xterm scrollback update was rejected.");
                return false;
            }

            var actualResult = await XtermLogWebView.ExecuteScriptAsync(
                "window.serialMonitorGetScrollback ? window.serialMonitorGetScrollback() : 0;");
            var actualSize = JsonSerializer.Deserialize<int>(actualResult);
            _viewModel.RecordXtermScrollbackApplied(actualSize);
            if (actualSize < scrollbackSize)
            {
                _viewModel.RecordXtermLayoutError(
                    $"xterm scrollback applied {actualSize:N0}, expected at least {scrollbackSize:N0}.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _viewModel.RecordXtermLayoutError($"xterm scrollback update failed: {ex.Message}");
            return false;
        }
    }

    private async Task ScrollXtermToBottomAsync(string action)
    {
        if (!_isXtermReady || IsClosingOrClosed || _isVisualAppendSuspendedForMinimize)
        {
            return;
        }

        try
        {
            var result = await XtermLogWebView.ExecuteScriptAsync("window.serialMonitorScrollToBottom ? window.serialMonitorScrollToBottom() : false;");
            _viewModel.RecordAutoScrollAction(action, TryParseScriptBoolean(result));
        }
        catch (Exception ex)
        {
            _viewModel.RecordAutoScrollError($"Auto Scroll failed: {ex.Message}");
        }
    }

    private async Task<XtermScrollState?> GetXtermScrollStateAsync()
    {
        try
        {
            var result = await XtermLogWebView.ExecuteScriptAsync(
                "window.serialMonitorGetScrollState ? window.serialMonitorGetScrollState() : null;");
            if (string.IsNullOrWhiteSpace(result) || string.Equals(result.Trim(), "null", StringComparison.Ordinal))
            {
                return null;
            }

            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new XtermScrollState(
                TryGetBoolean(root, "ok") ?? false,
                TryGetInt(root, "viewportY") ?? 0,
                TryGetInt(root, "baseY") ?? 0,
                TryGetInt(root, "rows") ?? 0,
                TryGetBoolean(root, "atBottom") ?? true);
        }
        catch (Exception ex)
        {
            _viewModel.RecordXtermLayoutError($"xterm scroll state capture failed: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> RestoreXtermScrollStateAsync(XtermScrollState state)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                viewportY = state.ViewportY,
                baseY = state.BaseY,
                rows = state.Rows,
                atBottom = state.AtBottom
            });
            var result = await XtermLogWebView.ExecuteScriptAsync(
                $"window.serialMonitorRestoreScrollState ? window.serialMonitorRestoreScrollState({payload}) : false;");
            return TryParseScriptBoolean(result) == true;
        }
        catch (Exception ex)
        {
            _viewModel.RecordXtermLayoutError($"xterm scroll restore failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> WaitForXtermAppendQueueIdleAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (IsClosingOrClosed || _isVisualAppendSuspendedForMinimize)
            {
                return false;
            }

            try
            {
                var result = await XtermLogWebView.ExecuteScriptAsync(
                    "window.serialMonitorGetAppendQueueState ? window.serialMonitorGetAppendQueueState() : { queueLength: 0, writing: false };");
                using var document = JsonDocument.Parse(result);
                var root = document.RootElement;
                var queueLength = TryGetInt(root, "queueLength") ?? 0;
                var writing = TryGetBoolean(root, "writing") ?? false;
                if (queueLength <= 0 && !writing)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _viewModel.RecordXtermLayoutError($"xterm append queue wait failed: {ex.Message}");
                return false;
            }

            await Task.Delay(25);
        }

        _viewModel.RecordXtermLayoutError(
            $"xterm append queue did not become idle within {timeout.TotalSeconds:0.#} seconds.");
        return false;
    }

    private async Task<bool> DrainXtermForViewPauseAsync(CancellationToken cancellationToken)
    {
        // All pre-boundary records have already left the ViewModel queues when this is called.
        // Wait for their host-side batches, then take the append gate so an in-flight script and
        // the JavaScript append queue are both settled before the UI reports "Paused".
        while (Volatile.Read(ref _pendingLiveXtermLines) > 0)
        {
            await Task.Delay(10, cancellationToken);
        }

        await _xtermAppendGate.WaitAsync(cancellationToken);
        try
        {
            return await WaitForXtermAppendQueueIdleAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            _xtermAppendGate.Release();
        }
    }

    private static bool? TryParseScriptBoolean(string? scriptResult)
    {
        if (string.IsNullOrWhiteSpace(scriptResult))
        {
            return null;
        }

        if (bool.TryParse(scriptResult.Trim(), out var directValue))
        {
            return directValue;
        }

        try
        {
            using var document = JsonDocument.Parse(scriptResult);
            return document.RootElement.ValueKind == JsonValueKind.True
                ? true
                : document.RootElement.ValueKind == JsonValueKind.False
                    ? false
                    : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out var value)
                ? value
                : null;
    }

    private static bool? TryGetBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.True
            ? true
            : property.ValueKind == JsonValueKind.False
                ? false
                : null;
    }

    private IEnumerable<XtermAppendChunk> SplitXtermAppendText(string text, int fallbackLineCount)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var maxLines = XtermLiveAppendMaxLines;
        var maxChars = XtermLiveAppendMaxChars;
        var start = 0;
        var next = 0;
        var linesInChunk = 0;
        var lastLineEnd = 0;

        while (next < text.Length)
        {
            var newlineIndex = text.IndexOf('\n', next);
            var lineEnd = newlineIndex >= 0 ? newlineIndex + 1 : text.Length;
            var wouldExceedChunk = linesInChunk > 0 &&
                (linesInChunk >= maxLines || lineEnd - start > maxChars);

            if (wouldExceedChunk)
            {
                yield return new XtermAppendChunk(text[start..lastLineEnd], linesInChunk);
                start = lastLineEnd;
                linesInChunk = 0;
                continue;
            }

            linesInChunk++;
            lastLineEnd = lineEnd;
            next = lineEnd;
        }

        if (start < text.Length)
        {
            var lineCount = linesInChunk > 0 ? linesInChunk : Math.Max(1, fallbackLineCount);
            yield return new XtermAppendChunk(text[start..], lineCount);
        }
    }

    private static IEnumerable<string> SplitXtermFullRenderTransportText(string text)
    {
        for (var start = 0; start < text.Length;)
        {
            var end = Math.Min(text.Length, start + XtermFullRenderTransportMaxChars);
            if (end < text.Length)
            {
                var newline = text.LastIndexOf('\n', end - 1, end - start);
                if (newline >= start)
                {
                    end = newline + 1;
                }
                else if (end > start && text[end - 1] == '\r' && text[end] == '\n')
                {
                    end--;
                }
            }

            if (end <= start)
            {
                end = Math.Min(text.Length, start + XtermFullRenderTransportMaxChars);
            }

            yield return text[start..end];
            start = end;
        }
    }

    private async Task ClearXtermAsync()
    {
        if (!_isXtermReady || IsClosingOrClosed || IsXtermVisualAppendSuspended())
        {
            if (IsXtermVisualAppendSuspended())
            {
                MarkXtermClearNeededAfterRestore();
            }

            return;
        }

        await _xtermAppendGate.WaitAsync();
        try
        {
            if (IsXtermVisualAppendSuspended())
            {
                MarkXtermClearNeededAfterRestore();
                return;
            }

            Interlocked.Increment(ref _xtermRenderGeneration);
            await XtermLogWebView.ExecuteScriptAsync("window.serialMonitorClear && window.serialMonitorClear();");
            _xtermSyncedThroughDisplayedLineCount = _viewModel.Log.DisplayedLineCount;
        }
        catch (Exception ex)
        {
            _viewModel.RecordXtermAppendError($"xterm clear failed: {ex.Message}");
        }
        finally
        {
            _xtermAppendGate.Release();
        }
    }

    private async Task FitXtermAsync()
    {
        if (!_isXtermReady || IsClosingOrClosed || _isVisualAppendSuspendedForMinimize)
        {
            return;
        }

        try
        {
            await XtermLogWebView.ExecuteScriptAsync("window.serialMonitorFit && window.serialMonitorFit();");
            _viewModel.RecordXtermFitResizeSuccess();
        }
        catch (Exception ex)
        {
            _viewModel.RecordXtermLayoutError($"xterm fit failed: {ex.Message}");
        }
    }

    private void QueueXtermFit()
    {
        if (_xtermFitQueued || IsClosingOrClosed)
        {
            return;
        }

        _xtermFitQueued = true;
        DispatcherQueue.TryEnqueue(async () =>
        {
            _xtermFitQueued = false;
            if (IsClosingOrClosed)
            {
                return;
            }

            await Task.Delay(50);
            await FitXtermAsync();
        });
    }

    private async Task SearchXtermAsync(XtermSearchRequest request)
    {
        _viewModel.RecordXtermSearchRequested();

        if (IsClosingOrClosed || _isVisualAppendSuspendedForMinimize)
        {
            return;
        }

        if (!_isXtermReady)
        {
            _viewModel.RecordXtermSearchError("xterm search failed: terminal is not ready.");
            return;
        }

        await _xtermAppendGate.WaitAsync();
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                requestId = request.RequestId,
                text = request.SearchText,
                caseSensitive = request.IsCaseSensitive,
                direction = request.Direction,
                resultIndex = request.ResultIndex
            });
            var resultJson = await XtermLogWebView.ExecuteScriptAsync(
                $"window.serialMonitorSearch ? window.serialMonitorSearch({payload}) : {{ ok: false, found: false, error: 'xterm search bridge is not available' }};");

            if (string.IsNullOrWhiteSpace(resultJson))
            {
                _viewModel.RecordXtermSearchError("xterm search failed: empty bridge response.");
                return;
            }

            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("ok", out var okElement) ||
                !okElement.GetBoolean())
            {
                var error = root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("error", out var errorElement)
                        ? errorElement.GetString()
                        : "invalid bridge response";
                _viewModel.RecordXtermSearchError($"xterm search failed: {error}");
                return;
            }

            var found = root.TryGetProperty("found", out var foundElement) &&
                foundElement.ValueKind == JsonValueKind.True;
            _viewModel.RecordXtermSearchResult(found);
        }
        catch (Exception ex)
        {
            _viewModel.RecordXtermSearchError($"xterm search failed: {ex.Message}");
        }
        finally
        {
            _xtermAppendGate.Release();
        }
    }

    private void XtermLogWebView_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        QueueXtermFit();
    }

    private void RulesTableScrollViewer_Loaded(object sender, RoutedEventArgs args)
    {
        QueueRulesTableViewportResize();
    }

    private void RulesTableScrollViewer_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        QueueRulesTableViewportResize();
    }

    private void RulesTableBodyBorder_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        QueueRulesTableViewportResize();
    }

    private void QueueRulesTableViewportResize()
    {
        if (_rulesTableViewportResizeQueued || IsClosingOrClosed)
        {
            return;
        }

        _rulesTableViewportResizeQueued = true;
        if (!DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    _rulesTableViewportResizeQueued = false;
                    UpdateRulesTableViewportHeight();
                }))
        {
            _rulesTableViewportResizeQueued = false;
        }
    }

    private void UpdateRulesTableViewportHeight()
    {
        var viewportHeight = RulesTableScrollViewer.ViewportHeight;
        if (!double.IsFinite(viewportHeight) || viewportHeight <= 0)
        {
            viewportHeight = RulesTableScrollViewer.ActualHeight;
        }

        if (!double.IsFinite(viewportHeight) || viewportHeight <= 0)
        {
            return;
        }

        if (Math.Abs(RulesTableViewportGrid.Height - viewportHeight) > 0.5 ||
            double.IsNaN(RulesTableViewportGrid.Height))
        {
            RulesTableViewportGrid.Height = viewportHeight;
        }
    }

    private void ApplyHexGroupTimeout_Click(object sender, RoutedEventArgs args)
    {
        _viewModel.ApplyHexGroupTimeoutDraft();
    }

    private void HexGroupTimeoutTextBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter)
        {
            return;
        }

        args.Handled = true;
        _viewModel.ApplyHexGroupTimeoutDraft();
    }

    private void SelectLatestEvent_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (_viewModel.SelectLatestEventFromUi())
            {
                ScrollSelectedEventIntoView();
            }
        }
        catch (Exception ex)
        {
            _viewModel.RecordEventSelectionError($"Select latest event click failed: {ex.Message}");
        }
    }

    private void EventListView_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        try
        {
            if (sender is ListView listView)
            {
                _viewModel.SelectEventFromUi(listView.SelectedItem);
            }
        }
        catch (Exception ex)
        {
            _viewModel.RecordEventSelectionError($"Event selection failed: {ex.Message}");
        }
    }

    private void EventListView_ItemClick(object sender, ItemClickEventArgs args)
    {
        try
        {
            if (args.ClickedItem is DetectedEvent)
            {
                _viewModel.SelectEventFromUi(args.ClickedItem);
            }
        }
        catch (Exception ex)
        {
            _viewModel.RecordEventSelectionError($"Event item click failed: {ex.Message}");
        }
    }

    private void EventListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        try
        {
            var detectedEvent = EventListView.SelectedItem as DetectedEvent;
            if (args.OriginalSource is FrameworkElement element &&
                element.DataContext is DetectedEvent sourceEvent)
            {
                detectedEvent = sourceEvent;
            }

            if (detectedEvent is null)
            {
                return;
            }

            EventListView.SelectedItem = detectedEvent;
            SelectEventAndShowContext(detectedEvent);
            args.Handled = true;
        }
        catch (Exception ex)
        {
            _viewModel.RecordEventSelectionError($"Event double-click failed: {ex.Message}");
        }
    }

    private async void SearchResultsListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        try
        {
            var searchResult = SearchResultsListView.SelectedItem as VisibleSearchResult;
            if (args.OriginalSource is FrameworkElement element &&
                element.DataContext is VisibleSearchResult sourceResult)
            {
                searchResult = sourceResult;
            }

            if (searchResult is null)
            {
                return;
            }

            SearchResultsListView.SelectedItem = searchResult;
            await _viewModel.JumpToSearchResultAsync(searchResult);
            args.Handled = true;
        }
        catch (Exception ex)
        {
            _viewModel.RecordXtermSearchError($"Search result jump failed: {ex.Message}");
        }
    }

    private void InspectorTabView_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        try
        {
            _viewModel.SetActiveInspectorTab(GetInspectorTabName(InspectorTabView.SelectedItem));
            if (ReferenceEquals(InspectorTabView.SelectedItem, ContextTabViewItem))
            {
                _viewModel.RecordContextTabActivated();
                _viewModel.RefreshSelectedEventContextForUi();
                InspectorTabView.UpdateLayout();
                ContextTabViewItem.UpdateLayout();
            }

            if (ReferenceEquals(InspectorTabView.SelectedItem, RulesTabViewItem))
            {
                InspectorTabView.UpdateLayout();
                RulesTabViewItem.UpdateLayout();
                QueueRulesTableViewportResize();
            }

            QueueXtermFit();
        }
        catch (Exception ex)
        {
            _viewModel.RecordInspectorTabLayoutError($"Inspector tab layout failed: {ex.Message}");
        }
    }

    private static string GetInspectorTabName(object? selectedItem)
    {
        return selectedItem is TabViewItem tabViewItem
            ? tabViewItem.Header?.ToString() ?? "(unknown)"
            : "(unknown)";
    }

    private void SelectEventAndShowContext(DetectedEvent detectedEvent)
    {
        _viewModel.SelectEventFromUi(detectedEvent);
        _viewModel.RefreshSelectedEventContextForUi();
        InspectorTabView.SelectedItem = ContextTabViewItem;
        InspectorTabView.UpdateLayout();
        ContextTabViewItem.UpdateLayout();
        DispatcherQueue.TryEnqueue(() =>
        {
            _viewModel.RefreshSelectedEventContextForUi();
        });
    }

    private void QueueEventAutoScrollToLatest(bool selectLatest)
    {
        if (_eventAutoScrollQueued || IsClosingOrClosed)
        {
            return;
        }

        _eventAutoScrollQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _eventAutoScrollQueued = false;
            if (IsClosingOrClosed)
            {
                return;
            }

            if (_isPointerOverEventList)
            {
                return;
            }

            if (_viewModel.IsEventAutoScrollSuppressedByXtermBackpressure)
            {
                _viewModel.RecordXtermBackpressureEventAutoScrollSuppressed();
                return;
            }

            try
            {
                var latestEvent = _viewModel.Events.Events.LastOrDefault();
                if (latestEvent is null)
                {
                    return;
                }

                if (selectLatest)
                {
                    EventListView.SelectedItem = latestEvent;
                    _viewModel.SelectEventFromUi(latestEvent);
                }

                EventListView.ScrollIntoView(latestEvent);
            }
            catch (Exception ex)
            {
                _viewModel.RecordEventListScrollError($"Event auto-scroll failed: {ex.Message}");
            }
        });
    }

    private void EventListView_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        _isPointerOverEventList = true;
    }

    private void EventListView_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        _isPointerOverEventList = false;
    }

    private void ScrollSelectedEventIntoView()
    {
        try
        {
            if (_viewModel.SelectedEvent is null)
            {
                return;
            }

            EventListView.SelectedItem = _viewModel.SelectedEvent;
            EventListView.ScrollIntoView(_viewModel.SelectedEvent);
        }
        catch (Exception ex)
        {
            _viewModel.RecordEventListScrollError($"Event latest scroll failed: {ex.Message}");
        }
    }

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (IsWebViewSource(args.OriginalSource))
        {
            return;
        }

        if (IsSearchFocusShortcut(args.Key))
        {
            args.Handled = true;
            FocusSearchBox("window");
            return;
        }

        if (IsSearchBoxSource(args.OriginalSource))
        {
            return;
        }

        if (IsTextInputSource(args.OriginalSource))
        {
            return;
        }

        if (TryHandleSearchNavigationShortcut(args, "window"))
        {
            return;
        }

        if (IsMarkerShortcut(args.Key))
        {
            args.Handled = true;
            await _viewModel.AddDefaultMarkerAsync();
            return;
        }

        var shortcutText = CreateShortcutText(args.Key);
        if (string.IsNullOrWhiteSpace(shortcutText))
        {
            return;
        }

        args.Handled = true;
        await ExecuteSavedCommandShortcutAsync(shortcutText);
    }

    private async void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (IsSearchFocusShortcut(args.Key))
        {
            args.Handled = true;
            FocusSearchBox("search box");
            return;
        }

        if (args.Key == VirtualKey.Escape && !IsModifierDown(VirtualKey.Control) && !IsModifierDown(VirtualKey.Menu))
        {
            args.Handled = true;
            _viewModel.RecordSearchEscapeShortcut("search box");
            XtermLogWebView.Focus(FocusState.Programmatic);
            return;
        }

        if ((args.Key == VirtualKey.Up || args.Key == VirtualKey.Down) &&
            !IsModifierDown(VirtualKey.Control) &&
            !IsModifierDown(VirtualKey.Menu))
        {
            args.Handled = true;
            if (_viewModel.RecallSearchHistory(args.Key == VirtualKey.Up ? -1 : 1))
            {
                SearchTextBox.Select(SearchTextBox.Text.Length, 0);
            }

            return;
        }

        if (TryHandleSearchNavigationShortcut(args, "search box"))
        {
            return;
        }

        if (args.Key != VirtualKey.Enter || IsModifierDown(VirtualKey.Control) || IsModifierDown(VirtualKey.Menu))
        {
            return;
        }

        args.Handled = true;
        if (IsModifierDown(VirtualKey.Shift))
        {
            await _viewModel.FindPreviousFromShortcutAsync("search box");
            return;
        }

        await _viewModel.FindNextFromShortcutAsync("search box");
    }

    private async Task HandleSearchShortcutAsync(string? action, string source)
    {
        if (IsClosingOrClosed)
        {
            return;
        }

        switch (action)
        {
            case "focus":
                FocusSearchBox(source);
                break;
            case "previous":
                await _viewModel.FindPreviousFromShortcutAsync(source);
                break;
            case "next":
                await _viewModel.FindNextFromShortcutAsync(source);
                break;
            default:
                _viewModel.RecordXtermSearchError($"Unknown search shortcut action: {action ?? "(null)"}");
                break;
        }
    }

    private bool TryHandleSearchNavigationShortcut(KeyRoutedEventArgs args, string source)
    {
        if (args.Key != VirtualKey.F3 || IsModifierDown(VirtualKey.Control) || IsModifierDown(VirtualKey.Menu))
        {
            return false;
        }

        args.Handled = true;
        if (IsModifierDown(VirtualKey.Shift))
        {
            _ = _viewModel.FindPreviousFromShortcutAsync(source);
            return true;
        }

        _ = _viewModel.FindNextFromShortcutAsync(source);
        return true;
    }

    private void FocusSearchBox(string source)
    {
        SearchTextBox.Focus(FocusState.Programmatic);
        SearchTextBox.SelectAll();
        _viewModel.RecordSearchFocusShortcut(source);
    }

    private async void BrowseLogFolder_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add("*");

            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

            var folder = await picker.PickSingleFolderAsync();
            _viewModel.SetLogSaveDirectoryFromPicker(folder?.Path);
        }
        catch (Exception ex)
        {
            _viewModel.RecordSaveDirectoryBrowseError($"Browse log folder failed: {ex.Message}");
        }
    }

    private async void BrowseCuteBackgroundImage_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".webp");

            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

            var file = await picker.PickSingleFileAsync();
            _viewModel.SetCuteBackgroundImagePathFromPicker(file?.Path);
        }
        catch (Exception ex)
        {
            _viewModel.RecordCuteBackgroundLoadResult(fileExists: false, loaded: false, $"Browse background image failed: {ex.Message}");
        }
    }

    private void CommandHistoryFlyout_Opening(object sender, object args)
    {
        CommandHistoryEmptyText.Visibility = _viewModel.Commands.CommandHistory.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_viewModel.Commands.CommandHistory.Count > 0 && CommandHistoryListView.SelectedItem is null)
        {
            CommandHistoryListView.SelectedIndex = 0;
        }
    }

    private void CommandHistoryListView_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is CommandHistoryEntry entry)
        {
            SelectHistoryCommand(entry);
        }
    }

    private async void CommandHistoryListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        await SendSelectedHistoryCommandAsync();
    }

    private async void CommandHistoryListView_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter)
        {
            args.Handled = true;
            await SendSelectedHistoryCommandAsync();
        }
    }

    private async void SendSelectedHistoryCommand_Click(object sender, RoutedEventArgs args)
    {
        await SendSelectedHistoryCommandAsync();
    }

    private void ClearCommandHistory_Click(object sender, RoutedEventArgs args)
    {
        _viewModel.ClearCommandHistory();
        CommandHistoryListView.SelectedItem = null;
        CommandHistoryEmptyText.Visibility = Visibility.Visible;
    }

    private void SelectHistoryCommand(CommandHistoryEntry entry)
    {
        _viewModel.SelectCommandHistoryEntry(entry);
        CommandTextBox.Focus(FocusState.Programmatic);
        CommandTextBox.SelectionStart = CommandTextBox.Text.Length;
    }

    private async Task SendSelectedHistoryCommandAsync()
    {
        if (CommandHistoryListView.SelectedItem is not CommandHistoryEntry entry)
        {
            return;
        }

        await _viewModel.SendCommandHistoryEntryAsync(entry);
        CommandHistoryFlyout.Hide();
        CommandTextBox.Focus(FocusState.Programmatic);
        CommandTextBox.SelectionStart = CommandTextBox.Text.Length;
    }

    private async Task<bool> ExecuteSavedCommandShortcutAsync(string? shortcutText)
    {
        if (string.IsNullOrWhiteSpace(shortcutText))
        {
            return false;
        }

        return await _viewModel.SendSavedCommandShortcutAsync(shortcutText);
    }

    private async void AddLogRule_Click(object sender, RoutedEventArgs args)
    {
        var rule = await ShowLogRuleDialogAsync(
            "Add log rule",
            new LogRule
            {
                UseForEvent = false,
                UseForHighlight = true,
                UseAsViewFilter = false,
                ForegroundColor = "Green",
                Priority = 10,
                MatchDirection = HighlightMatchDirection.RxOnly
            });
        if (rule is not null)
        {
            _viewModel.AddLogRule(rule);
        }
    }

    private async Task EditSelectedLogRuleAsync()
    {
        if (_viewModel.SelectedLogRule is null)
        {
            return;
        }

        var rule = await ShowLogRuleDialogAsync("Edit log rule", CloneLogRule(_viewModel.SelectedLogRule));
        if (rule is not null)
        {
            _viewModel.ReplaceSelectedLogRule(rule);
        }
    }

    private async Task DeleteSelectedLogRuleAsync()
    {
        if (_viewModel.SelectedLogRule is null)
        {
            return;
        }

        if (await ConfirmDeleteAsync("Delete log rule", $"Delete log rule '{_viewModel.SelectedLogRule.Name}'?"))
        {
            _viewModel.DeleteSelectedLogRule();
        }
    }

    private async void EditLogRuleRow_Click(object sender, RoutedEventArgs args)
    {
        SelectLogRuleFromSender(sender);
        await EditSelectedLogRuleAsync();
    }

    private async void DeleteLogRuleRow_Click(object sender, RoutedEventArgs args)
    {
        SelectLogRuleFromSender(sender);
        await DeleteSelectedLogRuleAsync();
    }

    private void LogRuleInlineClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: LogRule rule })
        {
            if (sender is CheckBox checkBox)
            {
                switch (checkBox.Tag?.ToString())
                {
                    case "Case":
                        rule.CaseSensitive = checkBox.IsChecked == true;
                        break;
                    case "Event":
                        rule.UseForEvent = checkBox.IsChecked == true;
                        break;
                    case "Highlight":
                        rule.UseForHighlight = checkBox.IsChecked == true;
                        break;
                    case "Filter":
                        rule.UseAsViewFilter = checkBox.IsChecked == true;
                        break;
                    default:
                        rule.Enabled = checkBox.IsChecked == true;
                        break;
                }
            }

            _viewModel.SelectedLogRule = rule;
            _viewModel.ApplyLogRuleChangesFromUi($"Updated log rule: {rule.Name}");
        }
    }

    private void LogRuleColorButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button || button.DataContext is not LogRule rule)
        {
            _viewModel.RecordRuleColorChangeError("Color menu could not identify the selected log rule.");
            return;
        }

        _viewModel.SelectedLogRule = rule;
        LogRuleListView.SelectedItem = rule;

        var flyout = new MenuFlyout();
        foreach (var color in _viewModel.HighlightColorPresets)
        {
            var item = new MenuFlyoutItem
            {
                Text = color,
                Tag = color,
                Icon = new FontIcon
                {
                    Glyph = "\u25A0",
                    FontSize = 11,
                    Foreground = HighlightColorBrushConverter.CreateBrush(color)
                }
            };

            item.Click += (_, _) =>
            {
                if (item.Tag is string selectedColor)
                {
                    _viewModel.UpdateLogRuleColorFromUi(rule, selectedColor);
                }
            };
            flyout.Items.Add(item);
        }

        flyout.ShowAt(button);
    }

    private void SelectLogRuleFromSender(object sender)
    {
        if (sender is FrameworkElement { DataContext: LogRule rule })
        {
            _viewModel.SelectedLogRule = rule;
            LogRuleListView.SelectedItem = rule;
        }
    }

    private async void AddSavedCommand_Click(object sender, RoutedEventArgs args)
    {
        var command = await ShowSavedCommandDialogAsync("Add saved command", new TxCommand());
        if (command is not null)
        {
            _viewModel.AddSavedCommand(command);
        }
    }

    private async void EditSavedCommand_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel.SelectedSavedCommand is null)
        {
            return;
        }

        var command = await ShowSavedCommandDialogAsync("Edit saved command", CloneTxCommand(_viewModel.SelectedSavedCommand));
        if (command is not null)
        {
            _viewModel.ReplaceSelectedSavedCommand(command);
        }
    }

    private async void DeleteSavedCommand_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel.SelectedSavedCommand is null)
        {
            return;
        }

        if (await ConfirmDeleteAsync("Delete saved command", $"Delete saved command '{_viewModel.SelectedSavedCommand.Name}'?"))
        {
            _viewModel.DeleteSelectedSavedCommand();
        }
    }

    private async void AddCommandSequence_Click(object sender, RoutedEventArgs args)
    {
        var sequence = await ShowCommandSequenceDialogAsync("Add sequence", new CommandSequence());
        if (sequence is not null)
        {
            _viewModel.AddCommandSequence(sequence);
        }
    }

    private async void EditCommandSequence_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel.SelectedCommandSequence is null)
        {
            return;
        }

        var sequence = await ShowCommandSequenceDialogAsync("Edit sequence", CloneCommandSequence(_viewModel.SelectedCommandSequence));
        if (sequence is not null)
        {
            _viewModel.ReplaceSelectedCommandSequence(sequence);
        }
    }

    private async void DeleteCommandSequence_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel.SelectedCommandSequence is null)
        {
            return;
        }

        if (await ConfirmDeleteAsync("Delete sequence", $"Delete sequence '{_viewModel.SelectedCommandSequence.Name}'?"))
        {
            _viewModel.DeleteSelectedCommandSequence();
        }
    }

    private async void AddCommandSequenceStep_Click(object sender, RoutedEventArgs args)
    {
        var step = await ShowCommandSequenceStepDialogAsync(
            "Add sequence step",
            new CommandSequenceStep { DelayAfterMs = 300 });
        if (step is not null)
        {
            _viewModel.AddCommandSequenceStep(step);
        }
    }

    private async void EditCommandSequenceStep_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel.SelectedCommandSequenceStep is null)
        {
            return;
        }

        var step = await ShowCommandSequenceStepDialogAsync("Edit sequence step", CloneCommandSequenceStep(_viewModel.SelectedCommandSequenceStep));
        if (step is not null)
        {
            _viewModel.ReplaceSelectedCommandSequenceStep(step);
        }
    }

    private void EditCommandSequenceStepRow_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: CommandSequenceStep step })
        {
            _viewModel.SelectedCommandSequenceStep = step;
            CommandSequenceStepListView.SelectedItem = step;
        }

        EditCommandSequenceStep_Click(sender, args);
    }

    private async void DeleteCommandSequenceStep_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel.SelectedCommandSequenceStep is null)
        {
            return;
        }

        if (await ConfirmDeleteAsync("Delete sequence step", $"Delete sequence step '{_viewModel.SelectedCommandSequenceStep.DisplayName}'?"))
        {
            _viewModel.DeleteSelectedCommandSequenceStep();
        }
    }

    private void DeleteCommandSequenceStepRow_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: CommandSequenceStep step })
        {
            _viewModel.SelectedCommandSequenceStep = step;
            CommandSequenceStepListView.SelectedItem = step;
        }

        DeleteCommandSequenceStep_Click(sender, args);
    }

    private void MoveCommandSequenceStepUp_Click(object sender, RoutedEventArgs args)
    {
        _viewModel.MoveSelectedCommandSequenceStep(-1);
    }

    private void MoveCommandSequenceStepDown_Click(object sender, RoutedEventArgs args)
    {
        _viewModel.MoveSelectedCommandSequenceStep(1);
    }

    private void CommandSequenceInlineClicked(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: CommandSequence sequence })
        {
            _viewModel.SelectedCommandSequence = sequence;
            _viewModel.ApplyCommandSequenceChangesFromUi($"Updated sequence: {sequence.Name}");
            return;
        }

        if (_viewModel.SelectedCommandSequence is { } selectedSequence)
        {
            _viewModel.ApplyCommandSequenceChangesFromUi($"Updated sequence: {selectedSequence.Name}");
        }
    }

    private async Task<LogRule?> ShowLogRuleDialogAsync(string title, LogRule source)
    {
        var nameBox = CreateDialogTextBox(source.Name, "e.g. RESET");
        var keywordBox = CreateDialogTextBox(source.Keyword, "ERROR or 49 4E");
        var enabledBox = new CheckBox { Content = "Enabled", IsChecked = source.Enabled };
        var eventBox = new CheckBox { Content = "Event", IsChecked = source.UseForEvent };
        var highlightBox = new CheckBox { Content = "Highlight", IsChecked = source.UseForHighlight };
        var filterBox = new CheckBox { Content = "Filter", IsChecked = source.UseAsViewFilter };
        var caseBox = new CheckBox { Content = "Case sensitive", IsChecked = source.CaseSensitive };
        var modeBox = CreateLogRuleModeComboBox(source.Mode);
        var directionBox = CreateEnumComboBox(source.MatchDirection);
        var foregroundBox = CreateStringComboBox(_viewModel.HighlightColorPresets, source.ForegroundColor);
        var backgroundOptions = new[] { "(none)", "Default", "Red", "Yellow", "Magenta", "Cyan", "Green", "Blue", "White", "Gray" };
        var backgroundBox = CreateStringComboBox(backgroundOptions, string.IsNullOrWhiteSpace(source.BackgroundColor) ? "(none)" : source.BackgroundColor);
        var priorityBox = CreateDialogTextBox(source.Priority.ToString(CultureInfo.InvariantCulture), "Priority");
        var trayNotificationBox = new CheckBox { Content = "Tray", IsChecked = source.TrayNotificationEnabled };
        var soundNotificationBox = new CheckBox { Content = "Sound", IsChecked = source.SoundNotificationEnabled };
        var popupNotificationBox = new CheckBox { Content = "Popup", IsChecked = source.PopupNotificationEnabled };
        var notificationCooldownBox = CreateDialogTextBox(
            Math.Clamp(source.NotificationCooldownSeconds, 5, 3_600).ToString(CultureInfo.InvariantCulture),
            "30");
        var errorText = CreateDialogErrorText();

        ToolTipService.SetToolTip(eventBox, "Event: add matching lines to Events.");
        ToolTipService.SetToolTip(highlightBox, "Highlight: color matching lines in the log view.");
        ToolTipService.SetToolTip(filterBox, "Filter: make this rule available in the Filter dropdown.");
        ToolTipService.SetToolTip(modeBox, "The rule runs only in the selected app mode. Terminal matches decoded text; HEX matches raw bytes.");
        ToolTipService.SetToolTip(caseBox, "Case-sensitive Terminal matching. Ignored when Mode is HEX.");
        ToolTipService.SetToolTip(trayNotificationBox, "Windows tray balloon. OFF by default. Events are grouped per rule.");
        ToolTipService.SetToolTip(soundNotificationBox, "Play one Windows alert sound per grouped notification. OFF by default.");
        ToolTipService.SetToolTip(popupNotificationBox, "Show a non-blocking in-app popup for 8 seconds. OFF by default.");
        ToolTipService.SetToolTip(notificationCooldownBox, "Seconds between notifications for this rule (5-3600). Default: 30 seconds.");

        foreach (var input in new FrameworkElement[]
                 {
                     nameBox, keywordBox, modeBox, directionBox, foregroundBox,
                     backgroundBox, priorityBox, notificationCooldownBox
                 })
        {
            input.MinWidth = 0;
            input.HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        var panel = new Grid
        {
            MinWidth = 430,
            ColumnSpacing = 8,
            RowSpacing = 5
        };
        for (var column = 0; column < 4; column++)
        {
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var row = 0; row < 6; row++)
        {
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        AddDialogGridChild(panel, CreateDialogField("Name", nameBox), row: 0, column: 0, columnSpan: 2);
        AddDialogGridChild(panel, CreateDialogField("Keyword", keywordBox), row: 0, column: 2, columnSpan: 2);
        AddDialogGridChild(
            panel,
            CreateInlineDialogRow(enabledBox, eventBox, highlightBox, filterBox, caseBox),
            row: 1,
            column: 0,
            columnSpan: 4);
        AddDialogGridChild(panel, CreateDialogField("Mode", modeBox), row: 2, column: 0);
        AddDialogGridChild(panel, CreateDialogField("Direction", directionBox), row: 2, column: 1);
        AddDialogGridChild(panel, CreateDialogField("Color", foregroundBox), row: 2, column: 2);
        AddDialogGridChild(panel, CreateDialogField("Background", backgroundBox), row: 2, column: 3);
        AddDialogGridChild(panel, CreateDialogField("Priority", priorityBox), row: 3, column: 0, columnSpan: 2);
        AddDialogGridChild(
            panel,
            CreateDialogField("Notify cooldown (sec)", notificationCooldownBox),
            row: 3,
            column: 2,
            columnSpan: 2);
        AddDialogGridChild(
            panel,
            CreateInlineDialogRow(trayNotificationBox, soundNotificationBox, popupNotificationBox),
            row: 4,
            column: 0,
            columnSpan: 4);
        AddDialogGridChild(panel, errorText, row: 5, column: 0, columnSpan: 4);

        var result = await ShowValidatedEditorDialogAsync(title, panel, clickArgs =>
        {
            if (string.IsNullOrWhiteSpace(keywordBox.Text))
            {
                errorText.Text = "Keyword is required.";
                errorText.Visibility = Visibility.Visible;
                clickArgs.Cancel = true;
                return;
            }

            if (!int.TryParse(notificationCooldownBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cooldown) ||
                cooldown is < 5 or > 3_600)
            {
                errorText.Text = "Notify cooldown must be between 5 and 3600 seconds.";
                errorText.Visibility = Visibility.Visible;
                clickArgs.Cancel = true;
            }
        });
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        var priority = int.TryParse(priorityBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPriority)
            ? parsedPriority
            : source.Priority;
        var background = GetSelectedString(backgroundBox, "(none)");

        return new LogRule
        {
            Name = nameBox.Text,
            Keyword = keywordBox.Text,
            Enabled = enabledBox.IsChecked == true,
            UseForEvent = eventBox.IsChecked == true,
            UseForHighlight = highlightBox.IsChecked == true,
            UseAsViewFilter = filterBox.IsChecked == true,
            CaseSensitive = caseBox.IsChecked == true,
            Mode = GetSelectedLogRuleMode(modeBox),
            MatchDirection = directionBox.SelectedItem is HighlightMatchDirection direction ? direction : HighlightMatchDirection.Both,
            ForegroundColor = GetSelectedString(foregroundBox, "Default"),
            BackgroundColor = background == "(none)" ? null : background,
            Priority = priority,
            TrayNotificationEnabled = trayNotificationBox.IsChecked == true,
            SoundNotificationEnabled = soundNotificationBox.IsChecked == true,
            PopupNotificationEnabled = popupNotificationBox.IsChecked == true,
            NotificationCooldownSeconds = int.Parse(notificationCooldownBox.Text, CultureInfo.InvariantCulture)
        };
    }

    private async Task<EventRule?> ShowEventRuleDialogAsync(string title, EventRule source)
    {
        var nameBox = CreateDialogTextBox(source.Name, "e.g. ERROR");
        var keywordBox = CreateDialogTextBox(source.Keyword, "ERROR or 49 4E");
        var enabledBox = new CheckBox { Content = "Enabled", IsChecked = source.Enabled };
        var caseBox = new CheckBox { Content = "Case sensitive", IsChecked = source.CaseSensitive };
        var modeBox = CreateLogRuleModeComboBox(source.Mode);
        var directionBox = CreateEnumComboBox(source.MatchDirection);
        var trayNotificationBox = new CheckBox { Content = "Tray", IsChecked = source.TrayNotificationEnabled };
        var soundNotificationBox = new CheckBox { Content = "Sound", IsChecked = source.SoundNotificationEnabled };
        var popupNotificationBox = new CheckBox { Content = "Popup", IsChecked = source.PopupNotificationEnabled };
        var notificationCooldownBox = CreateDialogTextBox(
            Math.Clamp(source.NotificationCooldownSeconds, 5, 3_600).ToString(CultureInfo.InvariantCulture),
            "30");
        var errorText = CreateDialogErrorText();
        ToolTipService.SetToolTip(modeBox, "The rule runs only in the selected app mode. Terminal matches decoded text; HEX matches raw bytes.");
        ToolTipService.SetToolTip(caseBox, "Case-sensitive Terminal matching. Ignored when Mode is HEX.");
        ToolTipService.SetToolTip(notificationCooldownBox, "Seconds between notifications for this rule (5-3600). Default: 30 seconds.");

        var panel = CreateDialogPanel();
        panel.Children.Add(CreateDialogField("Name", nameBox));
        panel.Children.Add(CreateDialogField("Keyword", keywordBox));
        panel.Children.Add(CreateDialogField("Mode", modeBox));
        panel.Children.Add(CreateDialogField("Direction", directionBox));
        panel.Children.Add(CreateInlineDialogRow(enabledBox, caseBox));
        panel.Children.Add(CreateInlineDialogRow(trayNotificationBox, soundNotificationBox, popupNotificationBox));
        panel.Children.Add(CreateDialogField("Notify cooldown (sec)", notificationCooldownBox));
        panel.Children.Add(errorText);

        var result = await ShowValidatedEditorDialogAsync(title, panel, clickArgs =>
        {
            if (string.IsNullOrWhiteSpace(keywordBox.Text))
            {
                errorText.Text = "Keyword is required.";
                errorText.Visibility = Visibility.Visible;
                clickArgs.Cancel = true;
                return;
            }

            if (!int.TryParse(notificationCooldownBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cooldown) ||
                cooldown is < 5 or > 3_600)
            {
                errorText.Text = "Notify cooldown must be between 5 and 3600 seconds.";
                errorText.Visibility = Visibility.Visible;
                clickArgs.Cancel = true;
            }
        });
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        return new EventRule
        {
            Name = nameBox.Text,
            Keyword = keywordBox.Text,
            Enabled = enabledBox.IsChecked == true,
            CaseSensitive = caseBox.IsChecked == true,
            Mode = GetSelectedLogRuleMode(modeBox),
            MatchDirection = directionBox.SelectedItem is EventMatchDirection direction ? direction : EventMatchDirection.RxOnly,
            HighlightColor = source.HighlightColor,
            TrayNotificationEnabled = trayNotificationBox.IsChecked == true,
            SoundNotificationEnabled = soundNotificationBox.IsChecked == true,
            PopupNotificationEnabled = popupNotificationBox.IsChecked == true,
            NotificationCooldownSeconds = int.Parse(notificationCooldownBox.Text, CultureInfo.InvariantCulture)
        };
    }

    private async Task<HighlightRule?> ShowHighlightRuleDialogAsync(string title, HighlightRule source)
    {
        var nameBox = CreateDialogTextBox(source.Name, "e.g. WARN");
        var keywordBox = CreateDialogTextBox(source.Keyword, "ERROR or 49 4E");
        var enabledBox = new CheckBox { Content = "Enabled", IsChecked = source.Enabled };
        var caseBox = new CheckBox { Content = "Case sensitive", IsChecked = source.CaseSensitive };
        var filterBox = new CheckBox { Content = "View filter", IsChecked = source.UseAsViewFilter };
        ToolTipService.SetToolTip(filterBox, "Make this rule available in the xterm visible filter selector.");
        ToolTipService.SetToolTip(caseBox, "Case-sensitive Terminal matching. Ignored when Mode is HEX.");
        var foregroundBox = CreateStringComboBox(_viewModel.HighlightColorPresets, source.ForegroundColor);
        var backgroundOptions = new[] { "(none)", "Default", "Red", "Yellow", "Magenta", "Cyan", "Green", "Blue", "White", "Gray" };
        var backgroundBox = CreateStringComboBox(backgroundOptions, string.IsNullOrWhiteSpace(source.BackgroundColor) ? "(none)" : source.BackgroundColor);
        var priorityBox = CreateDialogTextBox(source.Priority.ToString(CultureInfo.InvariantCulture), "Priority");
        var modeBox = CreateLogRuleModeComboBox(source.Mode);
        ToolTipService.SetToolTip(modeBox, "The rule runs only in the selected app mode. Terminal matches decoded text; HEX matches raw bytes.");
        var directionBox = CreateEnumComboBox(source.MatchDirection);
        var errorText = CreateDialogErrorText();
        var panel = CreateDialogPanel();
        panel.Children.Add(CreateDialogField("Name", nameBox));
        panel.Children.Add(CreateDialogField("Keyword", keywordBox));
        panel.Children.Add(CreateInlineDialogRow(enabledBox, caseBox, filterBox));
        panel.Children.Add(CreateDialogField("Mode", modeBox));
        panel.Children.Add(CreateDialogField("Foreground", foregroundBox));
        panel.Children.Add(CreateDialogField("Background", backgroundBox));
        panel.Children.Add(CreateDialogField("Priority", priorityBox));
        panel.Children.Add(CreateDialogField("Direction", directionBox));
        panel.Children.Add(errorText);

        var result = await ShowValidatedEditorDialogAsync(title, panel, clickArgs =>
        {
            if (string.IsNullOrWhiteSpace(keywordBox.Text))
            {
                errorText.Text = "Keyword is required.";
                errorText.Visibility = Visibility.Visible;
                clickArgs.Cancel = true;
            }
        });
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        var priority = int.TryParse(priorityBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPriority)
            ? parsedPriority
            : source.Priority;
        var background = GetSelectedString(backgroundBox, "(none)");

        return new HighlightRule
        {
            Name = nameBox.Text,
            Keyword = keywordBox.Text,
            Enabled = enabledBox.IsChecked == true,
            CaseSensitive = caseBox.IsChecked == true,
            Mode = GetSelectedLogRuleMode(modeBox),
            UseAsViewFilter = filterBox.IsChecked == true,
            ForegroundColor = GetSelectedString(foregroundBox, "Default"),
            BackgroundColor = background == "(none)" ? null : background,
            Priority = priority,
            MatchDirection = directionBox.SelectedItem is HighlightMatchDirection direction ? direction : HighlightMatchDirection.Both
        };
    }

    private async Task<TxCommand?> ShowSavedCommandDialogAsync(string title, TxCommand source)
    {
        var nameBox = CreateDialogTextBox(source.Name, "e.g. reboot");
        var commandBox = CreateDialogTextBox(source.CommandText, "ls / settings get / 41 09 42");
        var endingOptions = new[] { "Global", "None", "CR", "LF", "CRLF" };
        var endingBox = CreateStringComboBox(endingOptions, source.LineEndingMode?.ToString() ?? "Global");
        ToolTipService.SetToolTip(endingBox, LineEndingHelpText);
        var shortcutBox = CreateDialogTextBox(source.OptionalShortcut ?? string.Empty, "Ctrl+1 or Alt+S");
        var errorText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.WrapWholeWords,
            Visibility = Visibility.Collapsed
        };

        var panel = CreateDialogPanel();
        panel.Children.Add(CreateDialogField("Name", nameBox));
        panel.Children.Add(CreateDialogField("Command", commandBox));
        panel.Children.Add(CreateDialogField("Line ending", endingBox));
        panel.Children.Add(CreateDialogHint(LineEndingHelpText));
        panel.Children.Add(CreateDialogField("Shortcut (Ctrl+digit or Alt+key)", shortcutBox));
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (dialogSender, clickArgs) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                errorText.Text = "Name is required.";
                errorText.Visibility = Visibility.Visible;
                clickArgs.Cancel = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(commandBox.Text))
            {
                errorText.Text = "Command text is required.";
                errorText.Visibility = Visibility.Visible;
                clickArgs.Cancel = true;
                return;
            }

            if (!MainViewModel.TryNormalizeSavedCommandShortcut(shortcutBox.Text, out var ignoredShortcut, out var shortcutError))
            {
                errorText.Text = shortcutError;
                errorText.Visibility = Visibility.Visible;
                clickArgs.Cancel = true;
            }
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        MainViewModel.TryNormalizeSavedCommandShortcut(shortcutBox.Text, out var normalizedShortcut, out _);
        return new TxCommand(nameBox.Text, commandBox.Text)
        {
            LineEndingMode = ParseOptionalTxLineEnding(GetSelectedString(endingBox, "Global")),
            OptionalShortcut = normalizedShortcut
        };
    }

    private async Task<CommandSequence?> ShowCommandSequenceDialogAsync(string title, CommandSequence source)
    {
        var nameBox = CreateDialogTextBox(source.Name, "e.g. Boot Check");
        var enabledBox = new CheckBox { Content = "Enabled", IsChecked = source.Enabled };
        var errorText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.WrapWholeWords,
            Visibility = Visibility.Collapsed
        };

        var panel = CreateDialogPanel();
        panel.Children.Add(CreateDialogField("Name", nameBox));
        panel.Children.Add(enabledBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, clickArgs) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                errorText.Text = "Name is required.";
                errorText.Visibility = Visibility.Visible;
                clickArgs.Cancel = true;
            }
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        var sequence = CloneCommandSequence(source);
        sequence.Name = nameBox.Text;
        sequence.Enabled = enabledBox.IsChecked == true;
        return sequence;
    }

    private async Task<CommandSequenceStep?> ShowCommandSequenceStepDialogAsync(string title, CommandSequenceStep source)
    {
        var nameBox = CreateDialogTextBox(source.Name ?? string.Empty, "Optional step name");
        var commandBox = CreateDialogTextBox(source.CommandText, "ls / settings get / 41 09 42");
        var endingOptions = new[] { "Global", "None", "CR", "LF", "CRLF" };
        var endingBox = CreateStringComboBox(endingOptions, source.LineEndingMode?.ToString() ?? "Global");
        ToolTipService.SetToolTip(endingBox, LineEndingHelpText);
        var delayBox = CreateDialogTextBox(source.DelayAfterMs.ToString(CultureInfo.InvariantCulture), "Delay after ms");
        var commentBox = CreateDialogTextBox(source.Comment ?? string.Empty, "Optional note");
        var errorText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.WrapWholeWords,
            Visibility = Visibility.Collapsed
        };

        var panel = CreateDialogPanel();
        panel.Children.Add(CreateDialogField("Name", nameBox));
        panel.Children.Add(CreateDialogField("Command", commandBox));
        panel.Children.Add(CreateDialogField("Line ending", endingBox));
        panel.Children.Add(CreateDialogHint(LineEndingHelpText));
        panel.Children.Add(CreateDialogField("Delay after ms", delayBox));
        panel.Children.Add(CreateDialogField("Comment", commentBox));
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, clickArgs) =>
        {
            if (string.IsNullOrWhiteSpace(commandBox.Text))
            {
                errorText.Text = "Command text is required.";
                errorText.Visibility = Visibility.Visible;
                clickArgs.Cancel = true;
                return;
            }

            if (!int.TryParse(delayBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delay) ||
                delay is < 0 or > 600_000)
            {
                errorText.Text = "Delay must be between 0 and 600,000 ms.";
                errorText.Visibility = Visibility.Visible;
                clickArgs.Cancel = true;
            }
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        var parsedDelay = int.TryParse(delayBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delayAfterMs)
            ? delayAfterMs
            : source.DelayAfterMs;
        return new CommandSequenceStep
        {
            Name = string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text,
            CommandText = commandBox.Text,
            LineEndingMode = ParseOptionalTxLineEnding(GetSelectedString(endingBox, "Global")),
            DelayAfterMs = parsedDelay,
            Comment = string.IsNullOrWhiteSpace(commentBox.Text) ? null : commentBox.Text
        };
    }

    private async Task<ContentDialogResult> ShowEditorDialogAsync(string title, object content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        return await dialog.ShowAsync();
    }

    private async Task<ContentDialogResult> ShowValidatedEditorDialogAsync(
        string title,
        object content,
        Action<ContentDialogButtonClickEventArgs> validatePrimaryClick)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, clickArgs) => validatePrimaryClick(clickArgs);

        return await dialog.ShowAsync();
    }

    private async Task<bool> ConfirmDeleteAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.WrapWholeWords
            },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static StackPanel CreateDialogPanel()
    {
        return new StackPanel
        {
            Spacing = 6,
            MinWidth = 320
        };
    }

    private static TextBox CreateDialogTextBox(string text, string placeholder)
    {
        return new TextBox
        {
            Text = text,
            PlaceholderText = placeholder,
            MinWidth = 280
        };
    }

    private static ComboBox CreateEnumComboBox<T>(T selected)
        where T : struct, Enum
    {
        return new ComboBox
        {
            ItemsSource = Enum.GetValues<T>(),
            SelectedItem = selected,
            MinWidth = 180
        };
    }

    private static ComboBox CreateLogRuleModeComboBox(LogRuleMatchMode selected)
    {
        var textItem = new ComboBoxItem
        {
            Content = "Terminal",
            Tag = LogRuleMatchMode.Terminal
        };
        var hexItem = new ComboBoxItem
        {
            Content = "HEX",
            Tag = LogRuleMatchMode.Hex
        };
        var comboBox = new ComboBox
        {
            MinWidth = 180
        };
        comboBox.Items.Add(textItem);
        comboBox.Items.Add(hexItem);
        comboBox.SelectedItem = selected == LogRuleMatchMode.Hex
            ? hexItem
            : textItem;
        return comboBox;
    }

    private static LogRuleMatchMode GetSelectedLogRuleMode(ComboBox comboBox)
    {
        return comboBox.SelectedItem is ComboBoxItem { Tag: LogRuleMatchMode mode }
            ? mode
            : LogRuleMatchMode.Terminal;
    }

    private static ComboBox CreateStringComboBox(IEnumerable<string> items, string? selected)
    {
        var values = items.ToArray();
        return new ComboBox
        {
            ItemsSource = values,
            SelectedItem = values.FirstOrDefault(value => string.Equals(value, selected, StringComparison.OrdinalIgnoreCase)) ?? values.FirstOrDefault(),
            MinWidth = 180
        };
    }

    private static FrameworkElement CreateDialogField(string label, FrameworkElement input)
    {
        var panel = new StackPanel
        {
            Spacing = 2
        };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12
        });
        panel.Children.Add(input);
        return panel;
    }

    private static FrameworkElement CreateDialogHint(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 11,
            Opacity = 0.82,
            TextWrapping = TextWrapping.WrapWholeWords
        };
    }

    private static TextBlock CreateDialogErrorText()
    {
        return new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.WrapWholeWords,
            Visibility = Visibility.Collapsed
        };
    }

    private static FrameworkElement CreateInlineDialogRow(params UIElement[] children)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12
        };
        foreach (var child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    private static void AddDialogGridChild(
        Grid grid,
        FrameworkElement child,
        int row,
        int column,
        int columnSpan = 1)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, column);
        Grid.SetColumnSpan(child, columnSpan);
        grid.Children.Add(child);
    }

    private static string GetSelectedString(ComboBox comboBox, string fallback)
    {
        return comboBox.SelectedItem as string ?? fallback;
    }

    private static TxLineEndingMode? ParseOptionalTxLineEnding(string value)
    {
        if (string.Equals(value, "Global", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Enum.TryParse<TxLineEndingMode>(value, ignoreCase: true, out var mode)
            ? mode
            : null;
    }

    private static string? CreateShortcutText(VirtualKey key)
    {
        var isCtrlDown = IsModifierDown(VirtualKey.Control);
        var isAltDown = IsModifierDown(VirtualKey.Menu);
        var isShiftDown = IsModifierDown(VirtualKey.Shift);

        if (isShiftDown)
        {
            return null;
        }

        if (isCtrlDown && !isAltDown && TryGetDigitKeyText(key, out var digitKey))
        {
            return $"Ctrl+{digitKey}";
        }

        if (isAltDown && !isCtrlDown && TryGetLetterOrDigitKeyText(key, out var altKey))
        {
            return $"Alt+{altKey}";
        }

        return null;
    }

    private static bool IsMarkerShortcut(VirtualKey key)
    {
        return key == VirtualKey.M &&
            IsModifierDown(VirtualKey.Control) &&
            !IsModifierDown(VirtualKey.Menu) &&
            !IsModifierDown(VirtualKey.Shift);
    }

    private static bool IsSearchFocusShortcut(VirtualKey key)
    {
        return key == VirtualKey.F &&
            IsModifierDown(VirtualKey.Control) &&
            !IsModifierDown(VirtualKey.Menu) &&
            !IsModifierDown(VirtualKey.Shift);
    }

    private static bool IsModifierDown(VirtualKey key)
    {
        return (GetKeyState((int)key) & 0x8000) != 0;
    }

    private static bool TryGetDigitKeyText(VirtualKey key, out char digit)
    {
        if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9)
        {
            digit = (char)('0' + (key - VirtualKey.Number0));
            return true;
        }

        if (key is >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9)
        {
            digit = (char)('0' + (key - VirtualKey.NumberPad0));
            return true;
        }

        digit = '\0';
        return false;
    }

    private static bool TryGetLetterOrDigitKeyText(VirtualKey key, out char keyText)
    {
        if (TryGetDigitKeyText(key, out keyText))
        {
            return true;
        }

        if (key is >= VirtualKey.A and <= VirtualKey.Z)
        {
            keyText = (char)('A' + (key - VirtualKey.A));
            return true;
        }

        keyText = '\0';
        return false;
    }

    private static bool IsTextInputSource(object? source)
    {
        return HasAncestor<TextBox>(source) ||
            HasAncestor<PasswordBox>(source) ||
            HasAncestor<RichEditBox>(source);
    }

    private bool IsSearchBoxSource(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (ReferenceEquals(current, SearchTextBox))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current) ??
                (current as FrameworkElement)?.Parent as DependencyObject;
        }

        return false;
    }

    private static bool IsWebViewSource(object? source)
    {
        return HasAncestor<WebView2>(source);
    }

    private static bool HasAncestor<T>(object? source)
        where T : DependencyObject
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is T)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current) ??
                (current as FrameworkElement)?.Parent as DependencyObject;
        }

        return false;
    }

    private static EventRule CloneEventRule(EventRule rule)
    {
        return new EventRule
        {
            Name = rule.Name,
            Keyword = rule.Keyword,
            Enabled = rule.Enabled,
            CaseSensitive = rule.CaseSensitive,
            Mode = rule.Mode,
            MatchDirection = rule.MatchDirection,
            HighlightColor = rule.HighlightColor,
            TrayNotificationEnabled = rule.TrayNotificationEnabled,
            SoundNotificationEnabled = rule.SoundNotificationEnabled,
            PopupNotificationEnabled = rule.PopupNotificationEnabled,
            NotificationCooldownSeconds = rule.NotificationCooldownSeconds
        };
    }

    private static LogRule CloneLogRule(LogRule rule)
    {
        return new LogRule
        {
            Name = rule.Name,
            Keyword = rule.Keyword,
            Enabled = rule.Enabled,
            UseForEvent = rule.UseForEvent,
            UseForHighlight = rule.UseForHighlight,
            UseAsViewFilter = rule.UseAsViewFilter,
            CaseSensitive = rule.CaseSensitive,
            Mode = rule.Mode,
            MatchDirection = rule.MatchDirection,
            ForegroundColor = rule.ForegroundColor,
            BackgroundColor = rule.BackgroundColor,
            Priority = rule.Priority,
            TrayNotificationEnabled = rule.TrayNotificationEnabled,
            SoundNotificationEnabled = rule.SoundNotificationEnabled,
            PopupNotificationEnabled = rule.PopupNotificationEnabled,
            NotificationCooldownSeconds = rule.NotificationCooldownSeconds
        };
    }

    private static HighlightRule CloneHighlightRule(HighlightRule rule)
    {
        return new HighlightRule
        {
            Name = rule.Name,
            Keyword = rule.Keyword,
            Enabled = rule.Enabled,
            CaseSensitive = rule.CaseSensitive,
            Mode = rule.Mode,
            UseAsViewFilter = rule.UseAsViewFilter,
            ForegroundColor = rule.ForegroundColor,
            BackgroundColor = rule.BackgroundColor,
            Priority = rule.Priority,
            MatchDirection = rule.MatchDirection
        };
    }

    private static TxCommand CloneTxCommand(TxCommand command)
    {
        return new TxCommand(command.Name, command.CommandText)
        {
            LineEndingMode = command.LineEndingMode,
            OptionalShortcut = command.OptionalShortcut
        };
    }

    private static CommandSequence CloneCommandSequence(CommandSequence sequence)
    {
        return new CommandSequence
        {
            Name = sequence.Name,
            Enabled = sequence.Enabled,
            Steps = new System.Collections.ObjectModel.ObservableCollection<CommandSequenceStep>(
                sequence.Steps.Select(CloneCommandSequenceStep))
        };
    }

    private static CommandSequenceStep CloneCommandSequenceStep(CommandSequenceStep step)
    {
        return new CommandSequenceStep
        {
            Name = step.Name,
            CommandText = step.CommandText,
            LineEndingMode = step.LineEndingMode,
            DelayAfterMs = step.DelayAfterMs,
            Comment = step.Comment
        };
    }

    private void CommandTextBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (IsModifierDown(VirtualKey.Control) || IsModifierDown(VirtualKey.Menu))
        {
            return;
        }

        if (args.Key == VirtualKey.Up)
        {
            args.Handled = _viewModel.NavigateCommandHistory(direction: -1);
            if (args.Handled)
            {
                CommandTextBox.SelectionStart = CommandTextBox.Text.Length;
            }

            return;
        }

        if (args.Key == VirtualKey.Down)
        {
            args.Handled = _viewModel.NavigateCommandHistory(direction: 1);
            if (args.Handled)
            {
                CommandTextBox.SelectionStart = CommandTextBox.Text.Length;
            }

            return;
        }

        if (args.Key == VirtualKey.Enter && _viewModel.SendCommand.CanExecute(null))
        {
            args.Handled = true;
            _viewModel.SendCommand.Execute(null);
        }
    }
}
