using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

public static class SearchTextHighlightBehavior
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments",
        typeof(object),
        typeof(SearchTextHighlightBehavior),
        new PropertyMetadata(null, OnSegmentsChanged));

    public static object? GetSegments(DependencyObject element) =>
        element.GetValue(SegmentsProperty);

    public static void SetSegments(DependencyObject element, object? value) =>
        element.SetValue(SegmentsProperty, value);

    private static void OnSegmentsChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not TextBlock textBlock)
        {
            return;
        }

        textBlock.TextHighlighters.Clear();
        if (args.NewValue is not IEnumerable<EventTextSegment> segments)
        {
            return;
        }

        var highlighter = new TextHighlighter
        {
            Foreground = HighlightColorBrushConverter.CreateBrush("Yellow")
        };
        var textOffset = 0;
        foreach (var segment in segments)
        {
            if (segment.IsMatch && segment.Text.Length > 0)
            {
                highlighter.Ranges.Add(new TextRange
                {
                    StartIndex = textOffset,
                    Length = segment.Text.Length
                });
            }

            textOffset += segment.Text.Length;
        }

        if (highlighter.Ranges.Count > 0)
        {
            textBlock.TextHighlighters.Add(highlighter);
        }
    }
}
