using Microsoft.UI.Xaml;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class ModbusPacketBuilderViewModel
{
    private static readonly int[] DiagnosticFunctions = [0, 1, 2, 3, 4, 10, 11, 12, 13, 14, 15, 16, 17, 18, 20];
    private int _diagnosticIndex, _deviceAccessIndex;
    private string _diagnosticDataText = "0", _andMaskText = "0xFFFF", _orMaskText = "0", _writeAddressText = "0";
    private string _fileReadText = "1, 0, 1", _fileWriteText = "1, 0: 0", _objectIdText = "0";
    private string _customFunctionText = "0x41", _customDataText = "";

    public Visibility DiagnosticsVisibility => Show(Function == 8);
    public Visibility MaskVisibility => Show(Function == 22);
    public Visibility ReadWriteVisibility => Show(Function == 23);
    public Visibility FileReadVisibility => Show(Function == 20);
    public Visibility FileWriteVisibility => Show(Function == 21);
    public Visibility DeviceIdVisibility => Show(Function == 43);
    public Visibility CustomVisibility => Show(Function == 0);
    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    public string FunctionHelp => Function switch
    {
        >= 1 and <= 4 => UiText.Get("ModbusBuilderReadHelp", "Set quantity above 1 to read multiple consecutive coils, inputs or registers in one request."),
        23 => UiText.Get("ModbusBuilderReadWriteHelp", "Writes the supplied registers first, then reads a separate register range in the same transaction."),
        20 or 21 => UiText.Get("ModbusBuilderFileHelp", "One line per group. Multiple file/record groups can share a request; request and response sizes are checked."),
        8 => UiText.Get("ModbusBuilderDiagnosticHelp", "Diagnostic support is device-specific. Restart, clear counters and listen-only options change the device state when sent."),
        22 => UiText.Get("ModbusBuilderMaskHelp", "Register result = (current AND mask) OR (OR mask AND NOT AND mask)."),
        24 => UiText.Get("ModbusBuilderFifoHelp", "Reads the queue at the pointer address. The device supplies the queue length; no quantity is sent."),
        43 => UiText.Get("ModbusBuilderDeviceHelp", "Start stream queries at object 0. For further pages, use the Next Object ID returned by the device."),
        7 or 11 or 12 or 17 => UiText.Get("ModbusBuilderNoDataHelp", "This request needs only the Unit ID and function code; there is no address or data field."),
        0 => UiText.Get("ModbusBuilderCustomHelp", "Enter the function separately and data as HEX bytes without Unit ID, function or CRC. CRC is automatic; custom data semantics are not validated. Unit ID: 1–247."),
        _ => ""
    };
    public string WriteAddressLabel => UiText.Get("ModbusBuilderWriteAddress", "Write start address (base 0)");
    public string DiagnosticLabel => UiText.Get("ModbusBuilderDiagnostic", "Diagnostic subfunction (HEX)");
    public string DiagnosticDataLabel => UiText.Get("ModbusBuilderDiagnosticData", "Diagnostic data (16-bit words)");
    public string DiagnosticDataHelp => DiagnosticSubFunction switch
    {
        0 => UiText.Get("ModbusBuilderDiagnosticEchoHelp", "Enter 1–125 words to loop back, separated by spaces or commas."),
        1 => UiText.Get("ModbusBuilderDiagnosticRestartHelp", "0x0000 keeps the event log; 0xFF00 clears it during restart."),
        3 => UiText.Get("ModbusBuilderDiagnosticDelimiterHelp", "Enter CHAR then 00 as one word, e.g. 0x0A00 for LF."),
        _ => UiText.Get("ModbusBuilderDiagnosticZeroHelp", "This subfunction requires a single 0x0000 data word.")
    };
    public string FileRecordsLabel => UiText.Get("ModbusBuilderFileRecords", "File record groups");
    public string FileReadHelp => UiText.Get("ModbusBuilderFileReadHelp", "Each line: file, record, quantity. Example: 1, 0, 2. File: 1–65535; record: 0–9999." );
    public string FileWriteHelp => UiText.Get("ModbusBuilderFileWriteHelp", "Each line: file, record: values. Example: 1, 0: 10 0x1234. Values are 16-bit registers, not raw bytes.");
    public string DeviceAccessLabel => UiText.Get("ModbusBuilderDeviceAccess", "Device identification access");
    public string ObjectIdLabel => UiText.Get("ModbusBuilderObjectId", "Object ID (0–255)");
    public string CustomFunctionLabel => UiText.Get("ModbusBuilderCustomFunction", "Function (1–127 or 0x01–0x7F)");
    public string CustomDataLabel => UiText.Get("ModbusBuilderCustomData", "Raw data · HEX (0–252 bytes)");

    public IReadOnlyList<string> DiagnosticOptions { get; } =
    [
        "0000 · Return Query Data", "0001 · Restart Communications", "0002 · Return Diagnostic Register",
        "0003 · Change ASCII Input Delimiter", "0004 · Force Listen Only Mode", "000A · Clear Counters and Diagnostic Register",
        "000B · Return Bus Message Count", "000C · Return Bus Communication Error Count", "000D · Return Bus Exception Error Count",
        "000E · Return Server Message Count", "000F · Return Server No Response Count", "0010 · Return Server NAK Count",
        "0011 · Return Server Busy Count", "0012 · Return Bus Character Overrun Count", "0014 · Clear Overrun Counter and Flag"
    ];
    public IReadOnlyList<string> DeviceAccessOptions { get; } = ["01 · Basic", "02 · Regular", "03 · Extended", "04 · Individual object"];
    public int DiagnosticSubFunction => DiagnosticFunctions[DiagnosticIndex];
    public int DiagnosticIndex
    {
        get => _diagnosticIndex;
        set
        {
            if (value < 0 || value >= DiagnosticFunctions.Length || !SetProperty(ref _diagnosticIndex, value)) return;
            OnPropertyChanged(nameof(DiagnosticDataHelp)); OnPropertyChanged(nameof(DiagnosticSubFunction)); Refresh();
        }
    }
    public int DeviceAccessIndex
    {
        get => _deviceAccessIndex;
        set { if (value is >= 0 and <= 3 && SetProperty(ref _deviceAccessIndex, value)) Refresh(); }
    }
    public string DiagnosticDataText { get => _diagnosticDataText; set { if (SetProperty(ref _diagnosticDataText, value)) Refresh(); } }
    public string AndMaskText { get => _andMaskText; set { if (SetProperty(ref _andMaskText, value)) Refresh(); } }
    public string OrMaskText { get => _orMaskText; set { if (SetProperty(ref _orMaskText, value)) Refresh(); } }
    public string WriteAddressText { get => _writeAddressText; set { if (SetProperty(ref _writeAddressText, value)) Refresh(); } }
    public string FileReadText { get => _fileReadText; set { if (SetProperty(ref _fileReadText, value)) Refresh(); } }
    public string FileWriteText { get => _fileWriteText; set { if (SetProperty(ref _fileWriteText, value)) Refresh(); } }
    public string ObjectIdText { get => _objectIdText; set { if (SetProperty(ref _objectIdText, value)) Refresh(); } }
    public string CustomFunctionText { get => _customFunctionText; set { if (SetProperty(ref _customFunctionText, value)) Refresh(); } }
    public string CustomDataText { get => _customDataText; set { if (SetProperty(ref _customDataText, value)) Refresh(); } }

    private ushort[] Words(string text, int maximumCount, int maximumValue = 65535)
    {
        if (text.Length > 12000) throw new ArgumentException(UiText.Get("ModbusBuilderInputTooLong", "Input is too long; no data has been truncated."));
        var tokens = Tokens(text);
        if (tokens.Length < 1 || tokens.Length > maximumCount)
            throw new ArgumentException(UiText.Format("ModbusBuilderWordCountError", "Enter 1–{0} values.", maximumCount));
        return tokens.Select(token => (ushort)Number(token, ValuesLabel, maximumValue)).ToArray();
    }

    private static string[] Tokens(string text) => text.Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string[] Rows(string text, int maximum)
    {
        if (text.Length > 12000) throw new ArgumentException(UiText.Get("ModbusBuilderInputTooLong", "Input is too long; no data has been truncated."));
        var rows = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rows.Length < 1 || rows.Length > maximum)
            throw new ArgumentException(UiText.Format("ModbusBuilderRowCountError", "Enter 1–{0} record groups, one per line.", maximum));
        return rows;
    }

    private ModbusFileReadRecord[] ReadFileRows() => Rows(FileReadText, 35).Select(row =>
    {
        var parts = Tokens(row);
        if (parts.Length != 3) throw new ArgumentException(FileReadHelp);
        return new ModbusFileReadRecord(Number(parts[0], "File", 65535, 1), Number(parts[1], "Record", 9999), Number(parts[2], QuantityLabel, 124, 1));
    }).ToArray();

    private ModbusFileWriteRecord[] WriteFileRows() => Rows(FileWriteText, 27).Select(row =>
    {
        var pair = row.Split(':');
        if (pair.Length != 2) throw new ArgumentException(FileWriteHelp);
        var parts = Tokens(pair[0]);
        if (parts.Length != 2) throw new ArgumentException(FileWriteHelp);
        return new ModbusFileWriteRecord(Number(parts[0], "File", 65535, 1), Number(parts[1], "Record", 9999), Words(pair[1], 122));
    }).ToArray();

    private byte[] RawData()
    {
        if (CustomDataText.Length > 4096) throw new ArgumentException(CustomDataLabel);
        if (string.IsNullOrWhiteSpace(CustomDataText)) return [];
        if (!HexPayloadParser.TryParse(CustomDataText, out var data, out _) || data.Length > 252)
            throw new ArgumentException(UiText.Get("ModbusBuilderRawDataError", "Enter complete HEX bytes (e.g. 00 10 FF), at most 252 bytes."));
        return data;
    }
}
