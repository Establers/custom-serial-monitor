using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace SerialMonitor.WinUI.Infrastructure;

public sealed class FileLoggingStateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isEnabled = value is true;
        var color = isEnabled
            ? Color.FromArgb(255, 139, 195, 74)
            : Color.FromArgb(255, 255, 87, 34);

        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
