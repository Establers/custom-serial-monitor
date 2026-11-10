using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Views;

public sealed partial class ProtocolView : UserControl
{
    private DispatcherQueueTimer? _timer;
    private TabView? _tabs;
    private TabViewItem? _tab;
    private bool _isNarrow;
    private ProtocolViewModel? _attachedModel;
    private bool _latestScrollQueued;
    private int _viewGeneration;
    private ProtocolViewModel? Model => DataContext as ProtocolViewModel;
    public ProtocolView()
    {
        InitializeComponent();
        PacketList.AddHandler(PointerPressedEvent, new PointerEventHandler(PacketList_PointerPressed), true);
        PacketList.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(PacketList_PointerWheelChanged), true);
        PacketList.AddHandler(KeyDownEvent, new KeyEventHandler(PacketList_KeyDown), true);
    }

    private void View_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        // Stacking needs enough vertical space for two headers and scrollable
        // content. Keep the split layout in short Inspectors, even when narrow.
        var narrow = args.NewSize.Width < 780 && args.NewSize.Height >= 280;
        if (narrow == _isNarrow) return;
        _isNarrow = narrow;
        // Keep both panes scrollable inside the available Inspector height.
        // A fixed minimum here pushes the footer and details out of small panels.
        PacketWorkspace.ColumnDefinitions[0].Width = new GridLength(narrow ? 1 : 3, GridUnitType.Star);
        PacketWorkspace.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        PacketWorkspace.RowDefinitions[0].Height = new GridLength(narrow ? 3 : 1, GridUnitType.Star);
        PacketWorkspace.RowDefinitions[1].Height = narrow ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        PacketWorkspace.ColumnSpacing = narrow ? 0 : 6;
        PacketWorkspace.RowSpacing = narrow ? 6 : 0;
        Grid.SetColumn(DetailsPanel, narrow ? 0 : 1);
        Grid.SetRow(DetailsPanel, narrow ? 1 : 0);
    }

    private void View_Loaded(object sender, RoutedEventArgs args)
    {
        _viewGeneration++;
        _latestScrollQueued = false;
        if (_attachedModel is not null) _attachedModel.LatestRequested -= Model_LatestRequested;
        _attachedModel = Model;
        if (_attachedModel is not null) _attachedModel.LatestRequested += Model_LatestRequested;
        DependencyObject? parent = this;
        while ((parent = VisualTreeHelper.GetParent(parent)) is not null)
        {
            if (parent is TabViewItem tab) _tab = tab;
            if (parent is TabView tabs) { _tabs = tabs; break; }
        }
        _tab ??= _tabs?.TabItems.OfType<TabViewItem>().FirstOrDefault(item => ReferenceEquals(item.Content, this));
        if (_tabs is not null)
        {
            _tabs.SelectionChanged -= Tabs_SelectionChanged;
            _tabs.SelectionChanged += Tabs_SelectionChanged;
        }
        _timer ??= DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        _timer.Tick -= Timer_Tick;
        _timer.Tick += Timer_Tick;
        UpdateVisibility();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateVisibility();
    private void UpdateVisibility()
    {
        var visible = _tabs is null || ReferenceEquals(_tabs.SelectedItem, _tab);
        if (Model is { } model)
        {
            var restoreSelection = visible && !model.IsVisible;
            model.IsVisible = visible;
            if (restoreSelection && model.SelectedFrame is not null && (!model.IsFollowingLatest || model.IsDisplayPaused))
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    if (Model is { IsVisible: true, SelectedFrame: { } selected })
                        PacketList.ScrollIntoView(selected);
                });
        }
        if (visible) _timer?.Start(); else _timer?.Stop();
    }
    private void Timer_Tick(DispatcherQueueTimer sender, object args) => Model?.Refresh();
    private void Clear_Click(object sender, RoutedEventArgs args) => Model?.Clear();
    private void ResetStatistics_Click(object sender, RoutedEventArgs args) => Model?.ResetStatistics();

    private async void BuildPacket_Click(object sender, RoutedEventArgs args)
    {
        if (Model?.SelectedProtocolIndex != 1) return;
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            var dialog = new ModbusPacketBuilderDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("Protocol.PacketBuilder", ex);
            if (Model is { } model) model.ReportPacketBuilderError();
        }
        finally { button.IsEnabled = true; }
    }

    private void Model_LatestRequested(object? sender, EventArgs args)
    {
        if (_latestScrollQueued) return;
        _latestScrollQueued = true;
        var generation = _viewGeneration;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (generation != _viewGeneration) return;
            _latestScrollQueued = false;
            if (Model is { IsVisible: true, IsFollowingLatest: true, IsDisplayPaused: false } model && model.Frames.Count > 0)
                PacketList.ScrollIntoView(model.Frames[^1]);
        })) _latestScrollQueued = false;
    }

    private void PacketList_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (Model is { } model) model.IsFollowingLatest = false;
    }

    private void PacketList_PointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (args.GetCurrentPoint(PacketList).Properties.MouseWheelDelta > 0 && Model is { } model)
            model.IsFollowingLatest = false;
    }

    private void PacketList_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (AppShortcuts.Matches(AppShortcutAction.TextNavigation, args.Key) && Model is { } model)
            model.IsFollowingLatest = false;
    }

    private void FilterFlyout_Opening(object sender, object args)
    {
        FilterEditorPanel.DataContext = Model;
        Model?.BeginFilterEdit();
    }

    private void ApplyFilters_Click(object sender, RoutedEventArgs args) => ApplyFilters();
    private void ApplyFilters()
    {
        if (Model?.TryApplyFilters() == true) FilterFlyout.Hide();
    }
    private void ResetFilters_Click(object sender, RoutedEventArgs args)
    {
        Model?.ResetFilters();
        FilterFlyout.Hide();
    }
    private void FilterInput_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!AppShortcuts.Matches(AppShortcutAction.EditorConfirm, args.Key)) return;
        args.Handled = true;
        ApplyFilters();
    }

    private void View_Unloaded(object sender, RoutedEventArgs args)
    {
        _viewGeneration++;
        _latestScrollQueued = false;
        if (_attachedModel is not null) _attachedModel.LatestRequested -= Model_LatestRequested;
        _attachedModel = null;
        if (_tabs is not null) _tabs.SelectionChanged -= Tabs_SelectionChanged;
        if (_timer is not null) { _timer.Stop(); _timer.Tick -= Timer_Tick; }
        if (Model is { } model) model.IsVisible = false;
        _tabs = null;
        _tab = null;
    }
}
