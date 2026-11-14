using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Views;

public sealed class ProtocolViewSelector : ComboBox
{
    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is ComboBoxItem container && item is ProtocolViewOption option)
        {
            // Bind the generated container directly. A Binding in a Style setter
            // does not reliably update its availability in WinUI.
            container.ClearValue(Control.IsEnabledProperty);
            container.SetBinding(Control.IsEnabledProperty, new Binding
            {
                Source = option,
                Path = new PropertyPath(nameof(ProtocolViewOption.IsEnabled)),
                Mode = BindingMode.OneWay
            });
        }
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is ComboBoxItem container && item is ProtocolViewOption)
            container.ClearValue(Control.IsEnabledProperty);
        base.ClearContainerForItemOverride(element, item);
    }
}
