using System.Buffers.Binary;
using System.Text;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.ViewModels;

// Formats only the selected packet. Wire decoding and the retained binary
// history stay independent of the summary and expandable diagnostics.
internal static class ProtocolPacketPresentation
{
    public static string Summary(ProtocolFrameRow row)
    {
        var frame = row.Frame;
        var text = new StringBuilder();
        var role = frame is ModbusRtuFrame modbus ? Role(modbus) : "BACnet MS/TP";
        text.AppendLine($"{frame.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} · {row.DirectionText} · {role}");
        if (frame is BacnetMstpFrame mstp)
        {
            text.AppendLine($"{row.FunctionText} ({row.FunctionCodeText})");
            text.AppendLine(UiText.Format("ProtocolPacketMacFormat", "Source {0} → destination {1}",
                row.UnitText, mstp.Destination?.ToString() ?? "—"));
        }
        else
            text.AppendLine(UiText.Format("ProtocolPacketDeviceFormat", "Device {0} · {1} ({2})",
                row.UnitText, row.FunctionText, row.FunctionCodeText));
        var state = frame.Status != ProtocolFrameStatus.Decoded && frame.CrcStatus == ProtocolCrcStatus.Valid
            ? $"{row.StatusText} · CRC OK" : row.StatusText;
        if (role != state) text.AppendLine(state);

        var warning = frame.Status switch
        {
            ProtocolFrameStatus.CrcError => UiText.Get("ProtocolPacketCrcWarning", "CRC mismatch; values cannot be interpreted."),
            ProtocolFrameStatus.Incomplete => UiText.Get("ProtocolPacketIncompleteWarning", "Incomplete data; fields and values could not be established."),
            ProtocolFrameStatus.Undecoded => UiText.Get("ProtocolPacketUndecodedWarning", "Frame not decoded; no value interpretation is applied."),
            _ => string.Empty
        };
        if (warning.Length > 0) text.AppendLine(warning);
        else if (frame is ModbusRtuFrame packet && packet.Status == ModbusFrameStatus.Decoded)
            AppendModbusFields(text, packet);
        else if (frame is BacnetMstpFrame { Status: ProtocolFrameStatus.Decoded, FrameType: 5 or 6 })
            text.AppendLine(UiText.Get("ProtocolPacketMstpScope", "MS/TP frame only; application data is not decoded."));
        return text.ToString().TrimEnd();
    }

    public static string Diagnostics(IProtocolFrame frame)
    {
        if (frame is not ModbusRtuFrame modbus) return frame.Describe();
        var bytes = modbus.Bytes.Span;
        var text = new StringBuilder();
        text.AppendLine($"Session: {frame.Session}; bytes: {frame.Length}");
        if (bytes.Length >= 4 && modbus.CrcStatus != ModbusCrcStatus.Unavailable)
            text.AppendLine($"CRC: {modbus.CrcStatus}; received: {BinaryPrimitives.ReadUInt16LittleEndian(bytes[^2..]):X4}; calculated: {ModbusRtuCrc.Compute(bytes[..^2]):X4}");
        else text.AppendLine("CRC: unavailable (incomplete frame)");
        text.AppendLine();
        text.AppendLine("HEX:");
        text.Append(string.Join(" ", frame.Bytes.ToArray().Select(value => value.ToString("X2"))));
        return text.ToString();
    }

    private static string Role(ModbusRtuFrame frame) => frame.Status switch
    {
        ModbusFrameStatus.CrcError => UiText.Get("ProtocolStateCrcError", "CRC error"),
        ModbusFrameStatus.Incomplete => UiText.Get("ProtocolStateIncomplete", "Incomplete"),
        ModbusFrameStatus.Undecoded => UiText.Get("ProtocolStateUndecoded", "Undecoded"),
        _ => DecodedRole(frame.Kind)
    };

    private static string DecodedRole(ModbusFrameKind kind) => kind switch
    {
        ModbusFrameKind.Request => UiText.Get("ProtocolPacketRequest", "Request"),
        ModbusFrameKind.Response => UiText.Get("ProtocolPacketResponse", "Response"),
        ModbusFrameKind.RequestOrResponse => UiText.Get("ProtocolPacketRequestOrResponse", "Request / response (same layout)"),
        ModbusFrameKind.Exception => UiText.Get("ProtocolPacketExceptionResponse", "Exception response"),
        ModbusFrameKind.Incomplete => UiText.Get("ProtocolStateIncomplete", "Incomplete"),
        _ => UiText.Get("ProtocolStateUndecoded", "Undecoded")
    };

    private static void AppendModbusFields(StringBuilder text, ModbusRtuFrame frame)
    {
        var bytes = frame.Bytes.Span;
        if (ModbusRegisterInterpreter.CanInterpret(frame))
            text.AppendLine(UiText.Format("ProtocolPacketRegistersFormat", "Registers: {0}", bytes[2] / 2));
        else if (bytes.Length >= 8 && frame.FunctionCode is 1 or 2 or 3 or 4 or 15 or 16 &&
            (frame.Kind == ModbusFrameKind.Request || (frame.Kind == ModbusFrameKind.Response && frame.FunctionCode is 15 or 16)))
            AppendRange(text, bytes[2..], "ProtocolPacketRangeFormat", "Start address (base 0): {0} · quantity: {1}");
        else if (bytes.Length >= 13 && frame.Kind == ModbusFrameKind.Request && frame.FunctionCode == 23)
        {
            AppendRange(text, bytes[2..], "ProtocolPacketReadRangeFormat", "Read address (base 0): {0} · quantity: {1}");
            AppendRange(text, bytes[6..], "ProtocolPacketWriteRangeFormat", "Write address (base 0): {0} · quantity: {1}");
        }
        else if (bytes.Length == 8 && frame.FunctionCode is 5 or 6)
        {
            text.AppendLine(UiText.Format("ProtocolPacketAddressFormat", "Address (base 0): {0}",
                BinaryPrimitives.ReadUInt16BigEndian(bytes[2..])));
            // FC06's register value is shown once, by the configurable interpreter.
            if (frame.FunctionCode == 5)
                text.AppendLine(UiText.Format("ProtocolPacketCoilFormat", "Coil: {0}", bytes[4] == 0xFF ? "ON" : "OFF"));
        }
    }

    private static void AppendRange(StringBuilder text, ReadOnlySpan<byte> bytes, string key, string fallback) =>
        text.AppendLine(UiText.Format(key, fallback,
            BinaryPrimitives.ReadUInt16BigEndian(bytes), BinaryPrimitives.ReadUInt16BigEndian(bytes[2..])));
}
