using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace SerialMonitor.WinUI.Infrastructure;

/// <summary>Sets compact header geometry while keeping the native Expander template.</summary>
public static class ExpanderHeaderLayout
{
    public static readonly DependencyProperty PaddingProperty = DependencyProperty.RegisterAttached(
        "Padding", typeof(Thickness), typeof(ExpanderHeaderLayout),
        new PropertyMetadata(new Thickness(16, 0, 0, 0), OnLayoutChanged));

    public static readonly DependencyProperty ChevronButtonSizeProperty = DependencyProperty.RegisterAttached(
        "ChevronButtonSize", typeof(double), typeof(ExpanderHeaderLayout),
        new PropertyMetadata(double.NaN, OnLayoutChanged));

    public static readonly DependencyProperty ChevronMarginProperty = DependencyProperty.RegisterAttached(
        "ChevronMargin", typeof(Thickness), typeof(ExpanderHeaderLayout),
        new PropertyMetadata(new Thickness(4, 0, 4, 0), OnLayoutChanged));

    public static Thickness GetPadding(DependencyObject element) => (Thickness)element.GetValue(PaddingProperty);
    public static void SetPadding(DependencyObject element, Thickness value) => element.SetValue(PaddingProperty, value);
    public static double GetChevronButtonSize(DependencyObject element) => (double)element.GetValue(ChevronButtonSizeProperty);
    public static void SetChevronButtonSize(DependencyObject element, double value) => element.SetValue(ChevronButtonSizeProperty, value);
    public static Thickness GetChevronMargin(DependencyObject element) => (Thickness)element.GetValue(ChevronMarginProperty);
    public static void SetChevronMargin(DependencyObject element, Thickness value) => element.SetValue(ChevronMarginProperty, value);

    private static void OnLayoutChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not Expander expander) return;
        expander.Loaded -= OnLoaded;
        expander.Loaded += OnLoaded;
        if (expander.IsLoaded) ApplyLayout(expander);
    }

    private static void OnLoaded(object sender, RoutedEventArgs args)
    {
        var expander = (Expander)sender;
        ApplyLayout(expander);
        // On first display the header may be materialized after the parent's
        // Loaded event. Reapply once after that layout turn, without polling.
        expander.DispatcherQueue.TryEnqueue(() =>
        {
            if (expander.IsLoaded) ApplyLayout(expander);
        });
    }

    private static void ApplyLayout(Expander expander)
    {
        expander.ApplyTemplate();
        // The native header uses a private StaticResource, so overriding
        // ExpanderHeaderPadding in the view does not change its actual padding.
        if (FindHeader(expander) is not { } header) return;
        header.Padding = GetPadding(expander);
        var size = GetChevronButtonSize(expander);
        if (!double.IsFinite(size) || size <= 0) return;
        header.MinHeight = expander.MinHeight;
        header.FontSize = expander.FontSize;
        // The header's template can still be deferred when the Expander loads.
        // Materialize it before looking for the native chevron border.
        header.ApplyTemplate();
        // Detached flyouts must not depend on a view's resource scope to get
        // the same native chevron dimensions as inline sections.
        if (FindChevron(header) is not { } chevron) return;
        chevron.Width = chevron.Height = size;
        chevron.Margin = GetChevronMargin(expander);
    }

    private static ToggleButton? FindHeader(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ToggleButton { Name: "ExpanderHeader" } header) return header;
            if (child is not Expander && FindHeader(child) is { } nested) return nested;
        }
        return null;
    }

    private static FrameworkElement? FindChevron(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is FrameworkElement { Name: "ExpandCollapseChevronBorder" } chevron) return chevron;
            if (child is not Expander && FindChevron(child) is { } nested) return nested;
        }
        return null;
    }
}
