using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SerialMonitor.WinUI.Infrastructure;

// Keep compact controls above WinUI's overlay scrollbar, only while overflowing.
public static class HorizontalScrollInset
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(HorizontalScrollInset), new PropertyMetadata(false, OnEnabledChanged));
    private static readonly DependencyProperty QueuedProperty = DependencyProperty.RegisterAttached(
        "Queued", typeof(bool), typeof(HorizontalScrollInset), new PropertyMetadata(false));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ScrollViewer scroll) return;
        scroll.Loaded -= OnLoaded;
        scroll.SizeChanged -= OnSizeChanged;
        scroll.ViewChanged -= OnViewChanged;
        if (args.NewValue is true)
        {
            scroll.Loaded += OnLoaded;
            scroll.SizeChanged += OnSizeChanged;
            scroll.ViewChanged += OnViewChanged;
            QueueInset(scroll);
        }
        else scroll.Padding = new Thickness(0);
    }

    private static void OnLoaded(object sender, RoutedEventArgs args) => QueueInset((ScrollViewer)sender);
    private static void OnSizeChanged(object sender, SizeChangedEventArgs args) => QueueInset((ScrollViewer)sender);
    private static void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (sender is ScrollViewer scroll) QueueInset(scroll);
    }

    private static void QueueInset(ScrollViewer scroll)
    {
        if ((bool)scroll.GetValue(QueuedProperty)) return;
        scroll.SetValue(QueuedProperty, true);
        if (!scroll.DispatcherQueue.TryEnqueue(() =>
        {
            scroll.SetValue(QueuedProperty, false);
            if (!scroll.IsLoaded || !GetEnabled(scroll)) return;
            var padding = new Thickness(0, 0, 0, scroll.ScrollableWidth > 0.5 ? 12 : 0);
            if (scroll.Padding != padding) scroll.Padding = padding;
        })) scroll.SetValue(QueuedProperty, false);
    }
}
