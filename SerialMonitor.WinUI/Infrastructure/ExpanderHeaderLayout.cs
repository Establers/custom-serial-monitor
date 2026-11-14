using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace SerialMonitor.WinUI.Infrastructure;

/// <summary>Sets header padding while keeping the native Expander template.</summary>
public static class ExpanderHeaderLayout
{
    public static readonly DependencyProperty PaddingProperty = DependencyProperty.RegisterAttached(
        "Padding", typeof(Thickness), typeof(ExpanderHeaderLayout),
        new PropertyMetadata(new Thickness(16, 0, 0, 0), OnPaddingChanged));

    public static Thickness GetPadding(DependencyObject element) => (Thickness)element.GetValue(PaddingProperty);
    public static void SetPadding(DependencyObject element, Thickness value) => element.SetValue(PaddingProperty, value);

    private static void OnPaddingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not Expander expander) return;
        expander.Loaded -= OnLoaded;
        expander.Loaded += OnLoaded;
        if (expander.IsLoaded) ApplyPadding(expander);
    }

    private static void OnLoaded(object sender, RoutedEventArgs args) => ApplyPadding((Expander)sender);

    private static void ApplyPadding(Expander expander)
    {
        expander.ApplyTemplate();
        // The native header uses a private StaticResource, so overriding
        // ExpanderHeaderPadding in the view does not change its actual padding.
        if (FindHeader(expander) is { } header) header.Padding = GetPadding(expander);
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
}
