using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialMonitor.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace SerialMonitor.WinUI.Views;
public sealed partial class ModbusSensorView : UserControl
{
    public ModbusSensorView() => InitializeComponent();
    private async void Import_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
            var file = await picker.PickSingleFileAsync();
            if (file is not null && DataContext is ModbusSensorViewModel model) await model.LoadFileAsync(file.Path);
        }
        catch (Exception ex) { await ShowError(ex); }
    }
    private async void CopyGuide_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "docs", "modbus_sensor_authoring.md"));
            var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data);
        }
        catch (Exception ex) { await ShowError(ex); }
    }
    private async Task ShowError(Exception ex)
    {
        try { await new ContentDialog { XamlRoot = XamlRoot, Title = "파일 열기 / 지침 복사", Content = ex.Message, CloseButtonText = "닫기" }.ShowAsync(); }
        catch (Exception error) { Infrastructure.RuntimeDiagnostics.RecordError("SensorView", error); }
    }
}
