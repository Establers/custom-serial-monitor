using SerialMonitor.Core.Protocols;

namespace SerialMonitor.WinUI.ViewModels;

internal enum ProtocolStatusFilter { All, ErrorsAndExceptions, CrcError, Exception, Incomplete, Undecoded, CrcValid }

internal readonly record struct ProtocolFrameFilter(byte? UnitId, byte? FunctionCode,
    ProtocolDirection? Direction, ProtocolStatusFilter Status)
{
    public int ActiveCount => (UnitId.HasValue ? 1 : 0) + (FunctionCode.HasValue ? 1 : 0) +
        (Direction.HasValue ? 1 : 0) + (Status != ProtocolStatusFilter.All ? 1 : 0);

    public bool Matches(IProtocolFrame frame)
    {
        if (UnitId.HasValue && frame.Address != UnitId) return false;
        if (Direction.HasValue && frame.Direction != Direction) return false;
        if (FunctionCode is { } requested)
        {
            // An ordinary function also includes its exception response; an
            // explicit exception code such as 83 matches only that exact code.
            if (!frame.MatchesCode(requested)) return false;
        }
        return Status switch
        {
            ProtocolStatusFilter.ErrorsAndExceptions => frame.Status is ProtocolFrameStatus.CrcError or ProtocolFrameStatus.Exception,
            ProtocolStatusFilter.CrcError => frame.Status == ProtocolFrameStatus.CrcError,
            ProtocolStatusFilter.Exception => frame.Status == ProtocolFrameStatus.Exception,
            ProtocolStatusFilter.Incomplete => frame.Status == ProtocolFrameStatus.Incomplete,
            ProtocolStatusFilter.Undecoded => frame.Status == ProtocolFrameStatus.Undecoded,
            ProtocolStatusFilter.CrcValid => frame.CrcStatus == ProtocolCrcStatus.Valid,
            _ => true
        };
    }
}
