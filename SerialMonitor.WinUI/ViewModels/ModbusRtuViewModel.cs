using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

public sealed class ModbusRtuViewModel : ViewModelBase
{
    private readonly IModbusRtuMonitorService _service;
    private readonly Func<bool> _canStart;
    private readonly Func<Task> _saveSettings;
    private readonly Action _stateChanged;
    private readonly Func<string>? _availabilityReason;
    private bool _operationAdmitted, _writeAdmitted, _stopRequested;
    private string _unit = "1", _address = "0", _quantity = "10", _interval = "1000", _timeout = "1000", _scale = "1";
    private int _functionIndex = 2, _formatIndex;
    private bool _swapBytes, _swapWords;
    private string _status = "", _error = "", _statistics = "", _writeAddress = "0", _writeValues = "";
    private ModbusRegisterRow? _selectedRow;
    private ModbusMonitorSettings? _displaySettings;
    private ModbusMonitorSettings _lastPersistableSettings = new();
    private long _lastRevision = long.MinValue;
    private bool _lastBusy;
    private long _lastAgeSecond = long.MinValue;
    private string? _lastServiceError;

    public ModbusRtuViewModel(IModbusRtuMonitorService service, Func<bool> canStart, Func<Task> saveSettings, Action stateChanged, Func<string>? availabilityReason = null)
    {
        _service = service;
        _canStart = canStart;
        _saveSettings = saveSettings;
        _stateChanged = stateChanged;
        _availabilityReason = availabilityReason;
        StartCommand = new AsyncRelayCommand(() => RunAsync(true), CanRun);
        ReadOnceCommand = new AsyncRelayCommand(() => RunAsync(false), CanRun);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsBusy && !_stopRequested);
        WriteCommand = new AsyncRelayCommand(WriteAsync, CanWrite);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => !IsBusy && !_writeAdmitted);
        Refresh();
    }

    private static string T(string key, string fallback) => UiText.Get("ModbusMonitor" + key, fallback);
    public string TabTitle => "Modbus RTU";
    public string StartLabel => T("Start", "Start polling");
    public string StopLabel => T("Stop", "Stop");
    public string ReadOnceLabel => T("ReadOnce", "Read once");
    public string SettingsLabel => T("Settings", "Settings");
    public string SaveLabel => T("Save", "Save settings");
    public string UnitLabel => T("Unit", "Unit ID (1–247)");
    public string FunctionLabel => T("Function", "Read function");
    public string AddressLabel => T("Address", "Start address (base 0)");
    public string QuantityLabel => T("Quantity", "Quantity");
    public string IntervalLabel => T("Interval", "Interval (100–60000 ms)");
    public string TimeoutLabel => T("Timeout", "Response timeout (100–10000 ms)");
    public string FormatLabel => T("Format", "Display type");
    public string ScaleLabel => T("Scale", "Display multiplier");
    public string SwapBytesLabel => T("SwapBytes", "Swap bytes");
    public string SwapWordsLabel => T("SwapWords", "Swap words (32-bit)");
    public string SetupHelp => T("SetupHelp", "Addresses are protocol offsets: holding register 40001 is address 0. Settings are saved in the app profile. Loading settings or reconnecting never starts polling.");
    public string OwnershipHelp => T("OwnershipHelp", "Polling reserves this connection. Stop it before using sequences, manual TX or the bridge. Direct writes here are serialized with polling.");
    public string RawLabel => T("Raw", "Raw HEX");
    public string ValueLabel => T("Value", "Value");
    public string StateLabel => T("State", "State");
    public string UpdatedLabel => T("Updated", "Last response");
    public string TableAddressLabel => T("TableAddress", "Address (base 0)");
    public string EmptyHelp => T("Empty", "Connect to the device, check Settings, then choose Read once or Start polling.");
    public string WriteLabel => T("Write", "Write and verify");
    public string WriteEditorLabel => T("WriteEditor", "Direct write…");
    public string WriteAddressLabel => T("WriteAddress", "Write address (base 0)");
    public string WriteValuesLabel => IsCoilFunction ? T("WriteCoils", "Raw coil values (0 / 1)") : T("WriteRegisters", "Raw UInt16 values (0–65535 / 0x HEX)");
    public string WriteHelp => T("WriteHelp", "Select a row or enter an address inside the monitored area. Values are raw and unscaled; multiple values use consecutive addresses. Writes are never retried automatically. A readback checks the result.");
    public string ReadOnlyHelp => T("ReadOnly", "Discrete inputs and input registers are read-only. Select FC01 or FC03 in Settings to write.");
    public string WriteAvailabilityHelp => IsWritableFunction ? WriteHelp : ReadOnlyHelp;
    public bool IsWriteEditorEnabled => !_writeAdmitted && !_stopRequested;
    public IReadOnlyList<string> Functions { get; } = ["01 · Coils", "02 · Discrete inputs", "03 · Holding registers", "04 · Input registers"];
    public IReadOnlyList<string> Formats { get; } = ["UInt16", "Int16", "UInt32", "Int32", "Float32"];
    public ObservableCollection<ModbusRegisterRow> Rows { get; } = [];
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand ReadOnceCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand WriteCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public bool IsBusy => _operationAdmitted || _writeAdmitted || _service.IsBusy;
    public bool IsSettingsEnabled => !IsBusy && !_writeAdmitted;
    public bool IsCoilFunction => FunctionIndex is 0 or 1;
    public bool IsRegisterFunction => !IsCoilFunction;
    public bool IsWritableFunction => FunctionIndex is 0 or 2;
    public bool IsWordSwapEnabled => IsSettingsEnabled && IsRegisterFunction && FormatIndex >= 2;
    public string AvailabilityText => !IsBusy && !_canStart() ? _availabilityReason?.Invoke() ?? T("Busy", "Connect the device and stop sequences, automation and the bridge before starting Modbus.") : "";
    public Visibility AvailabilityVisibility => AvailabilityText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ErrorVisibility => Error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility WriteVisibility => IsWritableFunction ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReadOnlyVisibility => IsWritableFunction ? Visibility.Collapsed : Visibility.Visible;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(ErrorVisibility)); } }
    public string Statistics { get => _statistics; private set => SetProperty(ref _statistics, value); }
    public string RequestSummary => $"ID {UnitText} · FC {FunctionIndex + 1:00} · {AddressText} + {QuantityText} · {IntervalText} ms";
    public string UnitText { get => _unit; set => SetSetting(ref _unit, value, nameof(UnitText)); }
    public string AddressText { get => _address; set => SetSetting(ref _address, value, nameof(AddressText)); }
    public string QuantityText { get => _quantity; set => SetSetting(ref _quantity, value, nameof(QuantityText)); }
    public string IntervalText { get => _interval; set => SetSetting(ref _interval, value, nameof(IntervalText)); }
    public string TimeoutText { get => _timeout; set => SetSetting(ref _timeout, value, nameof(TimeoutText)); }
    public string ScaleText { get => _scale; set => SetSetting(ref _scale, value, nameof(ScaleText)); }
    public string WriteAddressText { get => _writeAddress; set { if (!_writeAdmitted) SetProperty(ref _writeAddress, value ?? ""); } }
    public string WriteValuesText { get => _writeValues; set { if (!_writeAdmitted) SetProperty(ref _writeValues, value ?? ""); } }
    public int FunctionIndex
    {
        get => _functionIndex;
        set
        {
            if (!IsSettingsEnabled || value is < 0 or > 3 || !SetProperty(ref _functionIndex, value)) return;
            SettingsChanged();
            foreach (var name in new[] { nameof(IsCoilFunction), nameof(IsRegisterFunction), nameof(IsWritableFunction), nameof(WriteVisibility), nameof(ReadOnlyVisibility), nameof(WriteValuesLabel), nameof(IsWordSwapEnabled), nameof(WriteAvailabilityHelp) }) OnPropertyChanged(name);
        }
    }
    public int FormatIndex
    {
        get => _formatIndex;
        set { if (IsSettingsEnabled && value is >= 0 and <= 4 && SetProperty(ref _formatIndex, value)) { SettingsChanged(); OnPropertyChanged(nameof(IsWordSwapEnabled)); } }
    }
    public bool SwapBytes { get => _swapBytes; set { if (IsSettingsEnabled && SetProperty(ref _swapBytes, value)) SettingsChanged(); } }
    public bool SwapWords { get => _swapWords; set { if (IsSettingsEnabled && SetProperty(ref _swapWords, value)) SettingsChanged(); } }
    public ModbusRegisterRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value) || value is null || _writeAdmitted) return;
            WriteAddressText = value.Address.ToString(CultureInfo.InvariantCulture);
            WriteValuesText = value.RawValues is { Length: > 0 } values ? string.Join(" ", values.Select(v => v.ToString(CultureInfo.InvariantCulture))) : "";
        }
    }

    private void SetSetting(ref string field, string? value, string property)
    {
        if (IsSettingsEnabled && SetProperty(ref field, value ?? "", property)) SettingsChanged();
    }
    private void SettingsChanged()
    {
        // Until the next explicit read, old service values must not acquire new addresses or formats.
        _displaySettings = null;
        if (Rows.Count > 0) { Rows.Clear(); SelectedRow = null; OnPropertyChanged(nameof(EmptyVisibility)); }
        Error = "";
        OnPropertyChanged(nameof(RequestSummary));
        RefreshAvailability();
    }
    private bool CanRun() => !IsBusy && !_writeAdmitted && _canStart();
    private bool CanWrite() => !_writeAdmitted && !_stopRequested && IsWritableFunction && (_service.GetSnapshot().IsPolling || CanRun());

    public ModbusMonitorSettings CreateSettingsSnapshot()
    {
        if (!TryNumber(UnitText, out var unit) || !TryNumber(AddressText, out var address) || !TryNumber(QuantityText, out var quantity) ||
            !int.TryParse(IntervalText, NumberStyles.None, CultureInfo.InvariantCulture, out var interval) ||
            !int.TryParse(TimeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out var timeout))
            throw new ArgumentException(T("InvalidNumbers", "Enter complete non-negative numbers. Unit ID, address and quantity also accept 0x HEX."));
        if (!double.TryParse(ScaleText, NumberStyles.Float, CultureInfo.CurrentCulture, out var scale) &&
            !double.TryParse(ScaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out scale))
            throw new ArgumentException(T("InvalidScale", "Enter a finite display multiplier, for example 1 or 0.1."));
        var result = new ModbusMonitorSettings { UnitId = unit, Function = (byte)(FunctionIndex + 1), Address = address, Quantity = quantity,
            IntervalMs = interval, TimeoutMs = timeout, ValueType = (ModbusValueType)FormatIndex, Scale = scale, SwapBytes = SwapBytes, SwapWords = SwapWords };
        try { result.Validate(); }
        catch (ArgumentException ex) { throw new ArgumentException(T("InvalidSettings", "Check ID 1–247, address 0–65535, quantity 1–125 registers / 1–2000 bits, address range, interval 100–60000 ms, timeout 100–10000 ms and a finite multiplier."), ex); }
        return result;
    }

    public ModbusMonitorSettings CreatePersistableSettingsSnapshot()
    {
        try { _lastPersistableSettings = CreateSettingsSnapshot(); }
        catch (ArgumentException) { }
        return _lastPersistableSettings.Clone();
    }

    public void ApplySettings(ModbusMonitorSettings? settings)
    {
        if (IsBusy || _writeAdmitted) throw new InvalidOperationException(T("StopBeforeSettings", "Stop Modbus before loading settings."));
        var copy = settings?.Clone() ?? new();
        _lastPersistableSettings = copy.Clone();
        UnitText = copy.UnitId.ToString(CultureInfo.InvariantCulture);
        // Preserve the complete saved target for correction; loading never transmits.
        // An unsupported function has no editable target and is reported explicitly.
        _functionIndex = copy.Function is >= 1 and <= 4 ? copy.Function - 1 : -1;
        OnPropertyChanged(nameof(FunctionIndex));
        foreach (var name in new[] { nameof(IsCoilFunction), nameof(IsRegisterFunction), nameof(IsWritableFunction), nameof(WriteVisibility), nameof(ReadOnlyVisibility), nameof(WriteValuesLabel), nameof(WriteAvailabilityHelp) }) OnPropertyChanged(name);
        AddressText = copy.Address.ToString(CultureInfo.InvariantCulture);
        QuantityText = copy.Quantity.ToString(CultureInfo.InvariantCulture);
        IntervalText = copy.IntervalMs.ToString(CultureInfo.InvariantCulture);
        TimeoutText = copy.TimeoutMs.ToString(CultureInfo.InvariantCulture);
        _formatIndex = Enum.IsDefined(copy.ValueType) ? (int)copy.ValueType : -1;
        OnPropertyChanged(nameof(FormatIndex));
        ScaleText = copy.Scale.ToString("G17", CultureInfo.CurrentCulture);
        SwapBytes = copy.SwapBytes;
        SwapWords = copy.SwapWords;
        WriteAddressText = AddressText;
        WriteValuesText = "";
        SettingsChanged();
        Status = T("Ready", "Ready · settings do not start communication");
        try { copy.Validate(); }
        catch (ArgumentException) { Error = T("InvalidSettings", "Check ID 1–247, address 0–65535, quantity 1–125 registers / 1–2000 bits, address range, interval 100–60000 ms, timeout 100–10000 ms and a finite multiplier."); }
    }

    public async Task RunAsync(bool repeat)
    {
        if (!CanRun()) { Error = T("Busy", "Connect the device and stop sequences, automation and the bridge before starting Modbus."); return; }
        ModbusMonitorSettings settings;
        try { settings = CreateSettingsSnapshot(); }
        catch (ArgumentException ex) { Error = ex.Message; return; }
        _operationAdmitted = true;
        Error = "";
        _lastPersistableSettings = settings.Clone();
        _displaySettings = settings;
        RebuildRows(settings);
        _lastRevision = long.MinValue;
        PublishAvailability();
        try { await _service.RunAsync(settings, repeat, CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; RuntimeDiagnostics.RecordError("ModbusMonitor.Run", ex); }
        finally { _operationAdmitted = false; Refresh(); PublishAvailability(); }
    }

    public async Task StopAsync()
    {
        _stopRequested = true;
        Status = T("Stopping", "Stopping · waiting for the current response (up to the response timeout)");
        RefreshAvailability();
        try { await _service.StopAsync(); }
        catch (Exception ex) { Error = ex.Message; RuntimeDiagnostics.RecordError("ModbusMonitor.Stop", ex); }
        finally { _stopRequested = false; _lastRevision = long.MinValue; Refresh(); PublishAvailability(); }
    }

    public async Task SaveSettingsAsync()
    {
        if (!IsSettingsEnabled) return;
        try { var settings = CreateSettingsSnapshot(); await _saveSettings(); _lastPersistableSettings = settings; Error = ""; Status = T("Saved", "Settings saved in the app profile"); }
        catch (Exception ex) { Error = ex.Message; RuntimeDiagnostics.RecordError("ModbusMonitor.Save", ex); }
    }

    public async Task WriteAsync()
    {
        if (!CanWrite()) { Error = T("WriteUnavailable", "Writing is available for FC01 and FC03 when this connection is free or polling here."); return; }
        ModbusMonitorSettings settings;
        int address;
        ushort[] values;
        try
        {
            settings = CreateSettingsSnapshot();
            if (!TryNumber(WriteAddressText, out address)) throw new ArgumentException(T("InvalidWriteAddress", "Enter a complete write address (base 0)."));
            if (WriteValuesText.Length > 16000) throw new ArgumentException(T("InvalidWriteValues", "Enter raw values separated by spaces or commas. Check the quantity and numeric range."));
            var parts = WriteValuesText.Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
            var maximum = settings.Function == 1 ? 1968 : 123;
            if (parts.Length == 0 || parts.Length > maximum) throw new ArgumentException(T("InvalidWriteValues", "Enter raw values separated by spaces or commas. Check the quantity and numeric range."));
            values = new ushort[parts.Length];
            for (var index = 0; index < parts.Length; index++)
            {
                if (!TryNumber(parts[index], out var value) || value > (settings.Function == 1 ? 1 : ushort.MaxValue))
                    throw new ArgumentException(T("InvalidWriteValues", "Enter raw values separated by spaces or commas. Check the quantity and numeric range."));
                values[index] = (ushort)value;
            }
            if (address < settings.Address || (long)address + values.Length > (long)settings.Address + settings.Quantity)
                throw new ArgumentException(T("WriteRange", "The complete write must be inside the monitored read area so it can be verified."));
        }
        catch (ArgumentException ex) { Error = ex.Message; return; }
        _writeAdmitted = true;
        Error = "";
        _lastPersistableSettings = settings.Clone();
        if (_displaySettings is null) { _displaySettings = settings; RebuildRows(settings); }
        PublishAvailability();
        try { await _service.WriteAsync(settings, address, values, CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; RuntimeDiagnostics.RecordError("ModbusMonitor.Write", ex); }
        finally { _writeAdmitted = false; Refresh(); PublishAvailability(); }
    }

    public void Refresh()
    {
        var snapshot = _service.GetSnapshot();
        var ageSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_lastRevision != snapshot.Revision || _lastAgeSecond != ageSecond)
        {
            _lastAgeSecond = ageSecond;
            Statistics = UiText.Format("ModbusMonitorStatistics", "OK {0:N0} · Errors {1:N0} · Response {2}", snapshot.SuccessCount, snapshot.ErrorCount,
                snapshot.ResponseMilliseconds is { } ms ? ms.ToString("F1", CultureInfo.CurrentCulture) + " ms" : "—");
            if (snapshot.UpdatedAt is { } updated)
                Statistics += " · " + UiText.Format("ModbusMonitorLastAge", "Last response {0:N0} s ago", Math.Max(0, Math.Floor((DateTimeOffset.Now - updated).TotalSeconds)));
        }
        if (_lastRevision != snapshot.Revision)
        {
            _lastRevision = snapshot.Revision;
            Status = _stopRequested && IsBusy ? T("Stopping", "Stopping · waiting for the current response (up to the response timeout)") : snapshot.State switch
            {
                ModbusMonitorState.Reading => T("Reading", "Reading"),
                ModbusMonitorState.Polling => T("Polling", "Polling · connection reserved"),
                ModbusMonitorState.Stopped => snapshot.ValuesCurrent ? T("ReadComplete", "Read complete · last response shown") : T("Stopped", "Stopped · retained values are stale"),
                ModbusMonitorState.Error => T("ErrorState", "Communication error · retained values are stale"),
                ModbusMonitorState.Disconnected => T("Disconnected", "Disconnected · restart explicitly after connecting"),
                _ => T("Ready", "Ready · settings do not start communication")
            };
            if (snapshot.WriteState != ModbusWriteState.None) Status += " · " + (snapshot.WriteState switch
            {
                ModbusWriteState.Queued => T("WriteQueued", "Write queued"),
                ModbusWriteState.Writing => T("Writing", "Writing"),
                ModbusWriteState.Verified => T("WriteVerified", "Write verified"),
                ModbusWriteState.NotVerified => T("WriteNotVerified", "Write not verified; inspect before retrying"),
                _ => T("WriteFailed", "Write failed; inspect before retrying")
            });
            if (!string.IsNullOrWhiteSpace(snapshot.Error)) Error = snapshot.Error;
            else if (_lastServiceError is not null && Error == _lastServiceError) Error = "";
            _lastServiceError = snapshot.Error;
            if (_displaySettings is { } settings)
                foreach (var row in Rows) row.Update(snapshot.Values, settings, snapshot.ValuesCurrent, snapshot.UpdatedAt);
        }
        var busy = IsBusy;
        if (_lastBusy != busy) { _lastBusy = busy; PublishAvailability(); }
        else RefreshAvailability();
    }

    private void RebuildRows(ModbusMonitorSettings settings)
    {
        var width = settings.Function <= 2 || settings.ValueType is ModbusValueType.UInt16 or ModbusValueType.Int16 ? 1 : 2;
        Rows.Clear();
        SelectedRow = null;
        for (var offset = 0; offset < settings.Quantity; offset += width)
            Rows.Add(new ModbusRegisterRow(settings.Address + offset, offset, Math.Min(width, settings.Quantity - offset), width));
        WriteAddressText = settings.Address.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(EmptyVisibility));
    }
    private void PublishAvailability() { RefreshAvailability(); _stateChanged(); }
    public void RefreshAvailability()
    {
        foreach (var property in new[] { nameof(IsBusy), nameof(IsSettingsEnabled), nameof(IsWordSwapEnabled), nameof(BusyVisibility), nameof(IsWriteEditorEnabled), nameof(AvailabilityText), nameof(AvailabilityVisibility) }) OnPropertyChanged(property);
        StartCommand.NotifyCanExecuteChanged(); ReadOnceCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged();
        WriteCommand.NotifyCanExecuteChanged(); SaveSettingsCommand.NotifyCanExecuteChanged();
    }
    internal static bool TryNumber(string text, out int number)
    {
        var trimmed = text.Trim();
        return trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(trimmed.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out number) && number >= 0
            : int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 0;
    }
}

public sealed class ModbusRegisterRow(int address, int offset, int count, int width) : ViewModelBase
{
    private string _raw = "—", _value = "—", _status = UiText.Get("ModbusMonitorNoValue", "No value"), _updated = "—", _changed = "";
    public int Address { get; } = address;
    public string AddressText => count == 1 ? Address.ToString(CultureInfo.InvariantCulture) : $"{Address}–{Address + count - 1}";
    public ushort[]? RawValues { get; private set; }
    public string RawText { get => _raw; private set => SetProperty(ref _raw, value); }
    public string ValueText { get => _value; private set => SetProperty(ref _value, value); }
    public string StatusText { get => _status; private set => SetProperty(ref _status, value); }
    public string UpdatedText { get => _updated; private set => SetProperty(ref _updated, value); }
    public string ChangeText { get => _changed; private set => SetProperty(ref _changed, value); }
    internal void Update(ushort[]? values, ModbusMonitorSettings settings, bool current, DateTimeOffset? updated)
    {
        if (values is null || offset + count > values.Length)
        {
            StatusText = UiText.Get("ModbusMonitorNoValue", "No value");
            return;
        }
        var raw = values.AsSpan(offset, count).ToArray();
        ChangeText = RawValues is { } old && !old.AsSpan().SequenceEqual(raw) ? "●" : "";
        RawValues = raw;
        RawText = string.Join(" ", raw.Select(v => v.ToString("X4", CultureInfo.InvariantCulture)));
        UpdatedText = updated?.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture) ?? "—";
        StatusText = current ? UiText.Get("ModbusMonitorCurrent", "Current") : UiText.Get("ModbusMonitorStale", "Stale value");
        if (count < width) { ValueText = UiText.Get("ModbusMonitorUnpaired", "Missing 32-bit partner"); return; }
        if (settings.Function <= 2) { ValueText = raw[0] == 0 ? "0 · OFF" : "1 · ON"; return; }
        ushort Word(ushort value) => settings.SwapBytes ? (ushort)((value >> 8) | (value << 8)) : value;
        double decoded;
        if (width == 1) decoded = settings.ValueType == ModbusValueType.Int16 ? unchecked((short)Word(raw[0])) : Word(raw[0]);
        else
        {
            var first = Word(raw[0]); var second = Word(raw[1]);
            var bits = settings.SwapWords ? ((uint)second << 16) | first : ((uint)first << 16) | second;
            decoded = settings.ValueType switch { ModbusValueType.UInt32 => (double)bits, ModbusValueType.Int32 => (double)unchecked((int)bits), _ => (double)BitConverter.Int32BitsToSingle(unchecked((int)bits)) };
        }
        var result = decoded * settings.Scale;
        ValueText = double.IsFinite(decoded) && !double.IsFinite(result) ? UiText.Get("ModbusMonitorOverflow", "Scaling overflow") :
            result.ToString(settings.ValueType == ModbusValueType.Float32 && settings.Scale == 1 ? "G9" : "G17", CultureInfo.CurrentCulture);
    }
}
