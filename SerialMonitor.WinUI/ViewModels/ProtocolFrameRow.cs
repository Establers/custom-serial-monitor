using System.Globalization;
using Microsoft.UI.Xaml;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.ViewModels;

public sealed class ProtocolFrameRow(ModbusRtuFrame frame)
{
    public ModbusRtuFrame Frame { get; } = frame;
    public long Id => Frame.Id;
    public string TimeText => Frame.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    public string DirectionText => Frame.Direction == ProtocolDirection.Rx ? "RX" : "TX";
    public string UnitText => Frame.UnitId?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string FunctionCodeText => Frame.FunctionCode?.ToString("X2", CultureInfo.InvariantCulture) ?? "—";
    public string FunctionText => Frame.Status == ModbusFrameStatus.Exception && Frame.Length == 5
        ? $"{ModbusRtuFrame.FunctionName(Frame.FunctionCode)} · {ModbusRtuFrame.ExceptionName(Frame.Bytes.Span[2])}"
        : ModbusRtuFrame.FunctionName(Frame.FunctionCode);
    public string StatusText => Frame.Status switch
    {
        ModbusFrameStatus.CrcError => UiText.Get("ProtocolStateCrcError", "CRC error"),
        ModbusFrameStatus.Incomplete => UiText.Get("ProtocolStateIncomplete", "Incomplete"),
        ModbusFrameStatus.Undecoded => UiText.Get("ProtocolStateUndecoded", "Undecoded"),
        ModbusFrameStatus.Exception when Frame.Length == 5 => UiText.Format("ProtocolStateExceptionFormat", "Exception {0:X2}", Frame.Bytes.Span[2]),
        ModbusFrameStatus.Exception => UiText.Get("ProtocolStateException", "Exception"),
        _ => "CRC OK"
    };
    public bool IsError => Frame.Status is ModbusFrameStatus.CrcError or ModbusFrameStatus.Exception;
    public Visibility ErrorVisibility => IsError ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NeutralVisibility => IsError ? Visibility.Collapsed : Visibility.Visible;
    public string Heading => $"{TimeText}  {DirectionText}  ID {UnitText}  FC {FunctionCodeText}  ·  {StatusText} · {Frame.Length} B";
    public string Summary => $"{FunctionText} · {StatusText} · CRC: {Frame.CrcStatus}";
}
