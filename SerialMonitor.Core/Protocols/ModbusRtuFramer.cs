using System.Buffers.Binary;

namespace SerialMonitor.Core.Protocols;

// A content-based stream recognizer. USB/ReadFile boundaries are not asserted
// to be physical RTU silence. One bounded buffer survives arbitrary read splits.
public sealed class ModbusRtuFramer(ProtocolDirection direction)
{
    private ProtocolFrameRole _role;
    private bool RequestOnly => _role == ProtocolFrameRole.Request || _role == ProtocolFrameRole.Automatic && direction == ProtocolDirection.Tx;
    public void SetRole(ProtocolFrameRole role) { if (_role != role) { Reset(); _role = role; } }
    private readonly byte[] _buffer = new byte[ModbusRtuFrame.MaximumBytes];
    private readonly DateTimeOffset[] _timestamps = new DateTimeOffset[ModbusRtuFrame.MaximumBytes];
    private int _count;
    public int PendingBytes => _count;
    public void Reset() => _count = 0;

    public void Feed(ReadOnlySpan<byte> bytes, DateTimeOffset timestamp, Action<ModbusRtuFrame> emit,
        CancellationToken cancellationToken = default, bool qualifiedBoundaryAtEnd = false)
    {
        while (!bytes.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var take = Math.Min(bytes.Length, _buffer.Length - _count);
            bytes[..take].CopyTo(_buffer.AsSpan(_count));
            _timestamps.AsSpan(_count, take).Fill(timestamp);
            _count += take;
            bytes = bytes[take..];
            if (_count == _buffer.Length) Process(emit, finalize: false, force: true);
        }
        if (qualifiedBoundaryAtEnd) Process(emit, finalize: true, force: false);
    }

    public void Flush(Action<ModbusRtuFrame> emit) => Process(emit, finalize: true, force: false);

    private void Process(Action<ModbusRtuFrame> emit, bool finalize, bool force)
    {
        Span<int> candidates = stackalloc int[3];
        while (_count > 0)
        {
            var bytes = _buffer.AsSpan(0, _count);
            var candidateCount = CandidateLengths(bytes, RequestOnly, candidates);
            var continuation = 0;
            var budget = 256;
            for (var i = 0; i < candidateCount; i++)
            {
                var length = candidates[i];
                if (length < _count && IsCompleteSequence(bytes[length..], ref budget))
                { if (continuation != 0 && continuation != length) { continuation = 0; break; } continuation = length; }
            }
            // Prefer a known frame-length boundary with a complete continuation
            // over a CRC-looking byte sequence buried inside a corrupt payload.
            if (continuation > 0) { Emit(continuation, true, emit); force = false; continue; }
            if ((finalize || force) && candidates[..candidateCount].Contains(_count) && bytes[0] <= 247)
            { Emit(_count, true, emit); return; }
            var lengthFound = ValidLength(bytes, allowUnknown: finalize || force);
            if (lengthFound > 0) { Emit(lengthFound, true, emit); force = false; continue; }
            if (!finalize && !force) return;
            if (lengthFound < 0) { Emit(_count, false, emit, undecoded: true); return; }
            // Recover after a corrupt/truncated ADU without joining missing data
            // to a later valid frame. Recovery work is capped by 256 bytes.
            var recovery = 0;
            for (var offset = 1; offset <= _count - 4; offset++)
                if (ValidLength(_buffer.AsSpan(offset, _count - offset), allowUnknown: true) > 0)
                { recovery = offset; break; }
            if (recovery > 0) { Emit(recovery, false, emit); force = false; continue; }
            Emit(_count, false, emit);
        }
    }

    private int ValidLength(ReadOnlySpan<byte> bytes, bool allowUnknown)
    {
        if (bytes.Length < 4 || bytes[0] > 247 || (bytes[1] & 127) == 0) return 0;
        var variableLayout = !IsFixedLayout(bytes[1]);
        if (variableLayout && !allowUnknown) return 0;
        if (variableLayout && ModbusRtuCrc.Compute(bytes) == 0) return bytes.Length;
        Span<int> lengths = stackalloc int[3];
        var count = CandidateLengths(bytes, RequestOnly, lengths);
        if (!allowUnknown)
            for (var i = 0; i < count; i++) if (lengths[i] > bytes.Length) return 0;
        var validLength = 0;
        var validCount = 0;
        for (var i = 0; i < count; i++)
            if (lengths[i] <= bytes.Length && ModbusRtuCrc.Compute(bytes[..lengths[i]]) == 0)
            { validLength = Math.Max(validLength, lengths[i]); validCount++; }
        if (validCount == 1) return validLength;
        if (validCount > 1)
        {
            // A response's first eight bytes can themselves be a CRC-valid
            // request. Wait for a boundary or a complete continuation instead
            // of greedily inventing a short request (or always preferring RX).
            if (allowUnknown && validLength == bytes.Length) return validLength;
            var resolved = 0;
            var budget = 256;
            for (var i = 0; i < count; i++)
            {
                var length = lengths[i];
                if (length >= bytes.Length || ModbusRtuCrc.Compute(bytes[..length]) != 0) continue;
                if (IsCompleteSequence(bytes[length..], ref budget))
                {
                    if (resolved != 0) return -1;
                    resolved = length;
                }
            }
            return resolved != 0 ? resolved : -1;
        }
        if (!allowUnknown || IsFixedLayout(bytes[1])) return 0;
        ushort crc = 0xFFFF;
        for (var index = 0; index < bytes.Length; index++)
        {
            crc = ModbusRtuCrc.Append(crc, bytes[index]);
            if (index >= 3 && crc == 0) return index + 1;
        }
        return 0;
    }

    private bool IsCompleteSequence(ReadOnlySpan<byte> bytes, ref int budget)
    {
        if (--budget < 0 || bytes.Length < 4 || bytes[0] > 247 || (bytes[1] & 127) == 0) return false;
        if (!IsFixedLayout(bytes[1]) && ModbusRtuCrc.Compute(bytes) == 0) return true;
        Span<int> lengths = stackalloc int[3];
        var count = CandidateLengths(bytes, RequestOnly, lengths);
        for (var i = 0; i < count; i++)
        {
            var length = lengths[i];
            if (length > bytes.Length || ModbusRtuCrc.Compute(bytes[..length]) != 0) continue;
            if (length == bytes.Length || IsCompleteSequence(bytes[length..], ref budget)) return true;
        }
        return false;
    }

    private void Emit(int length, bool valid, Action<ModbusRtuFrame> emit, bool undecoded = false)
    {
        var bytes = _buffer.AsSpan(0, length).ToArray();
        Span<int> candidates = stackalloc int[3];
        var count = CandidateLengths(bytes, RequestOnly, candidates);
        var knownLength = candidates[..count].Contains(length);
        var crcMatches = length >= 4 && ModbusRtuCrc.Compute(bytes) == 0;
        var status = crcMatches ? ModbusCrcStatus.Valid : knownLength && length >= 4 ? ModbusCrcStatus.Invalid : ModbusCrcStatus.Unavailable;
        var kind = undecoded ? ModbusFrameKind.Undecoded : valid && crcMatches ? Classify(bytes, RequestOnly) : crcMatches ? ModbusFrameKind.Undecoded : ModbusFrameKind.Incomplete;
        if (_role == ProtocolFrameRole.Response)
            kind = kind == ModbusFrameKind.RequestOrResponse ? ModbusFrameKind.Response : kind == ModbusFrameKind.Request ? ModbusFrameKind.Undecoded : kind;
        // A CRC-invalid complete candidate may be inspected, but is never used
        // as a valid register response or as evidence of a successful request.
        var result = new ModbusRtuFrame(0, 0, _timestamps[0], direction, bytes, kind, status);
        Array.Copy(_buffer, length, _buffer, 0, _count - length);
        Array.Copy(_timestamps, length, _timestamps, 0, _count - length);
        _count -= length;
        emit(result);
    }

    private static bool IsFixedLayout(byte function) => (function & 128) != 0 || function is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 11 or 12 or 15 or 16 or 17 or 22 or 23 or 24;

    private static int CandidateLengths(ReadOnlySpan<byte> b, bool tx, Span<int> lengths)
    {
        if (b.Length < 2) return 0;
        var count = 0;
        if ((b[1] & 128) != 0) { lengths[0] = 5; return 1; }
        var request = b[1] switch { 1 or 2 or 3 or 4 or 5 or 6 or 8 or 11 => 8, 7 or 12 or 17 => 4, 22 => 10, 24 => 6, 43 => 7, _ => 0 };
        // Event counter requests have no subfunction/data.
        if (b[1] == 11) request = 4;
        if (b[1] is 15 or 16 && b.Length >= 7) request = 9 + b[6];
        if (b[1] == 23 && b.Length >= 11) request = 13 + b[10];
        if (request is >= 4 and <= 256) lengths[count++] = request;
        if (tx) return count;
        var response = b[1] switch { 5 or 6 or 8 or 11 or 15 or 16 => 8, 7 => 5, 22 => 10, _ => 0 };
        if (b.Length >= 3 && b[1] is 1 or 2 or 3 or 4 or 12 or 17 or 23)
        {
            var byteCount = b[2];
            if (byteCount > 0 && (b[1] is not (3 or 4 or 23) || byteCount % 2 == 0)) response = 5 + byteCount;
        }
        if (b[1] == 24 && b.Length >= 4) response = 6 + BinaryPrimitives.ReadUInt16BigEndian(b[2..]);
        if (response is >= 4 and <= 256 && response != request) lengths[count++] = response;
        return count;
    }

    private static ModbusFrameKind Classify(ReadOnlySpan<byte> b, bool tx)
    {
        if ((b[1] & 128) != 0) return ModbusFrameKind.Exception;
        Span<int> request = stackalloc int[3];
        var requestCount = CandidateLengths(b, true, request);
        var isRequest = request[..requestCount].Contains(b.Length);
        if (tx) return isRequest ? ModbusFrameKind.Request : ModbusFrameKind.Undecoded;
        Span<int> all = stackalloc int[3];
        var allCount = CandidateLengths(b, false, all);
        var isResponse = b.Length >= 5 && b[1] is 1 or 2 or 3 or 4 or 12 or 17 or 23 &&
            b.Length == 5 + b[2] && (b[1] is not (3 or 4 or 23) || b[2] % 2 == 0);
        for (var i = 0; i < allCount; i++) if (all[i] == b.Length && (!isRequest || i > 0)) isResponse = true;
        if (b[1] is 5 or 6 or 8 or 22 && isRequest) return ModbusFrameKind.RequestOrResponse;
        if (isRequest && isResponse) return ModbusFrameKind.RequestOrResponse;
        if (isRequest) return ModbusFrameKind.Request;
        return isResponse ? ModbusFrameKind.Response : ModbusFrameKind.Undecoded;
    }
}
