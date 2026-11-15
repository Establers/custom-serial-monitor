using SerialMonitor.Core.Modbus;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.ViewModels;

public sealed class ModbusDevicePointRow : ViewModelBase
{
    private readonly ModbusItemDefinition _item;
    private readonly string _initialInput;
    private long _revision;
    private DateTimeOffset _changedAt;
    private string _inputValue = "", _currentValue = "", _changeSource = "초기값", _valueDetails = "";
    private double _highlightOpacity;
    public ModbusDevicePointRow(int index, ModbusSensorPoint point)
    {
        Index = index; _item = point.Item(); Name = point.Name;
        Address = $"FC{point.Area:00} · {point.Address}" + (_item.RegisterCount == 2 ? $"–{point.Address + 1}" : "");
        InitialValue = $"{point.InitialValue} {point.Unit}".TrimEnd();
        Format = $"{point.ValueType} · {(point.Writable ? "마스터 읽기/쓰기" : "마스터 읽기 전용")}";
        Details = $"{Name}\n{Format} · 배율 {point.Scale} · 바이트 교환 {(point.SwapBytes ? "켬" : "끔")} · 워드 교환 {(point.SwapWords ? "켬" : "끔")}";
        _initialInput = ModbusItemCodec.FormatEngineeringValue(ModbusItemCodec.Encode(point.InitialValue, _item, point.Area <= 2), _item);
        ResetPreview();
    }
    public int Index { get; }
    public string Name { get; }
    public string Address { get; }
    public string InitialValue { get; }
    public string Format { get; }
    public string Details { get; }
    public string InputValue { get => _inputValue; private set => SetProperty(ref _inputValue, value); }
    public string CurrentValue { get => _currentValue; private set => SetProperty(ref _currentValue, value); }
    public string ChangeSource { get => _changeSource; private set => SetProperty(ref _changeSource, value); }
    public string ValueDetails { get => _valueDetails; private set => SetProperty(ref _valueDetails, value); }
    public double HighlightOpacity { get => _highlightOpacity; private set => SetProperty(ref _highlightOpacity, value); }
    internal void ResetPreview()
    {
        _revision = 0; _changedAt = default; InputValue = _initialInput;
        CurrentValue = $"{InputValue} {_item.Unit}".TrimEnd(); ChangeSource = "초기값";
        ValueDetails = $"초기값 {InitialValue}\n{Details}"; HighlightOpacity = 0;
    }
    internal void Update(ModbusSensorPointState state)
    {
        if (_revision == state.Revision) return;
        _revision = state.Revision; _changedAt = state.ChangedAt;
        InputValue = ModbusItemCodec.FormatEngineeringValue(state.Words.AsSpan(), _item);
        CurrentValue = $"{InputValue} {_item.Unit}".TrimEnd();
        ChangeSource = state.Source switch { ModbusSensorChangeSource.Master => "마스터 쓰기", ModbusSensorChangeSource.User => "사용자 변경", ModbusSensorChangeSource.Reset => "초기값 복원", _ => "초기값" };
        ValueDetails = $"{ChangeSource} · {state.ChangedAt.ToLocalTime():HH:mm:ss.fff}\n원본 HEX: {string.Join(" ", state.Words.Select(word => word.ToString("X4")))}\n초기값 {InitialValue}\n{Details}";
    }
    internal void RefreshHighlight(DateTimeOffset now) => HighlightOpacity = _changedAt != default && now - _changedAt < TimeSpan.FromSeconds(2) ? 0.22 : 0;
}
