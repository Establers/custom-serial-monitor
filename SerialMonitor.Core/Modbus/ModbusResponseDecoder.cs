using System.Buffers.Binary;
using SerialMonitor.Core.Protocols;

namespace SerialMonitor.Core.Modbus;

public enum ModbusResponseStatus { Success, Exception, CrcError, InvalidResponse }

public sealed record ModbusResponseResult(ModbusResponseStatus Status, ushort[] Values, byte? ExceptionCode = null);

/// <summary>
/// Validates one complete response against the operation that produced it. Stream
/// boundaries and transaction ownership are the transport's responsibility.
/// </summary>
public static class ModbusResponseDecoder
{
    public static ModbusResponseResult DecodeRead(ModbusReadRequest request, ReadOnlySpan<byte> frame)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var preliminary = ValidateEnvelope(request.UnitId, request.Function, frame);
        if (preliminary is not null) return preliminary;

        var byteCount = request.Function <= 2 ? (request.Quantity + 7) / 8 : request.Quantity * 2;
        if (frame.Length != byteCount + 5 || frame[2] != byteCount)
            return Failure(ModbusResponseStatus.InvalidResponse);
        if (request.Function <= 2 && !HasZeroPadding(frame.Slice(3, byteCount), request.Quantity))
            return Failure(ModbusResponseStatus.InvalidResponse);
        // Some eight-byte coil responses can also be valid read requests. An
        // exact request echo is indistinguishable from a device response here;
        // fail closed instead of turning adapter echo into observed values.
        if (request.Function <= 2 && frame.Length == 8 && frame.SequenceEqual(request.BuildPacket()))
            return Failure(ModbusResponseStatus.InvalidResponse);

        var values = new ushort[request.Quantity];
        for (var i = 0; i < values.Length; i++)
            values[i] = request.Function <= 2
                ? (ushort)((frame[3 + i / 8] >> (i % 8)) & 1)
                : BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(3 + i * 2, 2));
        return new(ModbusResponseStatus.Success, values);
    }

    public static ModbusResponseResult DecodeWrite(ReadOnlySpan<byte> request, ReadOnlySpan<byte> frame)
    {
        // Broadcasts deliberately have no acknowledgement and cannot be decoded
        // as a successful unicast transaction.
        if (!IsValidWriteRequest(request)) return Failure(ModbusResponseStatus.InvalidResponse);
        var preliminary = ValidateEnvelope(request[0], request[1], frame);
        if (preliminary is not null) return preliminary;
        if (frame.Length != 8 || !frame[..6].SequenceEqual(request[..6]))
            return Failure(ModbusResponseStatus.InvalidResponse);
        return new(ModbusResponseStatus.Success, []);
    }

    private static ModbusResponseResult? ValidateEnvelope(int unitId, byte function, ReadOnlySpan<byte> frame)
    {
        if (frame.Length is < 5 or > 256 || frame[0] != unitId ||
            (frame[1] != function && frame[1] != (function | 0x80)))
            return Failure(ModbusResponseStatus.InvalidResponse);
        if (ModbusRtuCrc.Compute(frame) != 0) return Failure(ModbusResponseStatus.CrcError);
        if (frame[1] == (function | 0x80))
            return frame.Length == 5 && frame[2] != 0
                ? new(ModbusResponseStatus.Exception, [], frame[2])
                : Failure(ModbusResponseStatus.InvalidResponse);
        return null;
    }

    private static bool IsValidWriteRequest(ReadOnlySpan<byte> request)
    {
        if (request.Length is < 8 or > 256 || request[0] is < 1 or > 247 ||
            request[1] is not (5 or 6 or 15 or 16) || ModbusRtuCrc.Compute(request) != 0)
            return false;
        if (request[1] is 5 or 6)
            return request.Length == 8 && (request[1] == 6 ||
                BinaryPrimitives.ReadUInt16BigEndian(request.Slice(4, 2)) is 0 or 0xFF00);

        var quantity = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(4, 2));
        var address = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(2, 2));
        var byteCount = request[1] == 15 ? (quantity + 7) / 8 : quantity * 2;
        return quantity >= 1 && quantity <= (request[1] == 15 ? 1968 : 123) &&
            address + quantity <= 65536 && request.Length == byteCount + 9 && request[6] == byteCount &&
            (request[1] != 15 || HasZeroPadding(request.Slice(7, byteCount), quantity));
    }

    private static bool HasZeroPadding(ReadOnlySpan<byte> data, int quantity) =>
        quantity % 8 == 0 || (data[^1] & (0xFF << (quantity % 8))) == 0;

    private static ModbusResponseResult Failure(ModbusResponseStatus status) => new(status, []);
}
