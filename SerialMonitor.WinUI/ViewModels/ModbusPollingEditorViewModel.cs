using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

// A private, offline workspace draft. Applying the caller's dialog is the only
// operation that replaces live configuration; this model has no serial service.
public sealed class ModbusPollingEditorViewModel : ViewModelBase
{
    private ModbusPollingEditorEntry? _selectedGroup;
    private string? _selectedGroupId;
    private bool _loading, _draftDirty, _enabled = true;
    private bool _hasInvalidMetadata;
    private string _draftId = Guid.NewGuid().ToString("N");
    private string _name = "", _unit = "1", _address = "0", _quantity = "10", _interval = "1000", _timeout = "1000", _scale = "1", _error = "";
    private int _functionIndex = 2, _formatIndex;
    private bool _swapBytes, _swapWords;
    private List<ModbusItemDefinition>? _draftItems = [];

    public ModbusPollingEditorViewModel(ModbusPollingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = settings.Clone();
        _hasInvalidMetadata = copy.HasInvalidMetadata;
        _selectedGroupId = copy.SelectedGroupId;
        if (copy.Groups is null) Groups.Add(new(null));
        else foreach (var group in copy.Groups) Groups.Add(new(group?.Clone()));
        var selected = Groups.FirstOrDefault(entry => entry.Group is { } group && group.Id == copy.SelectedGroupId);
        if (selected is null && copy.SelectedGroupId is null) selected = Groups.FirstOrDefault();
        if (selected is not null) SelectedGroup = selected;
        else ResetDraft();
    }

    private static string T(string key, string fallback) => UiText.Get("ModbusPolling" + key, fallback);
    private static string M(string key, string fallback) => UiText.Get("ModbusMonitor" + key, fallback);
    public string Title => T("Title", "Polling groups");
    public string ApplyLabel => T("Apply", "Apply to monitor");
    public string CancelLabel => T("Cancel", "Cancel");
    public string Help => T("Help", "Define devices and read areas on this connection. Enabled groups are polled in turn. Applying does not send data or save the app profile.");
    public string ListLabel => T("List", "Groups");
    public string NewLabel => T("New", "New group");
    public string DuplicateLabel => T("Duplicate", "Duplicate");
    public string RemoveLabel => T("Remove", "Remove");
    public string ClearLabel => T("Clear", "Clear all");
    public string SaveGroupLabel => T("SaveGroup", "Add / update group");
    public string NameLabel => T("Name", "Group name");
    public string EnabledLabel => T("Enabled", "Include in polling");
    public string ItemsLabel => M("ItemsEditor", "Item definitions…");
    public string UnitLabel => M("Unit", "Unit ID (1–247)");
    public string FunctionLabel => M("Function", "Read function");
    public string AddressLabel => M("Address", "Start address (base 0)");
    public string QuantityLabel => M("Quantity", "Quantity");
    public string IntervalLabel => M("Interval", "Interval (100–60000 ms)");
    public string TimeoutLabel => M("Timeout", "Response timeout (100–10000 ms)");
    public string FormatLabel => M("Format", "Display type");
    public string ScaleLabel => M("Scale", "Display multiplier");
    public string SwapBytesLabel => M("SwapBytes", "Swap bytes");
    public string SwapWordsLabel => M("SwapWords", "Swap words (32-bit)");
    public string DraftHelp => _draftDirty ? T("UnsavedDraft", "Add/update this group before selecting another. Apply also saves the current draft; New discards it.") : "";
    public string ItemsSummary => _draftItems is null
        ? T("InvalidItems", "Invalid saved items · open Item definitions to correct them")
        : UiText.Format("ModbusPollingItemsCount", "Defined items: {0}", _draftItems.Count);
    public bool IsRegister => FunctionIndex is 2 or 3;
    public bool CanSwapWords => IsRegister && FormatIndex >= 2;
    public IReadOnlyList<string> Functions { get; } = ["01 · Coils", "02 · Discrete inputs", "03 · Holding registers", "04 · Input registers"];
    public IReadOnlyList<string> Formats { get; } = ["UInt16", "Int16", "UInt32", "Int32", "Float32"];
    public ObservableCollection<ModbusPollingEditorEntry> Groups { get; } = [];
    public string Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(ErrorVisibility)); } }
    public Visibility ErrorVisibility => Error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public ModbusPollingEditorEntry? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (ReferenceEquals(_selectedGroup, value)) return;
            if (!_loading && _draftDirty)
            {
                Error = T("SaveBeforeSelection", "Add/update this group first, or choose New to discard its draft.");
                OnPropertyChanged();
                return;
            }
            if (!SetProperty(ref _selectedGroup, value)) return;
            if (!_loading && value is not null)
            {
                _selectedGroupId = value.Group?.Id;
                Load(value.Group);
            }
        }
    }

    public string NameText { get => _name; set => SetDraft(ref _name, value ?? "", nameof(NameText)); }
    public bool Enabled { get => _enabled; set => SetDraft(ref _enabled, value, nameof(Enabled)); }
    public string UnitText { get => _unit; set => SetDraft(ref _unit, value ?? "", nameof(UnitText)); }
    public string AddressText { get => _address; set => SetDraft(ref _address, value ?? "", nameof(AddressText)); }
    public string QuantityText { get => _quantity; set => SetDraft(ref _quantity, value ?? "", nameof(QuantityText)); }
    public string IntervalText { get => _interval; set => SetDraft(ref _interval, value ?? "", nameof(IntervalText)); }
    public string TimeoutText { get => _timeout; set => SetDraft(ref _timeout, value ?? "", nameof(TimeoutText)); }
    public string ScaleText { get => _scale; set => SetDraft(ref _scale, value ?? "", nameof(ScaleText)); }
    public int FunctionIndex
    {
        get => _functionIndex;
        set
        {
            SetDraft(ref _functionIndex, value, nameof(FunctionIndex));
            OnPropertyChanged(nameof(IsRegister));
            OnPropertyChanged(nameof(CanSwapWords));
        }
    }
    public int FormatIndex
    {
        get => _formatIndex;
        set { SetDraft(ref _formatIndex, value, nameof(FormatIndex)); OnPropertyChanged(nameof(CanSwapWords)); }
    }
    public bool SwapBytes { get => _swapBytes; set => SetDraft(ref _swapBytes, value, nameof(SwapBytes)); }
    public bool SwapWords { get => _swapWords; set => SetDraft(ref _swapWords, value, nameof(SwapWords)); }

    private void SetDraft<TValue>(ref TValue field, TValue value, string property)
    {
        if (!SetProperty(ref field, value, property) || _loading) return;
        _draftDirty = true;
        Error = "";
        OnPropertyChanged(nameof(DraftHelp));
    }

    public void NewGroup() => ResetDraft();

    private void ResetDraft()
    {
        _loading = true;
        try
        {
            SelectedGroup = null;
            _draftId = Guid.NewGuid().ToString("N");
            NameText = UiText.Format("ModbusPollingNewName", "Group {0}", Groups.Count + 1);
            Enabled = true;
            LoadSettings(new());
            Error = "";
        }
        finally { _loading = false; _draftDirty = false; OnPropertyChanged(nameof(DraftHelp)); }
    }

    private void Load(ModbusPollingGroup? group)
    {
        _loading = true;
        try
        {
            _draftId = group?.Id ?? "";
            NameText = group?.Name ?? "";
            Enabled = group?.Enabled ?? false;
            if (group?.Settings is { } settings) LoadSettings(settings);
            else
            {
                // Missing imported wire settings are unknown, rather than the
                // defaults for a newly requested group. Require explicit input.
                LoadSettings(new());
                UnitText = "";
                AddressText = "";
                QuantityText = "";
                FunctionIndex = -1;
            }
            Error = group is null || group.Settings is null || group.HasInvalidMetadata
                ? T("InvalidGroup", "This saved group is invalid. Correct its fields and add/update it, or remove it.") : "";
        }
        finally { _loading = false; _draftDirty = false; OnPropertyChanged(nameof(DraftHelp)); }
    }

    private void LoadSettings(ModbusMonitorSettings settings)
    {
        UnitText = settings.UnitId.ToString(CultureInfo.InvariantCulture);
        _functionIndex = settings.Function is >= 1 and <= 4 ? settings.Function - 1 : -1;
        OnPropertyChanged(nameof(FunctionIndex));
        OnPropertyChanged(nameof(IsRegister));
        AddressText = settings.Address.ToString(CultureInfo.InvariantCulture);
        QuantityText = settings.Quantity.ToString(CultureInfo.InvariantCulture);
        IntervalText = settings.IntervalMs.ToString(CultureInfo.InvariantCulture);
        TimeoutText = settings.TimeoutMs.ToString(CultureInfo.InvariantCulture);
        FormatIndex = Enum.IsDefined(settings.ValueType) ? (int)settings.ValueType : -1;
        ScaleText = settings.Scale.ToString("G17", CultureInfo.CurrentCulture);
        SwapBytes = settings.SwapBytes;
        SwapWords = settings.SwapWords;
        _draftItems = settings.Items?.Select(item => item?.Clone()!).ToList();
        OnPropertyChanged(nameof(ItemsSummary));
    }

    private ModbusMonitorSettings ReadDraft(bool validateItems)
    {
        if (!ModbusRtuViewModel.TryNumber(UnitText, out var unit) || !ModbusRtuViewModel.TryNumber(AddressText, out var address) ||
            !ModbusRtuViewModel.TryNumber(QuantityText, out var quantity) || FunctionIndex is < 0 or > 3 ||
            !int.TryParse(IntervalText, NumberStyles.None, CultureInfo.InvariantCulture, out var interval) ||
            !int.TryParse(TimeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out var timeout))
            throw new ArgumentException(T("InvalidNumbers", "Enter complete unit, address, quantity and timing values. Unit, address and quantity also accept 0x HEX."));
        if (!double.TryParse(ScaleText, NumberStyles.Float, CultureInfo.CurrentCulture, out var scale) &&
            !double.TryParse(ScaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out scale))
            throw new ArgumentException(T("InvalidScale", "Enter a finite display multiplier, for example 1 or 0.1."));
        var settings = new ModbusMonitorSettings
        {
            UnitId = unit, Function = (byte)(FunctionIndex + 1), Address = address, Quantity = quantity,
            IntervalMs = interval, TimeoutMs = timeout, ValueType = (ModbusValueType)FormatIndex, Scale = scale,
            SwapBytes = SwapBytes, SwapWords = SwapWords, Items = []
        };
        if (!validateItems) settings.Validate();
        settings.Items = _draftItems?.Select(item => item?.Clone()!).ToList()!;
        if (validateItems) settings.Validate();
        return settings;
    }

    public ModbusMonitorSettings? CreateItemEditorSettings()
    {
        try { var settings = ReadDraft(validateItems: false); Error = ""; return settings; }
        catch (ArgumentException ex) { Error = ex.Message; return null; }
    }

    public void ApplyItemDefinitions(IReadOnlyList<ModbusItemDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _draftItems = definitions.Select(item => item?.Clone()!).ToList();
        _draftDirty = true;
        Error = "";
        OnPropertyChanged(nameof(ItemsSummary));
        OnPropertyChanged(nameof(DraftHelp));
    }

    public bool SaveGroup()
    {
        try
        {
            var settings = ReadDraft(validateItems: true);
            var duplicateId = Groups.Any(entry => !ReferenceEquals(entry, SelectedGroup) && entry.Group?.Id == _draftId);
            var validId = !string.IsNullOrWhiteSpace(_draftId) && _draftId.Length <= ModbusPollingGroup.MaximumIdLength &&
                _draftId == _draftId.Trim() && !_draftId.Any(char.IsControl) && !duplicateId;
            var group = new ModbusPollingGroup
            {
                Id = validId ? _draftId : Guid.NewGuid().ToString("N"), Name = NameText.Trim(), Enabled = Enabled, Settings = settings
            };
            group.Validate();
            var index = SelectedGroup is null ? -1 : Groups.IndexOf(SelectedGroup);
            if (index < 0 && Groups.Count >= ModbusPollingSettings.MaximumGroups)
                throw new ArgumentException(T("TooManyGroups", "At most 32 polling groups are allowed."));
            var otherCells = Groups.Where(entry => !ReferenceEquals(entry, SelectedGroup)).Sum(entry => (long)Math.Max(0, entry.Group?.Settings?.Quantity ?? 0));
            if (otherCells + settings.Quantity > ModbusPollingSettings.MaximumTotalCells)
                throw new ArgumentException(T("TooManyCells", "The combined read areas cannot exceed 8192 bits/registers."));
            var replacement = new ModbusPollingEditorEntry(group);
            _hasInvalidMetadata = false;
            _loading = true;
            try
            {
                if (index < 0) Groups.Add(replacement); else Groups[index] = replacement;
                SelectedGroup = replacement;
                _selectedGroupId = group.Id;
            }
            finally { _loading = false; }
            Load(group);
            return true;
        }
        catch (ArgumentException ex) { Error = ex.Message; return false; }
    }

    public bool DuplicateGroup()
    {
        if (_draftDirty && !SaveGroup()) return false;
        if (SelectedGroup?.Group is not { } selected) return false;
        try
        {
            selected.Validate();
            if (Groups.Count >= ModbusPollingSettings.MaximumGroups)
                throw new ArgumentException(T("TooManyGroups", "At most 32 polling groups are allowed."));
            var copy = selected.Clone();
            copy.Id = Guid.NewGuid().ToString("N");
            var suffix = T("CopySuffix", " copy");
            copy.Name = copy.Name.Length + suffix.Length <= ModbusPollingGroup.MaximumNameLength
                ? copy.Name + suffix : UiText.Format("ModbusPollingNewName", "Group {0}", Groups.Count + 1);
            var candidate = BuildResult();
            candidate.HasInvalidMetadata = false;
            candidate.Groups.Add(copy);
            candidate.SelectedGroupId = copy.Id;
            candidate.Validate();
            var entry = new ModbusPollingEditorEntry(copy);
            _hasInvalidMetadata = false;
            _loading = true;
            try { Groups.Add(entry); SelectedGroup = entry; _selectedGroupId = copy.Id; }
            finally { _loading = false; }
            Load(copy);
            return true;
        }
        catch (ArgumentException ex) { Error = ex.Message; return false; }
    }

    public void RemoveGroup()
    {
        if (SelectedGroup is not { } selected) return;
        _hasInvalidMetadata = false;
        _loading = true;
        try { Groups.Remove(selected); SelectedGroup = null; }
        finally { _loading = false; }
        ResetDraft();
        var next = Groups.FirstOrDefault();
        if (next is not null) SelectedGroup = next;
        else _selectedGroupId = null;
    }

    public void ClearGroups()
    {
        _hasInvalidMetadata = false;
        _loading = true;
        try { Groups.Clear(); SelectedGroup = null; _selectedGroupId = null; }
        finally { _loading = false; }
        ResetDraft();
    }

    private ModbusPollingSettings BuildResult() => new()
    {
        Groups = Groups.Select(entry => entry.Group?.Clone()!).ToList(), SelectedGroupId = _selectedGroupId,
        HasInvalidMetadata = _hasInvalidMetadata
    };

    public bool TryApply(out ModbusPollingSettings settings)
    {
        settings = new();
        if (_draftDirty && !SaveGroup()) return false;
        try
        {
            var candidate = BuildResult();
            candidate.Validate();
            settings = candidate;
            Error = "";
            return true;
        }
        catch (ArgumentException ex) { Error = ex.Message; return false; }
    }
}

public sealed class ModbusPollingEditorEntry(ModbusPollingGroup? group)
{
    public ModbusPollingGroup? Group { get; } = group;
    public string DisplayName => Group is not { Settings: { } settings } item
        ? UiText.Get("ModbusPollingInvalidGroupShort", "Invalid saved group")
        : $"{(string.IsNullOrWhiteSpace(item.Name) ? "—" : item.Name)} · ID {settings.UnitId} · FC {settings.Function:00} · {settings.Address} + {settings.Quantity}" +
            (item.HasInvalidMetadata ? " · " + UiText.Get("ModbusPollingInvalidGroupShort", "Invalid saved group") : "");
}
