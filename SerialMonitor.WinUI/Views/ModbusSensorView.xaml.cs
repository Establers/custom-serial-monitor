using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace SerialMonitor.WinUI.Views;
public sealed partial class ModbusSensorView : UserControl
{
    public ModbusSensorView() => InitializeComponent();
    private ModbusSensorViewModel? Model => DataContext as ModbusSensorViewModel;
    private nint WindowHandle => Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
    private async void View_Loaded(object sender, RoutedEventArgs args)
    {
        if (Model is { } model) await model.InitializeLibraryAsync();
    }
    private void Management_Opening(object sender, object args)
    {
        // Flyout items are outside the view's visual tree.
        if (sender is MenuFlyout flyout) foreach (var item in flyout.Items) item.DataContext = Model;
    }
    private async void Import_Click(object sender, RoutedEventArgs args)
    {
        if (Model is not { CanManage: true } model) return;
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file is not null) await model.ImportProductAsync(file.Path);
        }
        catch (Exception ex) { model.ShowFeedback("불러오기 실패: " + ex.Message); }
    }
    private async void Export_Click(object sender, RoutedEventArgs args)
    {
        if (Model is not { SelectedProduct: { } product } model) return;
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "modbus-device" };
            picker.FileTypeChoices.Add("Modbus 장치 JSON", new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await FileIO.WriteTextAsync(file, product.Json);
            model.ShowFeedback($"‘{product.Definition.Name}’을 {file.Name}에 저장했어요.");
        }
        catch (Exception ex) { model.ShowFeedback("내보내기 실패: " + ex.Message); }
    }
    private async void New_Click(object sender, RoutedEventArgs args) => await EditAsync(true);
    private async void Edit_Click(object sender, RoutedEventArgs args) => await EditAsync(false);
    private async Task EditAsync(bool createNew)
    {
        if (Model is not { CanManage: true } model || (!createNew && model.SelectedProduct is null)) return;
        try
        {
            var previous = createNew || model.SelectedProduct?.IsBuiltIn == true ? null : model.SelectedProduct;
            var dialog = new ModbusDeviceEditorDialog { XamlRoot = XamlRoot, Json = model.GetEditorJson(createNew),
                Title = createNew ? "새 제품" : previous is null ? "기본 예제의 복사본 편집" : "제품 편집",
                Save = json => model.SaveProductAsync(json, previous) };
            await dialog.ShowAsync();
        }
        catch (Exception ex) { model.ShowFeedback("제품 편집 실패: " + ex.Message); }
    }
    private async void Duplicate_Click(object sender, RoutedEventArgs args)
    {
        if (Model is not { } model) return;
        try { await model.DuplicateProductAsync(); }
        catch (Exception ex) { model.ShowFeedback("복제 실패: " + ex.Message); }
    }
    private async void Delete_Click(object sender, RoutedEventArgs args)
    {
        if (Model is not { CanDeleteSelected: true, SelectedProduct: { } product } model) return;
        try
        {
            var answer = await new ContentDialog { XamlRoot = XamlRoot, Title = "제품 삭제", Content = $"‘{product.Definition.Name}’을 목록에서 삭제할까요? 내보낸 JSON 파일은 유지돼요.", PrimaryButtonText = "삭제", CloseButtonText = "취소" }.ShowAsync();
            if (answer == ContentDialogResult.Primary && ReferenceEquals(product, model.SelectedProduct)) await model.DeleteProductAsync();
        }
        catch (Exception ex) { model.ShowFeedback("삭제 실패: " + ex.Message); }
    }
    private async void Reload_Click(object sender, RoutedEventArgs args)
    {
        if (Model is { } model) await model.ReloadLibraryAsync();
    }
    private async void ShowAiGuide_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await new ModbusDeviceAiGuideDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme }.ShowAsync();
        }
        catch (Exception ex) { Model?.ShowFeedback("AI 안내를 열지 못했어요: " + ex.Message); }
    }
    private void Support_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement button) return;
        new Flyout { Content = new ScrollViewer { MaxHeight = 360, Content = new TextBlock { Text = ModbusSensorViewModel.SupportedFunctions, MaxWidth = 420, Margin = new Thickness(0,0,16,0), FontSize = 12, TextWrapping = TextWrapping.Wrap } } }.ShowAt(button);
    }
    private async void ChangeValue_Click(object sender, RoutedEventArgs args) => await EditValueAsync();
    private async void Point_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs args) => await EditValueAsync();
    private bool _valueDialogOpen;
    private async Task EditValueAsync()
    {
        if (_valueDialogOpen || Model is not { CanChangeSelectedValue: true, SelectedPoint: { } row, LiveSession: { } session } model) return;
        _valueDialogOpen = true;
        try
        {
            await new ModbusSensorValueDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, DataContext = row,
                Value = row.InputValue, ApplyValue = value => model.ChangeValueAsync(session, row, value) }.ShowAsync();
        }
        catch (Exception ex) { model.ShowFeedback(ex.Message); }
        finally { _valueDialogOpen = false; }
    }
    private async void RestoreSelected_Click(object sender, RoutedEventArgs args)
    {
        if (Model is not { CanChangeSelectedValue: true, SelectedPoint: { } row, LiveSession: { } session } model) return;
        try { await model.RestoreValuesAsync(session, row); }
        catch (Exception ex) { model.ShowFeedback(ex.Message); }
    }
    private async void RestoreAll_Click(object sender, RoutedEventArgs args)
    {
        if (Model is not { CanChangeValues: true, LiveSession: { } session } model) return;
        try
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "모든 항목을 초기값으로 복원",
                Content = $"현재 값 {model.Points.Count}개를 제품 JSON의 초기값으로 되돌릴까요? 응답은 계속 진행돼요.",
                PrimaryButtonText = "모두 복원", CloseButtonText = "취소", DefaultButton = ContentDialogButton.Close,
                Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                TitleTemplate = (DataTemplate)Application.Current.Resources["ModbusDialogTitleTemplate"],
                PrimaryButtonStyle = (Style)Application.Current.Resources["ModbusAccentButtonStyle"],
                CloseButtonStyle = (Style)Application.Current.Resources["ModbusActionButtonStyle"] };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await model.RestoreValuesAsync(session, null);
        }
        catch (Exception ex) { model.ShowFeedback(ex.Message); }
    }
}
