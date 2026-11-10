using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace SerialMonitor.WinUI.Infrastructure;

/// <summary>Connects vertical alignment to the native TextBox content viewport.</summary>
public static class TextBoxContentAlignment
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(TextBoxContentAlignment), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not TextBox input) return;
        input.Loaded -= OnLoaded;
        if (!(bool)args.NewValue) return;
        input.Loaded += OnLoaded;
        if (input.IsLoaded) AlignContent(input);
    }

    private static void OnLoaded(object sender, RoutedEventArgs args) => AlignContent((TextBox)sender);

    private static void AlignContent(TextBox input)
    {
        input.ApplyTemplate();
        // DefaultTextBoxStyle does not bind this property to ContentElement.
        // Keep its native template, focus underline and clear button intact.
        if (FindContent(input) is not { } content) return;
        content.SetBinding(FrameworkElement.VerticalAlignmentProperty, new Binding
        {
            Source = input,
            Path = new PropertyPath(nameof(Control.VerticalContentAlignment))
        });
    }

    private static ScrollViewer? FindContent(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer { Name: "ContentElement" } content) return content;
            if (FindContent(child) is { } nested) return nested;
        }
        return null;
    }
}
