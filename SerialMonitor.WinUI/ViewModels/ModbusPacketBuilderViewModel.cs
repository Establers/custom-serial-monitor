using System.Globalization;
using Microsoft.UI.Xaml;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.ViewModels;

public sealed class ModbusPacketBuilderViewModel : ViewModelBase
{
    private static readonly byte[] Functions = [1, 2, 3, 4, 5, 6, 15, 16];
    private int _functionIndex = 2;
    private string _unitText = "1", _addressText = "0", _quantityText = "1", _valuesText = "0";
    private bool _coilOn;
    private byte[] _packet = [];
    private string _validationError = "", _preview = "", _summary = "";

    public ModbusPacketBuilderViewModel() => Refresh();

    public IReadOnlyList<string> FunctionOptions { get; } =
    [
        "01 · Read Coils", "02 · Read Discrete Inputs", "03 · Read Holding Registers", "04 · Read Input Registers",
        "05 · Write Single Coil", "06 · Write Single Register", "0F · Write Multiple Coils", "10 · Write Multiple Registers"
    ];
    public string Title => UiText.Get("ModbusBuilderTitle", "Modbus RTU packet builder");
    public string CopyLabel => UiText.Get("ModbusBuilderCopy", "Copy HEX");
    public string CloseLabel => UiText.Get("ModbusBuilderClose", "Close");
    public string UnitLabel => UiText.Get("ModbusBuilderUnit", "Unit ID (0–247)");
    public string AddressLabel => UiText.Get("ModbusBuilderAddress", "Start address (base 0)");
    public string FunctionLabel => UiText.Get("ProtocolFunction", "Function");
    public string QuantityLabel => UiText.Get("ModbusBuilderQuantity", "Quantity");
    public string ValuesLabel => UiText.Get("ModbusBuilderValues", "Values, in address order");
    public string CoilLabel => UiText.Get("ModbusBuilderCoil", "Coil value");
    public string PreviewLabel => UiText.Get("ModbusBuilderPreview", "RTU packet · HEX (CRC included)");
    public string NumberHelp => UiText.Get("ModbusBuilderNumberHelp", "Numbers: decimal or 0x HEX. Address 0 is the first item (e.g. holding register 40001).");
    public string UnitHelp => UiText.Get("ModbusBuilderUnitHelp", "ID 0 broadcasts writes to all units; no response is returned. Reads require ID 1–247.");
    public string ValuesHelp => Function == 15
        ? UiText.Get("ModbusBuilderCoilsHelp", "Enter 1–1968 coil values (0 or 1), separated by spaces or commas. Quantity and bit packing are automatic.")
        : Function == 6 ? UiText.Get("ModbusBuilderSingleHelp", "Enter one register value: 0–65535 or 0x0000–0xFFFF.")
        : UiText.Get("ModbusBuilderRegistersHelp", "Enter 1–123 register values, separated by spaces or commas. Quantity and byte count are automatic.");
    public string QuantityHelp => UiText.Format("ModbusBuilderQuantityHelp", "Allowed quantity: 1–{0}.", MaximumQuantity);
    public byte Function => Functions[FunctionIndex];
    public int MaximumQuantity => Function switch { 1 or 2 => 2000, 3 or 4 => 125, 15 => 1968, 16 => 123, _ => 1 };
    public Visibility QuantityVisibility => Function <= 4 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CoilVisibility => Function == 5 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ValuesVisibility => Function is 6 or 15 or 16 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ErrorVisibility => ValidationError.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public ReadOnlyMemory<byte> Packet => _packet;
    public bool CanUsePacket => _packet.Length > 0;
    public string ValidationError => _validationError;
    public string Preview => _preview;
    public string Summary => _summary;

    public int FunctionIndex
    {
        get => _functionIndex;
        set
        {
            if (value < 0 || value >= Functions.Length || !SetProperty(ref _functionIndex, value)) return;
            foreach (var name in new[] { nameof(Function), nameof(MaximumQuantity), nameof(QuantityVisibility), nameof(CoilVisibility),
                nameof(ValuesVisibility), nameof(ValuesHelp), nameof(QuantityHelp) }) OnPropertyChanged(name);
            Refresh();
        }
    }
    public string UnitText { get => _unitText; set { if (SetProperty(ref _unitText, value)) Refresh(); } }
    public string AddressText { get => _addressText; set { if (SetProperty(ref _addressText, value)) Refresh(); } }
    public string QuantityText { get => _quantityText; set { if (SetProperty(ref _quantityText, value)) Refresh(); } }
    public string ValuesText { get => _valuesText; set { if (SetProperty(ref _valuesText, value)) Refresh(); } }
    public bool CoilOn { get => _coilOn; set { if (SetProperty(ref _coilOn, value)) Refresh(); } }

    public void Refresh()
    {
        _packet = [];
        _preview = _summary = _validationError = "";
        try { Build(); }
        catch (ArgumentException ex) { _validationError = ex.Message; }
        foreach (var name in new[] { nameof(Packet), nameof(CanUsePacket), nameof(Preview), nameof(Summary), nameof(ValidationError), nameof(ErrorVisibility) })
            OnPropertyChanged(name);
    }

    private void Build()
    {
        var unit = Number(UnitText, UnitLabel, 247);
        if (unit == 0 && Function <= 4)
            throw new ArgumentException(UiText.Get("ModbusBuilderReadBroadcastError", "Reads require a Unit ID from 1 to 247."));
        var address = Number(AddressText, AddressLabel, 65535);
        var quantity = 1;
        ushort[] values = [];
        if (Function <= 4)
        {
            quantity = Number(QuantityText, QuantityLabel, MaximumQuantity, 1);
        }
        else if (Function != 5)
        {
            // Bound parsing as well as the final RTU packet, even outside the dialog's TextBox limits.
            if (ValuesText.Length > 12000) throw new ArgumentException(ValuesHelp);
            var tokens = ValuesText.Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 1 || tokens.Length > MaximumQuantity) throw new ArgumentException(ValuesHelp);
            values = tokens.Select(token => (ushort)Number(token, ValuesLabel, Function == 15 ? 1 : 65535)).ToArray();
            quantity = values.Length;
        }
        if (address + quantity > 65536)
            throw new ArgumentException(UiText.Get("ModbusBuilderAddressRangeError", "The last address must not exceed 65535."));
        _packet = Function switch
        {
            <= 4 => ModbusRtuRequestBuilder.Read(unit, Function, address, quantity),
            5 => ModbusRtuRequestBuilder.WriteSingle(unit, Function, address, (ushort)(CoilOn ? 1 : 0)),
            6 => ModbusRtuRequestBuilder.WriteSingle(unit, Function, address, values[0]),
            _ => ModbusRtuRequestBuilder.WriteMultiple(unit, Function, address, values)
        };
        _preview = string.Join(" ", _packet.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        _summary = UiText.Format("ModbusBuilderSummary", "{0} bytes · quantity {1} · CRC (low first) {2:X2} {3:X2}",
            _packet.Length, quantity, _packet[^2], _packet[^1]);
    }

    private static int Number(string text, string label, int max, int min = 0)
    {
        var span = text.AsSpan().Trim();
        var hex = span.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (hex) span = span[2..];
        if (!int.TryParse(span, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value < min || value > max)
            throw new ArgumentException(UiText.Format("ModbusBuilderNumberError", "{0}: enter {1}–{2} (decimal or 0x HEX).", label, min, max));
        return value;
    }
}
