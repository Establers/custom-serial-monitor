using System.Buffers.Binary;
using System.Globalization;
using Microsoft.UI.Xaml;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class ModbusPacketBuilderViewModel : ViewModelBase
{
    private static readonly byte[] Functions = [1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 15, 16, 17, 20, 21, 22, 23, 24, 43, 0];
    private int _functionIndex = 2;
    private string _unitText = "1", _addressText = "0", _quantityText = "1", _valuesText = "0";
    private bool _coilOn;
    private byte[] _packet = [];
    private byte _packetFunction;
    private string _validationError = "", _preview = "", _summary = "";

    public ModbusPacketBuilderViewModel() => Refresh();

    public IReadOnlyList<string> FunctionOptions { get; } =
    [
        "01 · Read Coils (multiple)", "02 · Read Discrete Inputs (multiple)",
        "03 · Read Holding Registers (multiple)", "04 · Read Input Registers (multiple)",
        "05 · Write Single Coil", "06 · Write Single Register", "07 · Read Exception Status", "08 · Diagnostics",
        "0B · Get Comm Event Counter", "0C · Get Comm Event Log", "0F · Write Multiple Coils", "10 · Write Multiple Registers",
        "11 · Report Server ID", "14 · Read File Record", "15 · Write File Record", "16 · Mask Write Register",
        "17 · Read/Write Multiple Registers", "18 · Read FIFO Queue", "2B/0E · Read Device Identification",
        UiText.Get("ModbusBuilderCustom", "Custom · Function + raw data")
    ];
    public string Title => UiText.Get("ModbusBuilderTitle", "Modbus RTU packet builder");
    public string CopyLabel => UiText.Get("ModbusBuilderCopy", "Copy HEX");
    public string CloseLabel => UiText.Get("ModbusBuilderClose", "Close");
    public string UnitLabel => UiText.Get("ModbusBuilderUnit", "Unit ID (0–247)");
    public string AddressLabel => Function == 23 ? UiText.Get("ModbusBuilderReadAddress", "Read start address (base 0)")
        : Function == 24 ? UiText.Get("ModbusBuilderFifoAddress", "FIFO pointer address (base 0)")
        : UiText.Get("ModbusBuilderAddress", "Start address (base 0)");
    public string FunctionLabel => UiText.Get("ProtocolFunction", "Function");
    public string QuantityLabel => Function == 23 ? UiText.Get("ModbusBuilderReadQuantity", "Read quantity") : UiText.Get("ModbusBuilderQuantity", "Quantity");
    public string ValuesLabel => UiText.Get("ModbusBuilderValues", "Values, in address order");
    public string CoilLabel => UiText.Get("ModbusBuilderCoil", "Coil value");
    public string PreviewLabel => UiText.Get("ModbusBuilderPreview", "RTU packet · HEX (CRC included)");
    public string NumberHelp => UiText.Get("ModbusBuilderNumberHelp", "Numbers: decimal or 0x HEX. Address 0 is the first item (e.g. holding register 40001).");
    public string UnitHelp => AllowsBroadcast
        ? UiText.Get("ModbusBuilderUnitHelp", "ID 0 broadcasts writes to all units; no response is returned. Reads require ID 1–247.")
        : UiText.Get("ModbusBuilderUnicastHelp", "This request requires Unit ID 1–247. Device support depends on the model.");
    public string ValuesHelp => Function == 15
        ? UiText.Get("ModbusBuilderCoilsHelp", "Enter 1–1968 coil values (0 or 1), separated by spaces or commas. Quantity and bit packing are automatic.")
        : Function == 6 ? UiText.Get("ModbusBuilderSingleHelp", "Enter one register value: 0–65535 or 0x0000–0xFFFF.")
        : UiText.Format("ModbusBuilderRegisterValuesHelp", "Enter 1–{0} register values, separated by spaces or commas. Write quantity and byte count are automatic.", MaximumQuantity);
    public byte Function => Functions[FunctionIndex];
    public int MaximumQuantity => Function switch { 1 or 2 => 2000, 3 or 4 => 125, 15 => 1968, 16 => 123, 23 => 121, _ => 1 };
    public string QuantityHelp => UiText.Format("ModbusBuilderQuantityHelp", "Allowed quantity: 1–{0}.", Function == 23 ? 125 : MaximumQuantity);
    public bool AllowsBroadcast => Function is 5 or 6 or 15 or 16 or 21 or 22;
    public Visibility AddressVisibility => Function is >= 1 and <= 6 or 15 or 16 or 22 or 23 or 24 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility QuantityVisibility => Function is >= 1 and <= 4 or 23 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CoilVisibility => Function == 5 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ValuesVisibility => Function is 15 or 16 or 23 ? Visibility.Visible : Visibility.Collapsed;
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
            foreach (var name in new[] { nameof(Function), nameof(MaximumQuantity), nameof(AddressVisibility), nameof(QuantityVisibility), nameof(CoilVisibility),
                nameof(ValuesVisibility), nameof(ValuesHelp), nameof(QuantityHelp), nameof(AddressLabel), nameof(QuantityLabel), nameof(UnitHelp),
                nameof(FunctionHelp), nameof(DiagnosticsVisibility), nameof(MaskVisibility), nameof(ReadWriteVisibility), nameof(FileReadVisibility),
                nameof(FileWriteVisibility), nameof(DeviceIdVisibility), nameof(CustomVisibility), nameof(ReadRangeVisibility),
                nameof(SingleRegisterVisibility), nameof(WriteRangeVisibility), nameof(FifoVisibility) }) OnPropertyChanged(name);
            Refresh();
        }
    }
    public string UnitText { get => _unitText; set { if (SetProperty(ref _unitText, value)) Refresh(); } }
    public string AddressText { get => _addressText; set { if (SetProperty(ref _addressText, value)) Refresh(); } }
    public string QuantityText { get => _quantityText; set { if (SetProperty(ref _quantityText, value)) Refresh(); } }
    public string ValuesText { get => _valuesText; set { if (SetProperty(ref _valuesText, value)) Refresh(); } }
    public bool CoilOn { get => _coilOn; set { if (SetProperty(ref _coilOn, value)) Refresh(); } }

    public void SelectFunction(byte function)
    {
        var index = Array.IndexOf(Functions, function);
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(function));
        FunctionIndex = index;
    }

    public void Refresh()
    {
        _packet = [];
        _preview = _summary = _validationError = "";
        try { Build(); _packetFunction = Function; }
        catch (ArgumentException ex) { _validationError = ex.Message; }
        foreach (var name in new[] { nameof(Packet), nameof(CanUsePacket), nameof(Preview), nameof(Summary), nameof(ValidationError), nameof(ErrorVisibility), nameof(WriteQuantityText) })
            OnPropertyChanged(name);
    }

    private void Build()
    {
        var unit = Number(UnitText, UnitLabel, 247);
        if (unit == 0 && !AllowsBroadcast) throw new ArgumentException(UnitHelp);
        var address = AddressVisibility == Visibility.Visible ? Number(AddressText, AddressLabel, 65535) : 0;
        _packet = Function switch
        {
            >= 1 and <= 4 => ModbusRtuRequestBuilder.Read(unit, Function, address, Number(QuantityText, QuantityLabel, MaximumQuantity, 1)),
            5 => ModbusRtuRequestBuilder.WriteSingle(unit, Function, address, (ushort)(CoilOn ? 1 : 0)),
            6 => ModbusRtuRequestBuilder.WriteSingle(unit, Function, address, Words(ValuesText, 1)[0]),
            15 or 16 => ModbusRtuRequestBuilder.WriteMultiple(unit, Function, address, Words(ValuesText, MaximumQuantity, Function == 15 ? 1 : 65535)),
            7 or 11 or 12 or 17 => ModbusRtuRequestBuilder.WithoutData(unit, Function),
            8 => ModbusRtuRequestBuilder.Diagnostics(unit, DiagnosticSubFunction, Words(DiagnosticDataText, DiagnosticSubFunction == 0 ? 125 : 1)),
            20 => ModbusRtuRequestBuilder.ReadFileRecords(unit, ReadFileRows()),
            21 => ModbusRtuRequestBuilder.WriteFileRecords(unit, WriteFileRows()),
            22 => ModbusRtuRequestBuilder.MaskWrite(unit, address, (ushort)Number(AndMaskText, "AND mask", 65535), (ushort)Number(OrMaskText, "OR mask", 65535)),
            23 => ModbusRtuRequestBuilder.ReadWriteMultiple(unit, address, Number(QuantityText, QuantityLabel, 125, 1),
                Number(WriteAddressText, WriteAddressLabel, 65535), Words(ValuesText, 121)),
            24 => ModbusRtuRequestBuilder.ReadFifo(unit, address),
            43 => ModbusRtuRequestBuilder.ReadDeviceIdentification(unit, DeviceAccessIndex + 1, Number(ObjectIdText, ObjectIdLabel, 255)),
            0 => ModbusRtuRequestBuilder.Custom(unit, Number(CustomFunctionText, FunctionLabel, 127, 1), RawData()),
            _ => throw new ArgumentOutOfRangeException(nameof(Function))
        };
        _preview = string.Join(" ", _packet.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        _summary = UiText.Format("ModbusBuilderPacketSummary", "{0} bytes · CRC (low first) {1:X2} {2:X2}", _packet.Length, _packet[^2], _packet[^1]);
        if (Function is >= 1 and <= 4 or 15 or 16)
            _summary = UiText.Format("ModbusBuilderSummary", "{0} bytes · quantity {1} · CRC (low first) {2:X2} {3:X2}",
                _packet.Length, BinaryPrimitives.ReadUInt16BigEndian(_packet.AsSpan(4)), _packet[^2], _packet[^1]);
        else if (Function == 23)
            _summary = UiText.Format("ModbusBuilderReadWriteSummary", "{0} bytes · read {1} / write {2} · CRC {3:X2} {4:X2}",
                _packet.Length, BinaryPrimitives.ReadUInt16BigEndian(_packet.AsSpan(4)), BinaryPrimitives.ReadUInt16BigEndian(_packet.AsSpan(8)), _packet[^2], _packet[^1]);
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
