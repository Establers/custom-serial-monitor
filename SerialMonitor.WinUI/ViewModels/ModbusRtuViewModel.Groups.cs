using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class ModbusRtuViewModel
{
    private ModbusPollingSettings? _pollingSettings;
    private ModbusPollingGroupRow? _selectedPollingGroup;
    private bool _loadingPollingGroup, _groupRunAdmitted, _acceptPollingSnapshots, _replacingPollingGroups;
    private string? _singleGroupId;
    private long _lastPollingRevision = long.MinValue;
    private string _pollingSummary = "", _pollingHealthSummary = "";

    private static string P(string key, string fallback) => UiText.Get("ModbusPolling" + key, fallback);
    public string GroupEditorLabel => P("Editor", "Polling groups…");
    public string GroupSelectorLabel => P("Selector", "Selected group");
    public ObservableCollection<ModbusPollingGroupRow> PollingGroups { get; } = [];
    public string PollingSummary { get => _pollingSummary; private set => SetProperty(ref _pollingSummary, value); }
    public string PollingHealthSummary { get => _pollingHealthSummary; private set { if (SetProperty(ref _pollingHealthSummary, value)) NotifyMonitorSummary(); } }
    public bool NeedsPollingGroup => _pollingSettings is not null && SelectedPollingGroup?.Definition?.Settings is null;
    public string EmptySetupLabel => NeedsPollingGroup ? GroupEditorLabel : SettingsLabel;
    public bool IsGroupSelectionEnabled => !_writeAdmitted && (!_operationAdmitted || _groupRunAdmitted);
    public Visibility PollingGroupsVisibility => _pollingSettings is null ? Visibility.Collapsed : Visibility.Visible;

    public ModbusPollingGroupRow? SelectedPollingGroup
    {
        get => _selectedPollingGroup;
        set
        {
            if (_replacingPollingGroups || ReferenceEquals(value, _selectedPollingGroup)) return;
            if (!IsGroupSelectionEnabled || (value is not null && !PollingGroups.Contains(value))) return;
            try { CaptureSelectedGroupDraft(strict: true); }
            catch (ArgumentException ex) { Error = ex.Message; OnPropertyChanged(); return; }
            _selectedPollingGroup = value;
            if (_pollingSettings is not null) _pollingSettings.SelectedGroupId = value?.Id;
            OnPropertyChanged();
            LoadSelectedGroup();
        }
    }

    private bool CanStartPolling() => CanRun() && (_pollingSettings is null || PollingGroups.Any(row => row.Definition?.Enabled == true));
    private bool CanReadSelected() => CanRun() && (_pollingSettings is null || SelectedPollingGroup?.Definition?.Settings is not null);
    private bool CanWriteSelectedGroup()
    {
        if (_pollingSettings is null) return true;
        if (SelectedPollingGroup?.Definition is not { Enabled: true, Settings: not null }) return false;
        return !_groupRunAdmitted || SelectedPollingGroup.Snapshot is { IsPolling: true, State: not ModbusMonitorState.Error };
    }

    private void ResetPollingContext()
    {
        _pollingSettings = null;
        _acceptPollingSnapshots = false;
        _selectedPollingGroup = null;
        _singleGroupId = null;
        _lastPollingRevision = long.MinValue;
        PollingGroups.Clear();
        PollingSummary = "";
        PollingHealthSummary = "";
        NotifyPollingPresentation();
    }

    public void ApplySavedPollingSettings(ModbusPollingSettings? settings)
    {
        if (IsBusy) throw new InvalidOperationException(T("StopBeforeSettings", "Stop Modbus before loading settings."));
        if (settings is null) { ResetPollingContext(); return; }
        _pollingSettings = settings.Clone();
        _acceptPollingSnapshots = false;
        _singleGroupId = null;
        _lastPollingRevision = long.MinValue;
        _replacingPollingGroups = true;
        try
        {
            PollingGroups.Clear();
            if (_pollingSettings.Groups is not null)
                foreach (var group in _pollingSettings.Groups) PollingGroups.Add(new(group));
            _selectedPollingGroup = PollingGroups.FirstOrDefault(row => row.Id is not null && row.Id == _pollingSettings.SelectedGroupId);
        }
        finally { _replacingPollingGroups = false; }
        LoadSelectedGroup();
        try { _pollingSettings.Validate(); }
        catch (ArgumentException ex) { Error = ex.Message; }
        NotifyPollingPresentation();
    }

    public void ApplyPollingSettings(ModbusPollingSettings settings, long? expectedRevision = null)
    {
        if (!IsSettingsEnabled) throw new InvalidOperationException(T("StopBeforeSettings", "Stop Modbus before loading settings."));
        if (expectedRevision is { } revision && revision != SettingsRevision)
            throw new InvalidOperationException(T("ItemTargetChanged", "The Modbus settings changed while editing. Reopen item definitions for the current target."));
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        ApplySavedPollingSettings(settings);
    }

    public ModbusPollingSettings CreatePollingEditorSettings()
    {
        if (_pollingSettings is null)
        {
            var group = new ModbusPollingGroup { Name = P("DefaultGroup", "Group 1"), Settings = CreatePersistableSettingsSnapshot() };
            return new() { Groups = [group], SelectedGroupId = group.Id };
        }
        CaptureSelectedGroupDraft(strict: false);
        return _pollingSettings.Clone();
    }

    public ModbusPollingSettings? CreatePersistablePollingSettingsSnapshot()
    {
        if (_pollingSettings is null) return null;
        CaptureSelectedGroupDraft(strict: false);
        return _pollingSettings.Clone();
    }

    private void CaptureSelectedGroupDraft(bool strict)
    {
        if (_loadingPollingGroup || IsBusy || SelectedPollingGroup?.Definition is not { } group) return;
        try
        {
            var settings = CreateSettingsSnapshot();
            if (group.Settings?.Request != settings.Request)
            {
                SelectedPollingGroup.Snapshot = null;
                if (_singleGroupId == group.Id) _singleGroupId = null;
                _acceptPollingSnapshots = false;
            }
            group.Settings = settings;
            SelectedPollingGroup.RefreshDefinitionPresentation();
            _lastPersistableSettings = settings.Clone();
        }
        catch (ArgumentException) when (!strict) { }
    }

    private void LoadSelectedGroup()
    {
        _settingsRevision++;
        _displaySettings = null;
        Rows.Clear();
        SelectedRow = null;
        Statistics = "";
        Error = "";
        if (SelectedPollingGroup?.Definition?.Settings is { } settings)
        {
            _loadingPollingGroup = true;
            try
            {
                ApplySettings(settings);
                settings.Validate();
                RebuildRows(settings);
                _displaySettings = settings.Clone();
            }
            catch (ArgumentException ex) { Error = ex.Message; }
            finally { _loadingPollingGroup = false; }
        }
        else Status = P("SelectGroup", "Select a group");
        _lastRevision = long.MinValue;
        NotifyPollingPresentation();
        Refresh();
        PublishAvailability();
    }

    private async Task RunPollingGroupsAsync()
    {
        if (!CanRun() || _service is not IModbusRtuGroupMonitorService groups)
        {
            Error = P("Unavailable", "Connect a serial port and stop other operations before polling groups.");
            return;
        }
        ModbusPollingSettings settings;
        try
        {
            CaptureSelectedGroupDraft(strict: true);
            settings = _pollingSettings!.Clone();
            settings.Validate(requireEnabled: true);
        }
        catch (ArgumentException ex) { Error = ex.Message; return; }
        if (SelectedPollingGroup?.Definition?.Settings is { } selectedSettings)
        {
            RebuildRows(selectedSettings);
            _displaySettings = selectedSettings.Clone();
            _lastRevision = long.MinValue;
        }
        ReleaseCommandHeader();
        _operationAdmitted = true;
        _groupRunAdmitted = true;
        _acceptPollingSnapshots = false;
        _singleGroupId = null;
        _lastPollingRevision = long.MinValue;
        Error = "";
        foreach (var row in PollingGroups) row.Snapshot = null;
        PublishAvailability();
        try
        {
            var running = groups.RunGroupsAsync(settings, CancellationToken.None);
            _acceptPollingSnapshots = true;
            await running;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; RuntimeDiagnostics.RecordError("Modbus.Groups", ex); }
        finally
        {
            Refresh();
            _operationAdmitted = false;
            _groupRunAdmitted = false;
            Refresh();
            PublishAvailability();
        }
    }

    private void RefreshPollingGroups()
    {
        if (_pollingSettings is null) return;
        if (_acceptPollingSnapshots && _service is IModbusRtuGroupMonitorService groups)
        {
            var snapshot = groups.GetPollingSnapshot();
            if (_lastPollingRevision != snapshot.Revision)
            {
                _lastPollingRevision = snapshot.Revision;
                foreach (var group in snapshot.Groups)
                {
                    var row = PollingGroups.FirstOrDefault(row => row.Id == group.GroupId);
                    if (row is not null && (_groupRunAdmitted || row.Id != _singleGroupId)) row.Snapshot = group.Snapshot;
                }
            }
        }
        if (!_groupRunAdmitted && _singleGroupId is not null)
        {
            var row = PollingGroups.FirstOrDefault(row => row.Id == _singleGroupId);
            if (row is not null) row.Snapshot = _service.GetSnapshot();
        }
        PollingSummary = string.Join("\n", PollingGroups.Select(row => row.Summary));
        var errors = PollingGroups.Where(row => row.HasError).ToArray();
        var disabled = PollingGroups.Count(row => !row.HasError && !row.IsEnabled);
        var current = PollingGroups.Count(row => !row.HasError && row.IsEnabled && row.Snapshot?.ValuesCurrent == true);
        var health = UiText.Format("ModbusPollingHealth", "Groups {0} · Errors {1} · Current {2} · Waiting/stale {3} · Disabled {4}",
            PollingGroups.Count, errors.Length, current, PollingGroups.Count - errors.Length - disabled - current, disabled);
        if (errors.Length > 0)
        {
            var names = string.Join(", ", errors.Take(3).Select(row => string.IsNullOrWhiteSpace(row.Name) ? row.Id ?? "—" : row.Name));
            health += " · " + names + (errors.Length > 3 ? $" (+{errors.Length - 3})" : "");
        }
        PollingHealthSummary = health;
    }

    private ModbusMonitorSnapshot GetSelectedSnapshot()
    {
        if (_pollingSettings is null) return _service.GetSnapshot();
        return SelectedPollingGroup?.Snapshot ?? new(0, ModbusMonitorState.Idle, IsBusy, _groupRunAdmitted,
            null, null, 0, 0, null, null, false);
    }

    private void MarkOtherGroupsStale(string? groupId)
    {
        foreach (var row in PollingGroups)
            if (row.Id != groupId && row.Snapshot is { } snapshot)
                row.Snapshot = snapshot with { ValuesCurrent = false };
    }

    private void PrepareSingleGroupOperation(string? groupId)
    {
        _singleGroupId = null;
        if (groupId is not null && PollingGroups.FirstOrDefault(row => row.Id == groupId) is { } row)
            row.Snapshot = null;
        MarkOtherGroupsStale(groupId);
    }

    private void NotifyPollingPresentation()
    {
        foreach (var name in new[] { nameof(SelectedPollingGroup), nameof(PollingGroupsVisibility), nameof(StartLabel), nameof(RequestSummary), nameof(IsGroupSelectionEnabled), nameof(NeedsPollingGroup), nameof(EmptySetupLabel) })
            OnPropertyChanged(name);
    }
}

public sealed class ModbusPollingGroupRow(ModbusPollingGroup? definition) : ViewModelBase
{
    internal ModbusPollingGroup? Definition { get; } = definition;
    private ModbusMonitorSnapshot? _snapshot;
    private string? _configurationError = ValidateDefinition(definition);
    public string? Id => Definition?.Id;
    public string DisplayName => Definition is null ? UiText.Get("ModbusPollingInvalidGroupShort", "Invalid group")
        : $"{Definition.Name} · ID {Definition.Settings?.UnitId} · FC {Definition.Settings?.Function:00} · {Definition.Settings?.Address}+{Definition.Settings?.Quantity}";
    public string Name => Definition?.Name ?? "";
    public bool IsEnabled => Definition?.Enabled == true;
    public bool HasError => _configurationError is not null || Snapshot?.State is ModbusMonitorState.Error or ModbusMonitorState.Disconnected || !string.IsNullOrWhiteSpace(Snapshot?.Error);
    public string SelectionLabel => $"{SelectionState} · {DisplayName}";
    private string SelectionState => HasError ? UiText.Get("ModbusPollingStateError", "Error")
        : !IsEnabled ? UiText.Get("ModbusPollingDisabled", "Disabled")
        : Snapshot?.ValuesCurrent == true ? UiText.Get("ModbusPollingStateCurrent", "Read OK")
        : Snapshot?.IsBusy == true ? UiText.Get("ModbusPollingStateReading", "Reading")
        : Snapshot?.UpdatedAt is not null ? UiText.Get("ModbusPollingStateStale", "Stale")
        : UiText.Get("ModbusPollingStateWaiting", "Ready");
    public string StatusText => _configurationError ?? Snapshot?.Error ?? (Snapshot?.State switch
    {
        ModbusMonitorState.Polling => UiText.Get("ModbusMonitorPolling", "Polling · connection reserved"),
        ModbusMonitorState.Reading => UiText.Get("ModbusMonitorReading", "Reading"),
        ModbusMonitorState.Error => UiText.Get("ModbusMonitorErrorState", "Communication error · retained values are stale"),
        ModbusMonitorState.Disconnected => UiText.Get("ModbusMonitorDisconnected", "Disconnected · restart explicitly after connecting"),
        ModbusMonitorState.Stopped => Snapshot?.ValuesCurrent == true
            ? UiText.Get("ModbusMonitorReadComplete", "Read complete · last response shown")
            : UiText.Get("ModbusMonitorStopped", "Stopped · retained values are stale"),
        _ => IsEnabled ? UiText.Get("ModbusMonitorReady", "Ready · settings do not start communication") : UiText.Get("ModbusPollingDisabled", "Disabled")
    });
    public string ValueSummary => Snapshot?.Values is not { Length: > 0 } values ? "—" : string.Join(" ", values.Take(4).Select(value => value.ToString(System.Globalization.CultureInfo.CurrentCulture))) + (values.Length > 4 ? " …" : "");
    public string Summary => $"{DisplayName} · {StatusText} · [{ValueSummary}] · OK {Snapshot?.SuccessCount ?? 0} / ERR {Snapshot?.ErrorCount ?? 0}";
    public ModbusMonitorSnapshot? Snapshot
    {
        get => _snapshot;
        internal set
        {
            if (!SetProperty(ref _snapshot, value)) return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(SelectionLabel));
            OnPropertyChanged(nameof(ValueSummary));
            OnPropertyChanged(nameof(Summary));
        }
    }
    internal void RefreshDefinitionPresentation()
    {
        _configurationError = ValidateDefinition(Definition);
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SelectionLabel));
        OnPropertyChanged(nameof(Summary));
    }
    private static string? ValidateDefinition(ModbusPollingGroup? definition)
    {
        if (definition is null) return UiText.Get("ModbusPollingInvalidGroupShort", "Invalid group");
        try { definition.Validate(); return null; }
        catch (ArgumentException ex) { return ex.Message; }
    }
}
