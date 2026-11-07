using System.Globalization;
using SerialMonitor.Core.Protocols.VirtualModbus;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

public sealed class VirtualModbusRegisterRow(VirtualModbusPointSnapshot point) : ViewModelBase
{
    private string _currentValue = point.Value;
    public int Index => point.Index;
    public string Name => point.Definition.Name;
    public string Address => $"{point.Definition.Area}  {point.Definition.Address} (0x{point.Definition.Address:X4})";
    public string Format => $"{point.Definition.DataType} · {point.Definition.Unit}";
    public string CurrentValue { get => _currentValue; private set => SetProperty(ref _currentValue, value); }
    public string Description => $"{point.Definition.Description}\nscale={point.Definition.Scale:G}, offset={point.Definition.Offset:G}; {point.Definition.ByteOrder}, {point.Definition.WordOrder}";
    public void Refresh(string value) => CurrentValue = value;
}

public sealed class VirtualModbusDeviceViewModel(VirtualModbusDeviceStore store, IVirtualModbusDeviceRuntime runtime,
    Action start, IVirtualModbusDeviceFileService? files = null) : ViewModelBase
{
    private readonly IVirtualModbusDeviceFileService _files = files ?? new VirtualModbusDeviceFileService();
    private readonly CancellationTokenSource _stop = new();
    private VirtualModbusDeviceInfo? _selectedDevice;
    private VirtualModbusRegisterRow? _selectedRegister;
    private string _editValue = "";
    private string _editUnitId = "";
    private string _message = "";
    private string _status = "";
    private bool _busy;
    private bool _refreshingRegisters;
    private bool _lastRunning, _lastStopping;
    private int _page;
    private long _version = -1;
    public bool SuppressLocalEcho { get; set; }
    public string EchoLabel => UiText.Get("VmdEcho", "Adapter echoes TX (ignore the next matching frame)");
    public bool CanConfigure => !IsRunning && !IsStopping && !IsBusy;
    public BulkObservableCollection<VirtualModbusDeviceInfo> Devices { get; } = new();
    public BulkObservableCollection<VirtualModbusRegisterRow> Registers { get; } = new();
    public string Title => "Virtual Modbus Device";
    public string ImportLabel => UiText.Get("VmdImport", "Add JSON…");
    public string PasteLabel => UiText.Get("VmdPaste", "Paste JSON…");
    public string CloseLabel => UiText.Get("VmdClose", "Close");
    public string GuideCopiedMessage => UiText.Get("VmdGuideCopied", "GPT instructions copied. Paste them into GPT along with the sensor protocol PDF.");
    public string SaveLabel => UiText.Get("VmdSave", "Save JSON…");
    public string GuideLabel => UiText.Get("VmdGuide", "Copy GPT instructions");
    public string SampleLabel => UiText.Get("VmdSample", "Load example");
    public string RemoveLabel => UiText.Get("VmdRemove", "Remove device");
    public string ApplyLabel => UiText.Get("VmdApply", "Apply value");
    public string ChangeIdLabel => UiText.Get("VmdChangeId", "Change ID");
    public string ValueLabel => UiText.Get("VmdValue", "New value (use . for decimals)");
    public string Hint => UiText.Get("VmdHint", "Add sensor JSON files, connect the product, then start. RX requests → virtual registers → TX responses. Values and IDs can change while running. Addresses are zero-based.");
    public string PersistenceHint => UiText.Get("VmdPersistence", "Save JSON to keep the current values and IDs. Starting and reconnecting never enable automatic replies by themselves.");
    public string StartLabel => IsRunning ? UiText.Get("VmdStop", "Stop") : IsStopping ? UiText.Get("VmdStopping", "Stopping…") : UiText.Get("VmdStart", "Start");
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public bool IsRunning => runtime.Snapshot().Running;
    public bool IsStopping => runtime.Snapshot().Stopping;
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) NotifyControls(); } }
    public bool CanOperate => !IsBusy;
    public bool CanToggle => !IsBusy && !IsStopping && (IsRunning || Devices.Count > 0);
    public bool CanEdit => !IsBusy && SelectedRegister is not null;
    public bool HasDevice => !IsBusy && SelectedDevice is not null;
    public bool CanPrevious => !IsBusy && _page > 0;
    public bool CanNext => !IsBusy && (_page + 1) * 128 < (SelectedDevice?.RegisterCount ?? 0);
    public string PageText => $"{_page + 1} / {Math.Max(1, ((SelectedDevice?.RegisterCount ?? 0) + 127) / 128)}";
    public string EditValue { get => _editValue; set => SetProperty(ref _editValue, value); }
    public string EditUnitId { get => _editUnitId; set => SetProperty(ref _editUnitId, value); }
    public VirtualModbusDeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            // A ListView reset can temporarily clear its current selection.
            if (value is null && _selectedDevice is not null && Devices.Contains(_selectedDevice)) return;
            if (!SetProperty(ref _selectedDevice, value)) return;
            _page = 0;
            SelectedRegister = null;
            EditUnitId = value?.UnitId.ToString(CultureInfo.InvariantCulture) ?? "";
            Refresh(true);
        }
    }
    public VirtualModbusRegisterRow? SelectedRegister
    {
        get => _selectedRegister;
        set
        {
            if (_refreshingRegisters && value is null) return;
            if (SetProperty(ref _selectedRegister, value)) { EditValue = value?.CurrentValue ?? ""; NotifyControls(); }
        }
    }

    public async Task ToggleAsync()
    {
        if (!CanToggle) return;
        IsBusy = true;
        try
        {
            if (IsRunning) await runtime.StopAsync(_stop.Token);
            else start();
            Message = "";
        }
        catch (Exception ex) { Report(ex.Message); }
        finally { IsBusy = false; Refresh(true); }
    }

    public async Task ImportAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var definition = await _files.LoadAsync(paths, _stop.Token);
            await Task.Run(() => store.Add(definition), _stop.Token);
            Message = UiText.Format("VmdAdded", "Added {0} device(s).", definition.Devices.Count);
        }
        catch (Exception ex) { Report(ex.Message); }
        finally { IsBusy = false; Refresh(true); }
    }

    public async Task SaveAsync(string path)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var definition = await Task.Run(store.Export, _stop.Token);
            await _files.SaveAsync(path, definition, _stop.Token);
            Message = UiText.Get("VmdSaved", "Saved current IDs and register values.");
        }
        catch (Exception ex) { Report(ex.Message); }
        finally { IsBusy = false; Refresh(); }
    }

    public async Task ImportTextAsync(string text)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var definition = await Task.Run(() => VirtualModbusJson.Parse(text), _stop.Token);
            await Task.Run(() => store.Add(definition), _stop.Token);
            Message = UiText.Format("VmdAdded", "Added {0} device(s).", definition.Devices.Count);
        }
        catch (Exception ex) { Report(ex.Message); }
        finally { IsBusy = false; Refresh(true); }
    }

    public async Task LoadExampleAsync()
    {
        await ImportAsync([Path.Combine(AppContext.BaseDirectory, "docs", "virtual_modbus_device.example.json")]);
    }

    public void ApplyValue()
    {
        if (!CanEdit || SelectedDevice is null || SelectedRegister is null) return;
        try { store.SetValue(SelectedDevice.UnitId, SelectedRegister.Index, EditValue); Message = UiText.Get("VmdApplied", "Value applied. The next request will read the updated value."); Refresh(); }
        catch (Exception ex) { Report(ex.Message); }
    }

    public void ChangeId()
    {
        if (!HasDevice || SelectedDevice is null) return;
        try
        {
            if (!int.TryParse(EditUnitId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) throw new FormatException("Sensor ID must be 1–247.");
            store.ChangeUnitId(SelectedDevice.UnitId, id);
            _selectedDevice = store.Devices().First(d => d.UnitId == id);
            Message = UiText.Get("VmdIdApplied", "Sensor ID changed.");
            Refresh(true);
        }
        catch (Exception ex) { Report(ex.Message); }
    }

    public void Remove()
    {
        if (!HasDevice || SelectedDevice is null) return;
        store.Remove(SelectedDevice.UnitId);
        if (store.Devices().Count == 0) runtime.RequestStop();
        _selectedDevice = null;
        _page = 0;
        SelectedRegister = null;
        Refresh(true);
    }
    public void Previous() { if (CanPrevious) { _page--; SelectedRegister = null; Refresh(true); } }
    public void Next() { if (CanNext) { _page++; SelectedRegister = null; Refresh(true); } }
    public void Report(string message) => Message = message;

    public void Refresh(bool force = false)
    {
        var snapshot = runtime.Snapshot();
        Status = snapshot.Error ?? UiText.Format("VmdStatus", "{0} · RX requests {1:N0} · TX replies {2:N0} · ignored {3:N0} · invalid {4:N0} · exceptions {5:N0} · broadcasts {6:N0} · echoes {7:N0}",
            snapshot.Running ? UiText.Get("VmdRunning", "RUNNING · auto reply ON") : snapshot.Stopping ? UiText.Get("VmdStopping", "Stopping…") : UiText.Get("VmdStopped", "Stopped · auto reply OFF"),
            snapshot.Requests, snapshot.Responses, snapshot.Ignored, snapshot.Invalid, snapshot.Exceptions, snapshot.Broadcasts, snapshot.Echoes);
        if (_lastRunning != snapshot.Running || _lastStopping != snapshot.Stopping)
        {
            _lastRunning = snapshot.Running; _lastStopping = snapshot.Stopping;
            OnPropertyChanged(nameof(IsRunning)); OnPropertyChanged(nameof(IsStopping));
        }
        var version = store.Version;
        if (force || _version != version)
        {
            var devices = store.Devices();
            if (!Devices.SequenceEqual(devices))
            {
                var id = _selectedDevice?.UnitId;
                Devices.ReplaceAll(devices);
                _selectedDevice = devices.FirstOrDefault(d => d.UnitId == id) ?? devices.FirstOrDefault();
                if (_selectedDevice?.UnitId != id) { _page = 0; _selectedRegister = null; EditValue = ""; }
                EditUnitId = _selectedDevice?.UnitId.ToString(CultureInfo.InvariantCulture) ?? "";
                OnPropertyChanged(nameof(SelectedDevice));
            }
            _page = Math.Clamp(_page, 0, Math.Max(0, ((_selectedDevice?.RegisterCount ?? 0) - 1) / 128));
            var points = _selectedDevice is null ? [] : store.Page(_selectedDevice.UnitId, _page * 128);
            if (force || points.Count != Registers.Count || !points.Select(p => p.Index).SequenceEqual(Registers.Select(p => p.Index)))
            {
                var index = _selectedRegister?.Index;
                _refreshingRegisters = true;
                try { Registers.ReplaceAll(points.Select(p => new VirtualModbusRegisterRow(p))); }
                finally { _refreshingRegisters = false; }
                _selectedRegister = Registers.FirstOrDefault(r => r.Index == index);
                OnPropertyChanged(nameof(SelectedRegister));
            }
            else for (var i = 0; i < points.Count; i++) Registers[i].Refresh(points[i].Value);
            _version = version;
        }
        NotifyControls();
    }

    private void NotifyControls()
    {
        foreach (var name in new[] { nameof(StartLabel), nameof(CanOperate), nameof(CanConfigure), nameof(CanToggle), nameof(CanEdit), nameof(HasDevice), nameof(CanPrevious), nameof(CanNext), nameof(PageText) })
            OnPropertyChanged(name);
    }

    public void CancelPendingOperations() => _stop.Cancel();
}
