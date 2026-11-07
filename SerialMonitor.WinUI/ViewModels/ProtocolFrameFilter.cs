using SerialMonitor.Core.Protocols;

namespace SerialMonitor.WinUI.ViewModels;

internal enum ProtocolStatusFilter { All, ErrorsAndExceptions, CrcError, Exception, Incomplete, Undecoded, CrcValid }

internal readonly record struct ProtocolFrameFilter(byte? UnitId, byte? FunctionCode,
    ProtocolDirection? Direction, ProtocolStatusFilter Status)
{
    public int ActiveCount => (UnitId.HasValue ? 1 : 0) + (FunctionCode.HasValue ? 1 : 0) +
        (Direction.HasValue ? 1 : 0) + (Status != ProtocolStatusFilter.All ? 1 : 0);

    public bool Matches(ModbusRtuFrame frame)
    {
        if (UnitId.HasValue && frame.UnitId != UnitId) return false;
        if (Direction.HasValue && frame.Direction != Direction) return false;
        if (FunctionCode is { } requested)
        {
            if (frame.FunctionCode is not { } actual) return false;
            // An ordinary function also includes its exception response; an
            // explicit exception code such as 83 matches only that exact code.
            if (requested < 0x80 ? (actual & 0x7F) != requested : actual != requested) return false;
        }
        return Status switch
        {
            ProtocolStatusFilter.ErrorsAndExceptions => frame.Status is ModbusFrameStatus.CrcError or ModbusFrameStatus.Exception,
            ProtocolStatusFilter.CrcError => frame.Status == ModbusFrameStatus.CrcError,
            ProtocolStatusFilter.Exception => frame.Status == ModbusFrameStatus.Exception,
            ProtocolStatusFilter.Incomplete => frame.Status == ModbusFrameStatus.Incomplete,
            ProtocolStatusFilter.Undecoded => frame.Status == ModbusFrameStatus.Undecoded,
            ProtocolStatusFilter.CrcValid => frame.CrcStatus == ModbusCrcStatus.Valid,
            _ => true
        };
    }
}
