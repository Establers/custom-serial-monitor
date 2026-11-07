using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialMonitor.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace SerialMonitor.WinUI.Views;

public sealed partial class VirtualModbusDeviceView : UserControl
{
    private VirtualModbusDeviceViewModel? Model => DataContext as VirtualModbusDeviceViewModel;
    public VirtualModbusDeviceView() => InitializeComponent();
    private nint WindowHandle => Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);

    private async void Import_Click(object sender, RoutedEventArgs args)
    {
        if (Model is not { } model) return;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count > 0) await model.ImportAsync(files.Select(f => f.Path).ToArray());
        }
        catch (Exception ex) { model.Report(ex.Message); }
    }

    private async void Save_Click(object sender, RoutedEventArgs args)
    {
        if (Model is not { } model) return;
        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "virtual-modbus-devices" };
            picker.FileTypeChoices.Add("JSON", new[] { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is not null) await model.SaveAsync(file.Path);
        }
        catch (Exception ex) { model.Report(ex.Message); }
    }

    private async void Paste_Click(object sender, RoutedEventArgs args)
    {
        if (Model is not { } model) return;
        try
        {
            var input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MaxLength = 16 * 1024 * 1024,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), MinHeight = 220, MaxHeight = 420 };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = model.PasteLabel, Content = input,
                PrimaryButtonText = model.ImportLabel, CloseButtonText = model.CloseLabel };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await model.ImportTextAsync(input.Text);
        }
        catch (Exception ex) { model.Report(ex.Message); }
    }

    private async void Guide_Click(object sender, RoutedEventArgs args)
    {
        if (Model is not { } model) return;
        try
        {
            var text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "docs", "virtual_modbus_device_gpt.md"));
            var package = new DataPackage(); package.SetText(text); Clipboard.SetContent(package);
            model.Report(model.GuideCopiedMessage);
        }
        catch (Exception ex) { model.Report(ex.Message); }
    }

    private async void Start_Click(object sender, RoutedEventArgs args) { if (Model is { } model) await model.ToggleAsync(); }
    private async void Sample_Click(object sender, RoutedEventArgs args) { if (Model is { } model) await model.LoadExampleAsync(); }
    private void Apply_Click(object sender, RoutedEventArgs args) => Model?.ApplyValue();
    private void ChangeId_Click(object sender, RoutedEventArgs args) => Model?.ChangeId();
    private void Remove_Click(object sender, RoutedEventArgs args) => Model?.Remove();
    private void Previous_Click(object sender, RoutedEventArgs args) => Model?.Previous();
    private void Next_Click(object sender, RoutedEventArgs args) => Model?.Next();
}
