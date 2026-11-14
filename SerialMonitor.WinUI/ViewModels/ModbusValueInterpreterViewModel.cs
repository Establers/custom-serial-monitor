using System.Buffers.Binary;
using System.Globalization;
using Microsoft.UI.Xaml;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.ViewModels;

public sealed class ModbusValueInterpreterViewModel : ViewModelBase
{
    private ModbusRtuFrame? _frame;
    private bool _canInterpret, _swapBytes, _swapWords;
    private int _formatIndex;
    private string _offsetText = "0", _scaleText = "1";
    private string _valuesText = string.Empty, _validationError = string.Empty, _remainderText = string.Empty;

    public IReadOnlyList<string> Formats { get; } = ["UInt16", "Int16", "UInt32", "Int32", "Float32"];
    public string Title => UiText.Get("ModbusValuesTitle", "Value interpretation");
    public string FormatLabel => UiText.Get("ModbusValuesFormat", "Data type");
    public string OffsetLabel => UiText.Get("ModbusValuesOffset", "Offset (+)");
    public string ScaleLabel => UiText.Get("ModbusValuesScale", "Multiplier");
    public string SwapBytesLabel => UiText.Get("ModbusValuesSwapBytes", "Swap bytes");
    public string SwapWordsLabel => UiText.Get("ModbusValuesSwapWords", "Swap words");
    public string SwapBytesHelp => UiText.Get("ModbusValuesSwapBytesHelp", "Swap bytes within each register: AB CD → BA DC.");
    public string SwapWordsHelp => UiText.Get("ModbusValuesSwapWordsHelp", "Swap the two registers of each 32-bit value: AB CD → CD AB.");
    public string Help
    {
        get
        {
            if (!_canInterpret) return UnavailableHelp();
            if (_frame is { FunctionCode: 6 } frame)
            {
                var address = BinaryPrimitives.ReadUInt16BigEndian(frame.Bytes.Span.Slice(2, 2));
                return UiText.Format("ModbusValuesSingleWriteHelp",
                    "FC06 contains one requested or echoed register value at address {0} (0x{1}). The packet role remains as shown; this is not a separate readback.",
                    address, address.ToString("X4", CultureInfo.InvariantCulture));
            }
            return UiText.Get("ModbusValuesHelp", "+0 is the first register in this response, not a device address. Displayed value = decoded value × multiplier.");
        }
    }
    public Visibility Visibility => _frame is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EditorVisibility => _canInterpret ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ErrorVisibility => string.IsNullOrEmpty(ValidationError) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ValuesVisibility => string.IsNullOrEmpty(ValuesText) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RemainderVisibility => string.IsNullOrEmpty(RemainderText) ? Visibility.Collapsed : Visibility.Visible;
    public bool IsWordSwapEnabled => FormatIndex >= 2;
    public string ValuesText
    {
        get => _valuesText;
        private set { if (SetProperty(ref _valuesText, value)) OnPropertyChanged(nameof(ValuesVisibility)); }
    }
    public string RemainderText
    {
        get => _remainderText;
        private set { if (SetProperty(ref _remainderText, value)) OnPropertyChanged(nameof(RemainderVisibility)); }
    }
    public string ValidationError
    {
        get => _validationError;
        private set { if (SetProperty(ref _validationError, value)) OnPropertyChanged(nameof(ErrorVisibility)); }
    }

    public int FormatIndex
    {
        get => _formatIndex;
        set
        {
            if (value < 0 || value >= Formats.Count || !SetProperty(ref _formatIndex, value)) return;
            OnPropertyChanged(nameof(IsWordSwapEnabled));
            Rebuild();
        }
    }
    public bool SwapBytes { get => _swapBytes; set { if (SetProperty(ref _swapBytes, value)) Rebuild(); } }
    public bool SwapWords { get => _swapWords; set { if (SetProperty(ref _swapWords, value)) Rebuild(); } }
    public string OffsetText { get => _offsetText; set { if (SetProperty(ref _offsetText, value)) Rebuild(); } }
    public string ScaleText { get => _scaleText; set { if (SetProperty(ref _scaleText, value)) Rebuild(); } }

    public void SetFrame(ModbusRtuFrame? frame)
    {
        if (ReferenceEquals(_frame, frame)) return;
        _frame = frame;
        _canInterpret = frame is not null &&
            (ModbusRegisterInterpreter.CanInterpret(frame) || CanInterpretSingleRegisterWrite(frame));
        foreach (var property in new[] { nameof(Visibility), nameof(EditorVisibility), nameof(Help) }) OnPropertyChanged(property);
        Rebuild();
    }

    private string UnavailableHelp()
    {
        if (_frame is { } frame)
        {
            if (frame.CrcStatus == ModbusCrcStatus.Invalid)
                return UiText.Get("ModbusValuesCrcError", "CRC error; values cannot be interpreted.");
            if (frame.CrcStatus == ModbusCrcStatus.Unavailable || frame.Kind == ModbusFrameKind.Incomplete)
                return UiText.Get("ModbusValuesIncomplete", "Incomplete frame; register data cannot be established.");
            if (frame.Length is >= 4 and <= ModbusRtuFrame.MaximumBytes && ModbusRtuCrc.Compute(frame.Bytes.Span) != 0)
                return UiText.Get("ModbusValuesCrcError", "CRC error; values cannot be interpreted.");
            if (frame.FunctionCode == 6)
                return UiText.Get("ModbusValuesInvalidSingleWrite", "FC06 requires a complete 8-byte single-register write request or echo with a valid device address.");
            switch (frame.Kind)
            {
                case ModbusFrameKind.Request or ModbusFrameKind.Response or ModbusFrameKind.RequestOrResponse when frame.FunctionCode == 5:
                    return UiText.Get("ModbusValuesCoilWrite", "Coil write request/echo; register interpretation is unavailable.");
                case ModbusFrameKind.Request:
                    return UiText.Get("ModbusValuesRequest", "Request packet; select the corresponding register read response to interpret values.");
                case ModbusFrameKind.Exception:
                    return UiText.Get("ModbusValuesException", "Exception response; no normal register values are present.");
                case ModbusFrameKind.RequestOrResponse:
                    return UnsupportedHelp();
                case ModbusFrameKind.Response when frame.FunctionCode is 3 or 4 or 23:
                    return UiText.Get("ModbusValuesInvalidResponse", "The register read response has an invalid address, length or data layout.");
            }
        }
        return UnsupportedHelp();
    }

    private static string UnsupportedHelp() => UiText.Get("ModbusValuesUnsupported", "Register value interpretation is unavailable for this function.");

    // FC06 echoes its address and one value, so the payload is known even when
    // capture alone cannot distinguish a request from its response. Do not turn
    // this projection into a read response or infer an acknowledgement, including
    // for Unit 0 broadcasts, which never have a normal Modbus response.
    private static bool CanInterpretSingleRegisterWrite(ModbusRtuFrame frame)
    {
        var bytes = frame.Bytes.Span;
        return frame.CrcStatus == ModbusCrcStatus.Valid && bytes.Length == 8 &&
            bytes[0] <= 247 && bytes[1] == 6 &&
            frame.Kind is ModbusFrameKind.Request or ModbusFrameKind.Response or ModbusFrameKind.RequestOrResponse &&
            ModbusRtuCrc.Compute(bytes) == 0;
    }

    private static ModbusValueInterpretation InterpretSingleRegisterWrite(ModbusRtuFrame frame,
        ModbusValueType type, bool swapBytes, double scale)
    {
        if (type is not (ModbusValueType.UInt16 or ModbusValueType.Int16)) return new([], 1);
        var bytes = frame.Bytes.Span.Slice(4, 2);
        var word = swapBytes ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
        double raw = type == ModbusValueType.Int16 ? unchecked((short)word) : word;
        return new([new(0, 1, raw, raw * scale)], 0);
    }

    private void Rebuild()
    {
        ValuesText = RemainderText = ValidationError = string.Empty;
        if (!_canInterpret || _frame is null) return;
        var isSingleWrite = _frame.FunctionCode == 6;
        var registerCount = isSingleWrite ? 1 : _frame.Bytes.Span[2] / 2;
        if (!int.TryParse(OffsetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) || offset < 0 || offset >= registerCount)
        {
            ValidationError = UiText.Format("ModbusValuesOffsetError", "Enter a register offset from 0 to {0}.", registerCount - 1);
            return;
        }
        if ((!double.TryParse(ScaleText, NumberStyles.Float, CultureInfo.CurrentCulture, out var scale) &&
             !double.TryParse(ScaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out scale)) || !double.IsFinite(scale))
        {
            ValidationError = UiText.Get("ModbusValuesScaleError", "Enter a finite multiplier, e.g. 1, 0.1 or -1.");
            return;
        }
        var type = (ModbusValueType)FormatIndex;
        var result = isSingleWrite
            ? InterpretSingleRegisterWrite(_frame, type, SwapBytes, scale)
            : ModbusRegisterInterpreter.Interpret(_frame, type, offset, SwapBytes, SwapWords && IsWordSwapEnabled, scale);
        ValuesText = string.Join(Environment.NewLine, result.Values.Select(value =>
        {
            var position = value.RegisterCount == 1 ? $"+{value.RegisterOffset}" : $"+{value.RegisterOffset}–+{value.RegisterOffset + 1}";
            // Preserve the original Single when unscaled, and the full Double
            // result after scaling. G7/G15 can make distinct values look equal.
            var rawText = type == ModbusValueType.Float32
                ? ((float)value.RawValue).ToString("G9", CultureInfo.CurrentCulture)
                : value.RawValue.ToString("G17", CultureInfo.CurrentCulture);
            var display = value.ScalingOverflow ? UiText.Get("ModbusValuesOverflow", "Scaling overflow")
                : scale == 1 ? rawText : value.Value.ToString("G17", CultureInfo.CurrentCulture);
            var raw = scale == 1 ? string.Empty : UiText.Format("ModbusValuesRawFormat", " (raw {0})", rawText);
            return $"{position}: {display}{raw}";
        }));
        if (result.TrailingRegisters > 0)
            RemainderText = UiText.Format("ModbusValuesRemainder", "Register +{0} has no partner for a 32-bit value; it was not interpreted.", registerCount - 1);
    }
}
