using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Views;

public sealed partial class ProtocolView : UserControl
{
    private DispatcherQueueTimer? _timer;
    private TabView? _tabs;
    private TabViewItem? _tab;
    private bool _isNarrow;
    private ProtocolViewModel? Model => DataContext as ProtocolViewModel;
    public ProtocolView() => InitializeComponent();

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
        // Reapply after the ItemsSource is populated, including when the
        // inspector is reparented or reopened with an existing view model.
        if (Model is { } model) WorkspaceSelector.SelectedIndex = model.SelectedWorkspaceIndex;
        DependencyObject? parent = this;
        while ((parent = VisualTreeHelper.GetParent(parent)) is not null)
        {
            if (parent is TabViewItem tab) _tab = tab;
            if (parent is TabView tabs) { _tabs = tabs; break; }
        }
        _tab ??= _tabs?.TabItems.OfType<TabViewItem>().FirstOrDefault(item => ReferenceEquals(item.Content, this));
        if (_tabs is not null) _tabs.SelectionChanged += Tabs_SelectionChanged;
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
            if (restoreSelection && model.SelectedFrame is not null)
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

    private void View_Unloaded(object sender, RoutedEventArgs args)
    {
        if (_tabs is not null) _tabs.SelectionChanged -= Tabs_SelectionChanged;
        if (_timer is not null) { _timer.Stop(); _timer.Tick -= Timer_Tick; }
        if (Model is { } model) model.IsVisible = false;
        _tabs = null;
        _tab = null;
    }
}
