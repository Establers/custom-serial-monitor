using SerialMonitor.Core.Modbus;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class ModbusSensorViewModel : ViewModelBase
{
    private readonly ModbusSensorService _service;
    private readonly Func<bool> _available;
    private readonly Func<string> _unavailableReason;
    private readonly Action _changed;
    private string _json = "", _summary = "제품을 선택해 주소와 초기값을 확인하세요.", _status = "응답 중지";
    private ModbusSensorDefinition? _definition;
    private bool _busy;
    private long _responses;
    private CancellationTokenSource? _stop;
    private Task? _running;
    private string _availabilityHint = "";
    private bool _lastCanStart, _lastCanStop, _lastCanEdit = true;
    public ModbusSensorViewModel(ModbusSensorService service, Func<bool> available, Action changed, Func<string>? unavailableReason = null, IModbusDeviceLibrary? library = null)
    {
        _service = service; _available = available; _changed = changed;
        _unavailableReason = unavailableReason ?? (() => "시리얼 포트를 연결하고 진행 중인 다른 통신을 중지하세요.");
        _library = library ?? new ModbusDeviceLibrary(ModbusDeviceLibrary.DefaultDirectory, Path.Combine(AppContext.BaseDirectory, "docs", "modbus_sensor_ee071.json"));
        ValidateCommand = new AsyncRelayCommand(() => { Validate(); return Task.CompletedTask; }, () => CanEdit);
        StartCommand = new AsyncRelayCommand(StartAsync, () => CanEdit && _definition is not null && _available());
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsBusy);
        ExampleCommand = new AsyncRelayCommand(LoadExampleAsync, () => CanEdit);
    }
    public string Json { get => _json; set { if (IsBusy || !SetProperty(ref _json, value)) return; _definition = null; Status = "제품 정의 편집 중"; Summary = "저장할 때 형식과 주소를 검사해요."; UpdateProductPreview(); Refresh(); } }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy => _busy;
    public bool CanEdit => !IsBusy && !_libraryBusy;
    public string AvailabilityHint => _availabilityHint;
    public AsyncRelayCommand ValidateCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand ExampleCommand { get; }
    public async Task LoadExampleAsync()
    {
        await InitializeLibraryAsync();
        SelectedProduct = Products.FirstOrDefault(p => p.IsBuiltIn);
    }
    public void Validate()
    {
        if (IsBusy) return;
        try
        {
            _definition = ModbusSensorDefinition.Parse(Json);
            Summary = $"{_definition.Name} · ID {_definition.UnitId} · 응답 지연 {_definition.ResponseDelayMs} ms\n" +
                (string.IsNullOrWhiteSpace(_definition.Source) ? "" : $"{_definition.Source}\n\n") + "시작할 때 사용할 초기값\n" +
                string.Join("\n", _definition.Points.Select(p => $"FC{p.Area:00} · 주소 {(p.Item().RegisterCount == 2 ? $"{p.Address}–{p.Address + 1}" : p.Address.ToString())} · {p.Name} = {p.InitialValue} {p.Unit} · {p.ValueType} × {p.Scale} · {(p.Writable ? "읽기/쓰기" : "읽기 전용")}"));
            Status = "준비 완료 · 응답 시작을 누르면 마스터의 요청을 기다려요";
        }
        catch (Exception ex) { _definition = null; Status = "제품 정의 오류"; Summary = ex.Message; }
        UpdateProductPreview();
        Refresh();
    }
    public async Task LoadFileAsync(string path)
    {
        if (IsBusy) return;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 262144) throw new IOException("JSON 파일은 256 KiB 이하로 선택하세요.");
            using var reader = new StreamReader(stream);
            var buffer = new char[262145];
            var count = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            if (count > 262144) throw new IOException("JSON 파일이 너무 커요.");
            if (IsBusy) return;
            Json = new string(buffer, 0, count); Validate();
        }
        catch (Exception ex) { Status = ex.Message; }
    }
    public async Task StartAsync()
    {
        if (!CanEdit || _definition is null || !_available()) return;
        _liveSession = new(); _liveSnapshot = null; _hasRun = true;
        foreach (var row in Points) row.ResetPreview();
        _stop = new(); SetBusy(true); Interlocked.Exchange(ref _responses, 0); ClearFeedback(); Refresh(); _changed();
        try
        {
            var definition = _definition; var token = _stop.Token; var session = _liveSession;
            _running = Task.Run(() => _service.RunAsync(definition, () => Interlocked.Increment(ref _responses), token, session));
            await _running; Status = "응답 중지";
        }
        catch (OperationCanceledException) { Status = "응답 중지"; }
        catch (Exception ex) { Status = "응답 중지: " + ex.Message; RuntimeDiagnostics.RecordError("ModbusSensor", ex); }
        finally { SetBusy(false); _stop.Dispose(); _stop = null; _running = null; Refresh(); _changed(); }
    }
    public async Task StopAsync()
    {
        _stop?.Cancel();
        if (_running is { } running) try { await running; } catch (Exception) { /* StartAsync publishes the failure. */ }
    }
    public void Refresh()
    {
        RefreshLiveValues();
        if (IsBusy) Status = $"요청 대기 · 응답 {Interlocked.Read(ref _responses)}회";
        var hint = IsBusy || _available() ? "" : _unavailableReason();
        if (_availabilityHint != hint) { _availabilityHint = hint; OnPropertyChanged(nameof(AvailabilityHint)); }
        var canStart = CanEdit && _definition is not null && _available();
        if (_lastCanStart != canStart) { _lastCanStart = canStart; StartCommand.NotifyCanExecuteChanged(); }
        if (_lastCanStop != IsBusy) { _lastCanStop = IsBusy; StopCommand.NotifyCanExecuteChanged(); }
        if (_lastCanEdit != CanEdit)
        {
            _lastCanEdit = CanEdit; OnPropertyChanged(nameof(CanEdit));
            ValidateCommand.NotifyCanExecuteChanged(); ExampleCommand.NotifyCanExecuteChanged();
        }
    }
    private void SetBusy(bool value)
    {
        if (_busy == value) return;
        _busy = value; OnPropertyChanged(nameof(IsBusy)); RefreshManagement();
        OnPropertyChanged(nameof(ValueColumnHeader));
    }
}
