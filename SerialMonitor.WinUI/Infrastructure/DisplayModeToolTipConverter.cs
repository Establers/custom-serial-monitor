using Microsoft.UI.Xaml.Data;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

public sealed class DisplayModeToolTipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        RxDisplayMode.Terminal => UiText.Get("TerminalModeDescription", "Terminal: Text logs and text commands with the selected line ending."),
        RxDisplayMode.Hex => UiText.Get("HexModeDescription", "HEX: Display and send raw bytes in hexadecimal, without a line ending."),
        _ => string.Empty
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
