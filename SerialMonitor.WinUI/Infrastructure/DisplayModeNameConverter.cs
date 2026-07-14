using Microsoft.UI.Xaml.Data;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

public sealed class DisplayModeNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value switch
        {
            RxDisplayMode.Hex => "HEX",
            TxSendMode.Hex => "HEX",
            LogRuleMatchMode.Hex => "HEX",
            RxDisplayMode.Terminal => "Terminal",
            TxSendMode.Terminal => "Terminal",
            LogRuleMatchMode.Terminal => "Terminal",
            _ => "Terminal"
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
