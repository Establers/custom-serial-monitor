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
    private ProtocolViewModel? Model => DataContext as ProtocolViewModel;
    public ProtocolView() => InitializeComponent();

    private void View_Loaded(object sender, RoutedEventArgs args)
    {
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
        if (Model is { } model) model.IsVisible = visible;
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
