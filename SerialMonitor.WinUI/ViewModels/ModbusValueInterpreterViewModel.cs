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
    public string Help => _canInterpret
        ? UiText.Get("ModbusValuesHelp", "+0 is the first register in this response, not a device address. Displayed value = decoded value × multiplier.")
        : UnavailableHelp();
    public Visibility Visibility => _frame is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EditorVisibility => _canInterpret ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ErrorVisibility => string.IsNullOrEmpty(ValidationError) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RemainderVisibility => string.IsNullOrEmpty(RemainderText) ? Visibility.Collapsed : Visibility.Visible;
    public bool IsWordSwapEnabled => FormatIndex >= 2;
    public string ValuesText { get => _valuesText; private set => SetProperty(ref _valuesText, value); }
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
        _canInterpret = frame is not null && ModbusRegisterInterpreter.CanInterpret(frame);
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
            switch (frame.Kind)
            {
                case ModbusFrameKind.Request:
                    return UiText.Get("ModbusValuesRequest", "Request packet; select the corresponding register read response to interpret values.");
                case ModbusFrameKind.Exception:
                    return UiText.Get("ModbusValuesException", "Exception response; no normal register values are present.");
                case ModbusFrameKind.RequestOrResponse:
                    return UiText.Get("ModbusValuesAmbiguous", "The request/response role is ambiguous; values are not interpreted.");
                case ModbusFrameKind.Response when frame.FunctionCode is 3 or 4 or 23:
                    return UiText.Get("ModbusValuesInvalidResponse", "The register read response has an invalid address, length or data layout.");
            }
        }
        return UiText.Get("ModbusValuesUnsupported", "Register value interpretation supports FC03, FC04 and FC17 read responses.");
    }

    private void Rebuild()
    {
        ValuesText = RemainderText = ValidationError = string.Empty;
        if (!_canInterpret || _frame is null) return;
        var registerCount = _frame.Bytes.Span[2] / 2;
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
        var result = ModbusRegisterInterpreter.Interpret(_frame, type, offset, SwapBytes, SwapWords && IsWordSwapEnabled, scale);
        ValuesText = string.Join(Environment.NewLine, result.Values.Select(value =>
        {
            var position = value.RegisterCount == 1 ? $"+{value.RegisterOffset}" : $"+{value.RegisterOffset}–+{value.RegisterOffset + 1}";
            var display = value.ScalingOverflow ? UiText.Get("ModbusValuesOverflow", "Scaling overflow")
                : value.Value.ToString(type == ModbusValueType.Float32 ? "G7" : "G15", CultureInfo.CurrentCulture);
            var raw = scale == 1 ? string.Empty : UiText.Format("ModbusValuesRawFormat", " (raw {0})",
                type == ModbusValueType.Float32 ? ((float)value.RawValue).ToString("G", CultureInfo.CurrentCulture)
                : value.RawValue.ToString("G", CultureInfo.CurrentCulture));
            return $"{position}: {display}{raw}";
        }));
        if (result.TrailingRegisters > 0)
            RemainderText = UiText.Format("ModbusValuesRemainder", "Register +{0} has no partner for a 32-bit value; it was not interpreted.", registerCount - 1);
    }
}
