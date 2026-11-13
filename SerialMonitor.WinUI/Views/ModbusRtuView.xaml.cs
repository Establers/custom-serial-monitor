using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Views;

public sealed partial class ModbusRtuView : UserControl
{
    private ModbusRtuViewModel? Model => DataContext as ModbusRtuViewModel;
    public ModbusRtuView() => InitializeComponent();
    private void View_Loaded(object sender, RoutedEventArgs args)
    {
        Model?.Refresh();
    }
    private void SettingsFlyout_Opening(object sender, object args) => SettingsEditorRoot.DataContext = Model;
    private void WriteFlyout_Opening(object sender, object args) => WriteEditorRoot.DataContext = Model;
}
