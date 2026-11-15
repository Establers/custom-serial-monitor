using System.Globalization;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class ModbusRtuViewModel
{
    private List<ModbusItemDefinition> _itemDefinitions = [];
    private int _writeModeIndex;
    private long _settingsRevision;
    public long SettingsRevision => _settingsRevision;

    public string ItemsEditorLabel => T("ItemsEditor", "Names and units…");
    public string TableNameLabel => T("TableName", "Name");
    public string WriteModeLabel => T("WriteMode", "Write format");
    public IReadOnlyList<string> WriteModes => [T("WriteModeRaw", "Raw number (before scaling)"), T("WriteModeItem", "Defined item value")];
    public int WriteModeIndex
    {
        get => _writeModeIndex;
        set
        {
            if (_writeAdmitted || value is < 0 or > 1 || !SetProperty(ref _writeModeIndex, value)) return;
            ClearWriteFeedback();
            // Switching formats is explicit; no reinterpretation of an old draft.
            _writeValues = "";
            if (SelectedRow is not null) SynchronizeSelectedRowEditor();
            OnPropertyChanged(nameof(WriteValuesText));
            NotifyWritePresentation();
        }
    }

    public string WriteItemSummary
    {
        get
        {
            if (WriteModeIndex != 1) return "";
            var item = FindWriteItem();
            return item is null ? T("WriteItemSelect", "Select a defined item for interpreted writes.")
                : $"{item.Name} · {item.Address} · {item.ValueType} × {item.Scale.ToString("G", CultureInfo.CurrentCulture)} {item.Unit} · {item.Access}";
        }
    }

    public string WritePreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(WriteValuesText)) return "";
            try
            {
                var settings = CreateSettingsSnapshot();
                var (address, values) = BuildWriteValues(settings);
                var function = settings.Function == 1 ? (values.Length == 1 ? (byte)5 : (byte)15)
                    : values.Length == 1 ? (byte)6 : (byte)16;
                var packet = values.Length == 1 ? ModbusRtuRequestBuilder.WriteSingle(settings.UnitId, function, address, values[0])
                    : ModbusRtuRequestBuilder.WriteMultiple(settings.UnitId, function, address, values);
                var words = string.Join(" ", values.Select(word => $"0x{word:X4}"));
                var preview = UiText.Format("ModbusMonitorWritePreview", "Wire words: {0}", words);
                return preview + "\nHEX: " + string.Join(" ", packet.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return ex.Message; }
        }
    }

    public void ApplyItemDefinitions(IReadOnlyList<ModbusItemDefinition> definitions, long? expectedRevision = null)
    {
        if (!IsSettingsEnabled) throw new InvalidOperationException(T("StopBeforeSettings", "Stop Modbus before loading settings."));
        if (expectedRevision is { } revision && revision != SettingsRevision)
            throw new InvalidOperationException(T("ItemTargetChanged", "The Modbus settings changed while editing. Reopen item definitions for the current target."));
        ArgumentNullException.ThrowIfNull(definitions);
        var settings = CreateItemEditorSettings();
        settings.Items = definitions.Select(item => item?.Clone()!).ToList();
        settings.Validate();
        _itemDefinitions = CloneItemDefinitions(settings.Items);
        _lastPersistableSettings = settings.Clone();
        SettingsChanged();
    }

    public void ReportItemEditorError(string message) => Error = message;

    private static List<ModbusItemDefinition> CloneItemDefinitions(List<ModbusItemDefinition>? items) =>
        items?.Select(item => item?.Clone()!).ToList()!;

    private ModbusItemDefinition? FindWriteItem()
    {
        if (!TryNumber(WriteAddressText, out var address)) return null;
        return _itemDefinitions?.FirstOrDefault(item => item is not null && item.Address == address);
    }

    private (int Address, ushort[] Values) BuildWriteValues(ModbusMonitorSettings settings)
    {
        if (!TryNumber(WriteAddressText, out var address)) throw new ArgumentException(T("InvalidWriteAddress", "Enter a complete write address (base 0)."));
        if (WriteValuesText.Length > 16000) throw new ArgumentException(T("InvalidWriteValues", "Enter raw values separated by spaces or commas. Check the quantity and numeric range."));
        ushort[] values;
        if (WriteModeIndex == 1)
        {
            var item = settings.Items.FirstOrDefault(item => item.Address == address)
                ?? throw new ArgumentException(T("WriteItemSelect", "Select a defined item for interpreted writes."));
            values = ModbusItemCodec.Encode(WriteValuesText, item, settings.Function <= 2);
        }
        else
        {
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
        }
        settings.ValidateWrite(address, values.Length);
        return (address, values);
    }

    private void NotifyWritePresentation()
    {
        foreach (var name in new[] { nameof(WriteValuesLabel), nameof(WriteHelp), nameof(WriteAvailabilityHelp), nameof(WriteItemSummary), nameof(WritePreview) })
            OnPropertyChanged(name);
    }
}
