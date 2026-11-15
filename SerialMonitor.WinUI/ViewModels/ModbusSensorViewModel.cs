using SerialMonitor.Core.Modbus;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

public sealed class ModbusSensorViewModel : ViewModelBase
{
    private readonly ModbusSensorService _service;
    private readonly Func<bool> _available;
    private readonly Func<string> _unavailableReason;
    private readonly Action _changed;
    private string _json = "", _summary = "JSON을 불러와 센서 주소와 값을 확인하세요.", _status = "응답 중지";
    private ModbusSensorDefinition? _definition;
    private bool _busy;
    private long _responses;
    private CancellationTokenSource? _stop;
    private Task? _running;
    public ModbusSensorViewModel(ModbusSensorService service, Func<bool> available, Action changed, Func<string>? unavailableReason = null)
    {
        _service = service; _available = available; _changed = changed;
        _unavailableReason = unavailableReason ?? (() => "시리얼 포트를 연결하고 진행 중인 다른 통신을 중지하세요.");
        ValidateCommand = new AsyncRelayCommand(() => { Validate(); return Task.CompletedTask; }, () => !IsBusy);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy && _definition is not null && _available());
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsBusy);
        ExampleCommand = new AsyncRelayCommand(async () => await LoadFileAsync(Path.Combine(AppContext.BaseDirectory, "docs", "modbus_sensor_example.json")), () => !IsBusy);
    }
    public string Json { get => _json; set { if (IsBusy || !SetProperty(ref _json, value)) return; _definition = null; Status = "JSON 확인 필요"; Summary = "내용이 바뀌었어요. JSON 확인을 누르세요."; Refresh(); } }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy => _busy;
    public bool CanEdit => !IsBusy;
    public string AvailabilityHint => IsBusy || _available() ? "" : _unavailableReason();
    public AsyncRelayCommand ValidateCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand ExampleCommand { get; }
    public void Validate()
    {
        if (IsBusy) return;
        try
        {
            _definition = ModbusSensorDefinition.Parse(Json);
            Summary = $"{_definition.Name} · ID {_definition.UnitId} · 응답 지연 {_definition.ResponseDelayMs} ms\n" +
                string.Join("\n", _definition.Points.Select(p => $"FC{p.Area:00} · 주소 {(p.Item().RegisterCount == 2 ? $"{p.Address}–{p.Address + 1}" : p.Address.ToString())} · {p.Name} = {p.InitialValue} {p.Unit} · {p.ValueType} × {p.Scale} · {(p.Writable ? "읽기/쓰기" : "읽기 전용")}"));
            Status = "확인 완료 · 센서 응답 시작을 누르면 요청을 기다려요";
        }
        catch (Exception ex) { _definition = null; Status = "JSON 확인 실패"; Summary = ex.Message; }
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
        if (IsBusy || _definition is null || !_available()) return;
        _stop = new(); _busy = true; Interlocked.Exchange(ref _responses, 0); Refresh(); _changed();
        try
        {
            var definition = _definition; var token = _stop.Token;
            _running = Task.Run(() => _service.RunAsync(definition, () => Interlocked.Increment(ref _responses), token));
            await _running; Status = "응답 중지";
        }
        catch (OperationCanceledException) { Status = "응답 중지"; }
        catch (Exception ex) { Status = "응답 중지: " + ex.Message; RuntimeDiagnostics.RecordError("ModbusSensor", ex); }
        finally { _busy = false; _stop.Dispose(); _stop = null; _running = null; Refresh(); _changed(); }
    }
    public async Task StopAsync()
    {
        _stop?.Cancel();
        if (_running is { } running) try { await running; } catch (Exception) { /* StartAsync publishes the failure. */ }
    }
    public void Refresh()
    {
        if (IsBusy) Status = $"요청 대기 · 응답 {Interlocked.Read(ref _responses)}회 · 이 PC가 센서 역할";
        OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(AvailabilityHint));
        StartCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged(); ValidateCommand.NotifyCanExecuteChanged(); ExampleCommand.NotifyCanExecuteChanged();
    }
}
