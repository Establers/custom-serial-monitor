using System.Globalization;
using Microsoft.UI.Xaml;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.ViewModels;

public sealed class ProtocolFrameRow(IProtocolFrame frame)
{
    public IProtocolFrame Frame { get; } = frame;
    public long Id => Frame.Id;
    public string TimeText => Frame.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    public string DirectionText => Frame.Direction == ProtocolDirection.Rx ? "RX" : "TX";
    public string UnitText => Frame.Address?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string FunctionCodeText => Frame.Code?.ToString("X2", CultureInfo.InvariantCulture) ?? "—";
    public string FunctionText => Frame.CodeName;
    public string StatusText => Frame.Status switch
    {
        ProtocolFrameStatus.CrcError => UiText.Get("ProtocolStateCrcError", "CRC error"),
        ProtocolFrameStatus.Incomplete => UiText.Get("ProtocolStateIncomplete", "Incomplete"),
        ProtocolFrameStatus.Undecoded => UiText.Get("ProtocolStateUndecoded", "Undecoded"),
        ProtocolFrameStatus.Exception when Frame is ModbusRtuFrame && Frame.Length == 5 => UiText.Format("ProtocolStateExceptionFormat", "Exception {0:X2}", Frame.Bytes.Span[2]),
        ProtocolFrameStatus.Exception => UiText.Get("ProtocolStateException", "Exception"),
        _ => "CRC OK"
    };
    public bool IsError => Frame.Status is ProtocolFrameStatus.CrcError or ProtocolFrameStatus.Exception;
    public Visibility ErrorVisibility => IsError ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NeutralVisibility => IsError ? Visibility.Collapsed : Visibility.Visible;
    public string Heading => $"{TimeText}  {DirectionText}  {(Frame is BacnetMstpFrame ? "Src" : "ID")} {UnitText}  {(Frame is BacnetMstpFrame ? "Type" : "FC")} {FunctionCodeText}  ·  {StatusText} · {Frame.Length} B";
    public string Summary => $"{FunctionText} · {StatusText} · CRC: {Frame.CrcStatus}";
}
