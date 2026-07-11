using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace SerialMonitor.WinUI.Infrastructure;

public sealed class HighlightColorBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return CreateBrush(value as string);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }

    public static Brush CreateBrush(string? colorName)
    {
        var color = NormalizeColorName(colorName) switch
        {
            "Red" => Color.FromArgb(255, 224, 108, 117),
            "Orange" => Color.FromArgb(255, 209, 154, 102),
            "Yellow" => Color.FromArgb(255, 229, 192, 123),
            "Green" => Color.FromArgb(255, 152, 195, 121),
            "Cyan" => Color.FromArgb(255, 86, 182, 194),
            "Blue" => Color.FromArgb(255, 97, 175, 239),
            "Magenta" => Color.FromArgb(255, 198, 120, 221),
            "White" => Color.FromArgb(255, 240, 240, 240),
            "Gray" => Color.FromArgb(255, 138, 143, 152),
            _ => Color.FromArgb(255, 58, 58, 58)
        };

        return new SolidColorBrush(color);
    }

    private static string NormalizeColorName(string? colorName)
    {
        if (string.IsNullOrWhiteSpace(colorName))
        {
            return "Default";
        }

        var trimmed = colorName.Trim();
        return trimmed.Equals("Grey", StringComparison.OrdinalIgnoreCase)
            ? "Gray"
            : trimmed;
    }
}
