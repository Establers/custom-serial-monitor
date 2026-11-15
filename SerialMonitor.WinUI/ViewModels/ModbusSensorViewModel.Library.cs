using System.Collections.ObjectModel;
using System.Text.Json;
using SerialMonitor.Core.Modbus;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

public sealed record ModbusDevicePointRow(string Name, string Address, string InitialValue, string Format, string Details);

public sealed partial class ModbusSensorViewModel
{
    private readonly IModbusDeviceLibrary _library;
    private Task? _initialization;
    private bool _libraryBusy;
    private ModbusDevicePreset? _selectedProduct;
    private string _feedback = "";
    public ObservableCollection<ModbusDevicePreset> Products { get; } = [];
    private IReadOnlyList<ModbusDevicePointRow> _points = [];
    public IReadOnlyList<ModbusDevicePointRow> Points { get => _points; private set => SetProperty(ref _points, value); }
    public bool CanManage => !IsBusy && !_libraryBusy;
    public bool CanManageSelected => CanManage && SelectedProduct is not null;
    public bool HasSelectedProduct => SelectedProduct is not null;
    public bool CanDeleteSelected => CanManageSelected && SelectedProduct?.IsBuiltIn == false;
    public string Feedback { get => _feedback; private set => SetProperty(ref _feedback, value); }
    public string ProductSummary => _definition is null ? "제품을 선택하거나 제품 관리에서 추가하세요." :
        $"장치 ID {_definition.UnitId} · {_definition.Points.Count}개 항목 · 응답 지연 {_definition.ResponseDelayMs} ms";
    public string ProductSource => _definition?.Source ?? "";
    public const string SupportedFunctions = "읽기: FC01 코일, FC02 입력 비트, FC03 보유 레지스터, FC04 입력 레지스터\n쓰기: FC05/06 한 개 쓰기, FC15/16 여러 개 쓰기\n값: 비트, 16/32비트 정수, Float32, 배율·바이트/워드 순서\n\n선택한 장치 ID 하나에 응답해요. 진단·장치 식별·파일·FIFO·사용자 정의 기능, 문자열 자료형, 값 자동 변화와 오류 주입은 아직 지원하지 않아요.\n쓰기는 메모리에 반영되고 다시 시작하면 초기값으로 돌아가요.";

    public ModbusDevicePreset? SelectedProduct
    {
        get => _selectedProduct;
        set
        {
            if (!CanManage || ReferenceEquals(_selectedProduct, value)) return;
            _selectedProduct = value; OnPropertyChanged();
            Json = value?.Json ?? "";
            if (value is not null) Validate();
            else { _definition = null; Points = []; OnPropertyChanged(nameof(ProductSummary)); OnPropertyChanged(nameof(ProductSource)); }
            RefreshManagement(); Refresh();
        }
    }

    public Task InitializeLibraryAsync() => _initialization ??= ReloadLibraryAsync();
    public async Task ReloadLibraryAsync()
    {
        if (!CanManage) return;
        var selectedId = SelectedProduct?.Id;
        _libraryBusy = true; RefreshManagement(); Refresh();
        try
        {
            var result = await _library.LoadAsync(CancellationToken.None);
            Products.Clear(); foreach (var item in result.Items) Products.Add(item);
            _libraryBusy = false;
            SelectedProduct = Products.FirstOrDefault(p => p.Id == selectedId) ?? Products.FirstOrDefault();
            Feedback = result.Warning;
        }
        catch (Exception ex) { Feedback = "제품 목록을 불러오지 못했어요: " + ex.Message; }
        finally { _libraryBusy = false; RefreshManagement(); Refresh(); }
    }

    public async Task SaveProductAsync(string json, ModbusDevicePreset? previous = null)
    {
        if (!CanManage) throw new InvalidOperationException("응답을 중지한 뒤 제품을 편집하세요.");
        _libraryBusy = true; RefreshManagement(); Refresh();
        try
        {
            var saved = await _library.SaveAsync(json, previous, CancellationToken.None);
            var index = previous is null ? -1 : Products.IndexOf(previous);
            if (index >= 0) Products[index] = saved; else Products.Add(saved);
            _libraryBusy = false; SelectedProduct = saved;
            Feedback = $"‘{saved.Definition.Name}’을 제품 목록에 저장했어요.";
        }
        finally { _libraryBusy = false; RefreshManagement(); Refresh(); }
    }

    public async Task ImportProductAsync(string path)
    {
        if (!CanManage) return;
        var json = await ModbusDeviceLibrary.ReadJsonFileAsync(path, CancellationToken.None);
        await SaveProductAsync(UniqueCopyJson(json, forceSuffix: false));
    }

    public Task DuplicateProductAsync() => SelectedProduct is { } product
        ? SaveProductAsync(UniqueCopyJson(product.Json, forceSuffix: true)) : Task.CompletedTask;

    public async Task DeleteProductAsync()
    {
        if (!CanDeleteSelected || SelectedProduct is not { } product) return;
        _libraryBusy = true; RefreshManagement(); Refresh();
        try
        {
            await _library.DeleteAsync(product, CancellationToken.None);
            Products.Remove(product); _libraryBusy = false; SelectedProduct = Products.FirstOrDefault();
            Feedback = $"‘{product.Definition.Name}’을 목록에서 삭제했어요.";
        }
        finally { _libraryBusy = false; RefreshManagement(); Refresh(); }
    }

    public string GetEditorJson(bool createNew)
    {
        if (!createNew && SelectedProduct is { } product)
            return product.IsBuiltIn ? UniqueCopyJson(product.Json, forceSuffix: true) : product.Json;
        return JsonSerializer.Serialize(new ModbusSensorDefinition
        {
            SchemaVersion = 1, Name = UniqueName("새 장치"), UnitId = 1,
            Points = [new() { Name = "설정값", Area = 3, Address = 0, InitialValue = "0", Writable = true }]
        }, ModbusDeviceLibrary.JsonOptions);
    }

    private string UniqueCopyJson(string json, bool forceSuffix)
    {
        var definition = ModbusSensorDefinition.Parse(json);
        definition.Name = UniqueName(definition.Name, forceSuffix);
        return JsonSerializer.Serialize(definition, ModbusDeviceLibrary.JsonOptions);
    }
    private string UniqueName(string name, bool forceSuffix = false)
    {
        var root = name[..Math.Min(name.Length, 110)];
        var candidate = forceSuffix ? root + " 복사본" : root;
        for (var index = 2; Products.Any(p => string.Equals(p.Definition.Name, candidate, StringComparison.OrdinalIgnoreCase)); index++)
            candidate = $"{root} ({index})";
        return candidate;
    }

    public void ShowFeedback(string text) => Feedback = text;
    public void ClearFeedback() => Feedback = "";
    private void RefreshManagement()
    {
        OnPropertyChanged(nameof(CanManage)); OnPropertyChanged(nameof(CanManageSelected)); OnPropertyChanged(nameof(CanDeleteSelected)); OnPropertyChanged(nameof(HasSelectedProduct));
    }

    private void UpdateProductPreview()
    {
        Points = _definition?.Points.Select(p => new ModbusDevicePointRow(p.Name,
            $"FC{p.Area:00} · {p.Address}" + (p.Item().RegisterCount == 2 ? $"–{p.Address + 1}" : ""),
            $"{p.InitialValue} {p.Unit}".TrimEnd(), $"{p.ValueType} · {(p.Writable ? "읽기/쓰기" : "읽기 전용")}",
            $"{p.ValueType} · 배율 {p.Scale} · 바이트 교환 {(p.SwapBytes ? "켬" : "끔")} · 워드 교환 {(p.SwapWords ? "켬" : "끔")}")).ToArray() ?? [];
        OnPropertyChanged(nameof(ProductSummary)); OnPropertyChanged(nameof(ProductSource));
    }
}
