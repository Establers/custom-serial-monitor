using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using SerialMonitor.WinUI.Models;
using Windows.UI;

namespace SerialMonitor.WinUI.Infrastructure;

public sealed class EventTextSegmentBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not EventTextSegment { IsMatch: true } segment)
        {
            return null;
        }

        return string.Equals(parameter?.ToString(), "Background", StringComparison.OrdinalIgnoreCase)
            ? CreateBackground(segment.BackgroundColor)
            : CreateForeground(segment.ForegroundColor, segment.BackgroundColor);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }

    private static Brush? CreateBackground(string? colorName)
    {
        return IsDefaultColor(colorName)
            ? null
            : HighlightColorBrushConverter.CreateBrush(colorName);
    }

    private static Brush CreateForeground(string? foregroundColor, string? backgroundColor)
    {
        if (!IsDefaultColor(foregroundColor))
        {
            return HighlightColorBrushConverter.CreateBrush(foregroundColor);
        }

        if (IsDefaultColor(backgroundColor))
        {
            return HighlightColorBrushConverter.CreateBrush("Yellow");
        }

        var useDarkText = backgroundColor!.Trim().ToLowerInvariant() is
            "orange" or "yellow" or "green" or "cyan" or "white";
        return new SolidColorBrush(useDarkText
            ? Color.FromArgb(255, 22, 22, 22)
            : Color.FromArgb(255, 250, 250, 250));
    }

    private static bool IsDefaultColor(string? colorName)
    {
        return string.IsNullOrWhiteSpace(colorName) ||
            colorName.Trim().Equals("Default", StringComparison.OrdinalIgnoreCase) ||
            colorName.Trim().Equals("None", StringComparison.OrdinalIgnoreCase) ||
            colorName.Trim().Equals("(none)", StringComparison.OrdinalIgnoreCase);
    }
}
