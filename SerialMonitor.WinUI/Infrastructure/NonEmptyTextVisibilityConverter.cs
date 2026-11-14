using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace SerialMonitor.WinUI.Infrastructure;

public sealed class NonEmptyTextVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string text && !string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
