using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

// Edits a private definition list. No connection, live values or transmit state
// belongs to this editor; applying the dialog is an explicit parent operation.
public sealed class ModbusItemEditorViewModel : ViewModelBase
{
    private readonly ModbusMonitorSettings _settings;
    private ModbusItemEditorEntry? _selectedItem;
    private bool _loading, _draftDirty;
    private string _address = "", _name = "", _scale = "1", _unit = "", _valueNames = "", _error = "";
    private int _formatIndex, _accessIndex;
    private bool _swapBytes, _swapWords;

    public ModbusItemEditorViewModel(ModbusMonitorSettings settings, int? selectedAddress = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Clone();
        if (_settings.Items is null) Items.Add(new(null));
        else foreach (var definition in _settings.Items) Items.Add(new(definition?.Clone()));
        var selected = selectedAddress is { } address
            ? Items.FirstOrDefault(entry => entry.Definition is { } definition && address >= definition.Address &&
                (long)address < (long)definition.Address + definition.RegisterCount)
            : null;
        if (selected is not null) SelectedItem = selected;
        else ResetDraft(selectedAddress ?? settings.Address);
    }

    private static string T(string key, string fallback) => UiText.Get("ModbusItem" + key, fallback);
    public string Title => T("Title", "Item definitions");
    public string ApplyLabel => T("Apply", "Apply");
    public string CancelLabel => T("Cancel", "Cancel");
    public string Help => T("Help", "Define names and formats inside the current read area. Undefined addresses keep the default format. Applying does not save the app profile or send data.");
    public string RangeSummary => $"ID {_settings.UnitId} · FC {_settings.Function:00} · {_settings.Address}–{(long)_settings.Address + _settings.Quantity - 1}";
    public string ListLabel => T("List", "Defined items");
    public string NewLabel => T("New", "New");
    public string SaveItemLabel => T("SaveItem", "Add / update item");
    public string RemoveLabel => T("Remove", "Remove");
    public string ClearLabel => T("Clear", "Clear all");
    public string AddressLabel => T("Address", "Address (base 0)");
    public string NameLabel => T("Name", "Name");
    public string FormatLabel => T("Format", "Value type");
    public string ScaleLabel => T("Scale", "Multiplier");
    public string UnitLabel => T("Unit", "Unit");
    public string AccessLabel => T("Access", "Access");
    public string SwapBytesLabel => T("SwapBytes", "Swap bytes");
    public string SwapWordsLabel => T("SwapWords", "Swap words (32-bit)");
    public string ValueNamesLabel => T("ValueNames", "Value names (one raw value = name per line)");
    public string ValueNamesHelp => T("ValueNamesHelp", "Example: 0=Manual, 1=Automatic on separate lines. Values refer to decoded numbers before the multiplier. Leave blank for numeric display.");
    public string DraftHelp => _draftDirty ? T("UnsavedDraft", "This item has unsaved edits. Add/update it before selecting another item. Apply also adds or updates the current draft.") : "";
    public bool IsRegister => _settings.Function is 3 or 4;
    // An invalid imported writable input must remain correctable. Once it is
    // read-only, the input-area selector is locked to that valid choice.
    public bool CanChooseAccess => _settings.Function is 1 or 3 || AccessIndex != (int)ModbusItemAccess.ReadOnly;
    public bool CanSwapWords => IsRegister && FormatIndex >= 2;
    public IReadOnlyList<string> Formats { get; } = ["UInt16", "Int16", "UInt32", "Int32", "Float32"];
    public IReadOnlyList<string> AccessOptions => [T("ReadOnly", "Read only"), T("ReadWrite", "Read / write")];
    public ObservableCollection<ModbusItemEditorEntry> Items { get; } = [];
    public string Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(ErrorVisibility)); } }
    public Visibility ErrorVisibility => Error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public ModbusItemEditorEntry? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (ReferenceEquals(_selectedItem, value)) return;
            if (!_loading && _draftDirty)
            {
                Error = T("SaveBeforeSelection", "Add/update this item first, or choose New to discard its draft.");
                OnPropertyChanged();
                return;
            }
            if (!SetProperty(ref _selectedItem, value)) return;
            if (!_loading && value is not null) Load(value.Definition);
        }
    }

    public string AddressText { get => _address; set => SetDraft(ref _address, value ?? "", nameof(AddressText)); }
    public string NameText { get => _name; set => SetDraft(ref _name, value ?? "", nameof(NameText)); }
    public string ScaleText { get => _scale; set => SetDraft(ref _scale, value ?? "", nameof(ScaleText)); }
    public string UnitText { get => _unit; set => SetDraft(ref _unit, value ?? "", nameof(UnitText)); }
    public string ValueNamesText { get => _valueNames; set => SetDraft(ref _valueNames, value ?? "", nameof(ValueNamesText)); }
    public int FormatIndex
    {
        get => _formatIndex;
        set
        {
            SetDraft(ref _formatIndex, value, nameof(FormatIndex));
            OnPropertyChanged(nameof(CanSwapWords));
        }
    }
    public int AccessIndex
    {
        get => _accessIndex;
        set
        {
            SetDraft(ref _accessIndex, value, nameof(AccessIndex));
            OnPropertyChanged(nameof(CanChooseAccess));
        }
    }
    public bool SwapBytes { get => _swapBytes; set => SetDraft(ref _swapBytes, value, nameof(SwapBytes)); }
    public bool SwapWords { get => _swapWords; set => SetDraft(ref _swapWords, value, nameof(SwapWords)); }

    private void SetDraft<TValue>(ref TValue field, TValue value, string property)
    {
        if (!SetProperty(ref field, value, property) || _loading) return;
        _draftDirty = true;
        OnPropertyChanged(nameof(DraftHelp));
        Error = "";
    }

    public void NewItem() => ResetDraft(NextAddress());

    private void ResetDraft(int address)
    {
        _loading = true;
        try
        {
            SelectedItem = null;
            AddressText = address.ToString(CultureInfo.InvariantCulture);
            NameText = "";
            FormatIndex = IsRegister && Enum.IsDefined(_settings.ValueType) ? (int)_settings.ValueType : 0;
            ScaleText = (IsRegister ? _settings.Scale : 1).ToString("R", CultureInfo.CurrentCulture);
            UnitText = "";
            AccessIndex = 0;
            SwapBytes = IsRegister && _settings.SwapBytes;
            SwapWords = IsRegister && _settings.SwapWords;
            ValueNamesText = "";
            Error = "";
        }
        finally { _loading = false; _draftDirty = false; OnPropertyChanged(nameof(DraftHelp)); }
    }

    private int NextAddress()
    {
        var address = _settings.Address;
        foreach (var entry in Items.Where(entry => entry.Definition is not null).OrderBy(entry => entry.Definition!.Address))
        {
            var definition = entry.Definition!;
            if (definition.Address > address) break;
            if (definition.Address >= _settings.Address && (long)definition.Address + definition.RegisterCount > address)
                address = (int)Math.Min(65535, (long)definition.Address + definition.RegisterCount);
        }
        return (long)address < (long)_settings.Address + _settings.Quantity ? address : _settings.Address;
    }

    private void Load(ModbusItemDefinition? definition)
    {
        if (definition is null)
        {
            var invalidEntry = _selectedItem;
            ResetDraft(_settings.Address);
            _loading = true;
            try { SelectedItem = invalidEntry; }
            finally { _loading = false; }
            Error = T("InvalidEntry", "This saved definition is invalid. Remove it or clear the list, then define the item again.");
            return;
        }
        _loading = true;
        try
        {
            AddressText = definition.Address.ToString(CultureInfo.InvariantCulture);
            NameText = definition.Name ?? "";
            FormatIndex = Enum.IsDefined(definition.ValueType) ? (int)definition.ValueType : -1;
            ScaleText = definition.Scale.ToString("R", CultureInfo.CurrentCulture);
            UnitText = definition.Unit ?? "";
            AccessIndex = Enum.IsDefined(definition.Access) ? (int)definition.Access : -1;
            SwapBytes = definition.SwapBytes;
            SwapWords = definition.SwapWords;
            ValueNamesText = definition.ValueNames is null ? "" : string.Join(Environment.NewLine,
                definition.ValueNames.Select(value => value is null ? "?=" : $"{value.Value.ToString(CultureInfo.InvariantCulture)}={value.Name}"));
            Error = "";
        }
        finally { _loading = false; _draftDirty = false; OnPropertyChanged(nameof(DraftHelp)); }
    }

    public bool SaveItem()
    {
        try
        {
            var definition = ReadDraft();
            // Validate the new item independently so a second malformed saved
            // item cannot prevent correcting the first one.
            var candidate = _settings.Clone();
            candidate.Items = [definition];
            candidate.Validate();
            foreach (var entry in Items)
            {
                if (ReferenceEquals(entry, SelectedItem) || entry.Definition is not { } other) continue;
                if ((long)definition.Address < (long)other.Address + other.RegisterCount &&
                    (long)other.Address < (long)definition.Address + definition.RegisterCount)
                    throw new ArgumentException(T("Overlap", "This item overlaps another definition. Remove or edit that definition first."));
            }
            var replacement = new ModbusItemEditorEntry(definition);
            var index = SelectedItem is null ? -1 : Items.IndexOf(SelectedItem);
            if (index < 0 && Items.Count >= _settings.Quantity)
                throw new ArgumentException(T("TooMany", "The number of items cannot exceed the size of the read area."));
            _loading = true;
            try
            {
                if (index < 0) Items.Add(replacement);
                else Items[index] = replacement;
                SelectedItem = replacement;
            }
            finally { _loading = false; }
            Load(definition);
            return true;
        }
        catch (ArgumentException ex) { Error = ex.Message; return false; }
    }

    private ModbusItemDefinition ReadDraft()
    {
        if (!ModbusRtuViewModel.TryNumber(AddressText, out var address))
            throw new ArgumentException(T("InvalidAddress", "Enter a complete address from 0 to 65535, or 0x HEX."));
        if (!double.TryParse(ScaleText, NumberStyles.Float, CultureInfo.CurrentCulture, out var scale) &&
            !double.TryParse(ScaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out scale))
            throw new ArgumentException(T("InvalidScale", "Enter a finite multiplier, for example 1 or 0.1."));
        var names = new List<ModbusValueName>();
        if (ValueNamesText.Length > 20000)
            throw new ArgumentException(T("InvalidValueNames", "Use at most 128 value names, one integer = name per line."));
        foreach (var line in ValueNamesText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = line.IndexOf('=');
            if (equals <= 0) throw new ArgumentException(T("InvalidValueNames", "Use at most 128 value names, one integer = name per line."));
            var keyText = line[..equals].Trim();
            var isHex = keyText.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (!long.TryParse(isHex ? keyText[2..] : keyText,
                isHex ? NumberStyles.AllowHexSpecifier : NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var key) || (isHex && key < 0))
                throw new ArgumentException(T("InvalidValueNames", "Use at most 128 value names, one integer = name per line."));
            names.Add(new() { Value = key, Name = line[(equals + 1)..].Trim() });
            if (names.Count > 128) throw new ArgumentException(T("InvalidValueNames", "Use at most 128 value names, one integer = name per line."));
        }
        return new()
        {
            Address = address, Name = NameText.Trim(), ValueType = (ModbusValueType)FormatIndex, Scale = scale,
            Unit = UnitText.Trim(), Access = (ModbusItemAccess)AccessIndex, SwapBytes = SwapBytes, SwapWords = SwapWords,
            ValueNames = names
        };
    }

    public void RemoveItem()
    {
        if (SelectedItem is not { } selected) return;
        _loading = true;
        try { Items.Remove(selected); SelectedItem = null; }
        finally { _loading = false; }
        ResetDraft(NextAddress());
    }

    public void ClearItems()
    {
        _loading = true;
        try { Items.Clear(); SelectedItem = null; }
        finally { _loading = false; }
        ResetDraft(_settings.Address);
    }

    public bool TryApply(out IReadOnlyList<ModbusItemDefinition> items)
    {
        items = [];
        if (_draftDirty && !SaveItem()) return false;
        try
        {
            var candidate = _settings.Clone();
            candidate.Items = Items.Select(entry => entry.Definition?.Clone()!).ToList();
            candidate.Validate();
            items = candidate.Items;
            Error = "";
            return true;
        }
        catch (ArgumentException ex) { Error = ex.Message; return false; }
    }
}

public sealed class ModbusItemEditorEntry(ModbusItemDefinition? definition)
{
    public ModbusItemDefinition? Definition { get; } = definition;
    public string Summary => Definition is not { } item
        ? UiText.Get("ModbusItemInvalidEntryShort", "Invalid saved definition")
        : $"{item.Address} · {(string.IsNullOrWhiteSpace(item.Name) ? "—" : item.Name)} · {item.ValueType}";
}
