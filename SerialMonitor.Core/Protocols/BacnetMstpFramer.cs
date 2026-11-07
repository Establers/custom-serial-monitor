using System.Buffers.Binary;

namespace SerialMonitor.Core.Protocols;

// Length-based, bounded stream parser. ReadFile chunk boundaries never terminate
// a valid-header payload. A corrupt header waits for idle/bounded recovery.
public sealed class BacnetMstpFramer(ProtocolDirection direction)
{
    private readonly byte[] _buffer = new byte[BacnetMstpFrame.MaximumBytes];
    private readonly DateTimeOffset[] _times = new DateTimeOffset[BacnetMstpFrame.MaximumBytes];
    private int _count;
    private bool _allowPad;
    public int PendingBytes => _count;
    public void Reset() { _count = 0; _allowPad = false; }
    public void Feed(ReadOnlySpan<byte> bytes, DateTimeOffset time, Action<BacnetMstpFrame> emit,
        CancellationToken cancellationToken = default, bool qualifiedBoundaryAtEnd = false)
    {
        while (!bytes.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int take = Math.Min(bytes.Length, _buffer.Length - _count);
            bytes[..take].CopyTo(_buffer.AsSpan(_count)); _times.AsSpan(_count,take).Fill(time);
            _count += take; bytes = bytes[take..]; Process(emit,finalize:false);
            // Parsing a full buffer can leave a valid partial tail. Only force
            // recovery when parsing made no room (e.g. an untrusted header).
            if (_count == _buffer.Length) Process(emit,finalize:true);
        }
        // Only a bad header needs this hint; intact headers use their declared length.
        if (qualifiedBoundaryAtEnd && _count >= 8 && HeaderStatus() == ProtocolCrcStatus.Invalid) Process(emit,true);
    }
    public void Flush(Action<BacnetMstpFrame> emit) => Process(emit,true);
    private ProtocolCrcStatus HeaderStatus() => BacnetMstpCrc.Header(_buffer.AsSpan(2,5)) == _buffer[7]
        ? ProtocolCrcStatus.Valid : ProtocolCrcStatus.Invalid;
    private bool HeaderAt(int offset) => _count - offset >= 8 && _buffer[offset] == 0x55 && _buffer[offset+1] == 0xFF &&
        BacnetMstpCrc.Header(_buffer.AsSpan(offset+2,5)) == _buffer[offset+7];
    private void Process(Action<BacnetMstpFrame> emit, bool finalize)
    {
        while (_count > 0)
        {
            if (_allowPad)
            {
                _allowPad = false;
                if (_buffer[0] == 0xFF) { Consume(1); continue; }
            }
            if (_count < 2 || _buffer[0] != 0x55 || _buffer[1] != 0xFF)
            {
                int prefix = 0;
                while (prefix < _count && !(_buffer[prefix] == 0x55 &&
                    (prefix + 1 == _count || _buffer[prefix+1] == 0xFF))) prefix++;
                if (prefix == 0) { if (!finalize) return; prefix = _count; }
                if (prefix == _count && !finalize) { Emit(prefix,ProtocolFrameStatus.Undecoded,ProtocolCrcStatus.Unavailable,ProtocolCrcStatus.Unavailable,emit); continue; }
                Emit(prefix,ProtocolFrameStatus.Incomplete,ProtocolCrcStatus.Unavailable,ProtocolCrcStatus.Unavailable,emit); continue;
            }
            if (_count < 8)
            {
                if (finalize) Emit(_count,ProtocolFrameStatus.Incomplete,ProtocolCrcStatus.Unavailable,ProtocolCrcStatus.Unavailable,emit);
                return;
            }
            int size = BinaryPrimitives.ReadUInt16BigEndian(_buffer.AsSpan(5));
            bool extended = _buffer[2] is >= 32 and <= 127;
            bool plausible = extended ? size is >= 5 and <= 1509 : size <= BacnetMstpFrame.MaximumClassicDataBytes;
            int length = extended ? size + 10 : 8 + size + (size > 0 ? 2 : 0);
            if (HeaderStatus() == ProtocolCrcStatus.Invalid)
            {
                // A plausible length is only a recovery hint, never trusted data.
                // Do not search inside a payload for an invented valid packet.
                if (plausible && _count > length && HeaderAt(length))
                { Emit(length,ProtocolFrameStatus.CrcError,ProtocolCrcStatus.Invalid,ProtocolCrcStatus.Unavailable,emit); continue; }
                if (plausible && _count > length && _buffer[length] == 0xFF && HeaderAt(length + 1))
                {
                    Emit(length,ProtocolFrameStatus.CrcError,ProtocolCrcStatus.Invalid,ProtocolCrcStatus.Unavailable,emit);
                    Consume(1); // One optional pad, justified by the following CRC-valid header.
                    continue;
                }
                if (finalize) Emit(_count,ProtocolFrameStatus.CrcError,ProtocolCrcStatus.Invalid,ProtocolCrcStatus.Unavailable,emit);
                return;
            }
            if (!plausible)
            { Emit(8,ProtocolFrameStatus.Undecoded,ProtocolCrcStatus.Valid,ProtocolCrcStatus.Unavailable,emit); continue; }
            if (_count < length)
            {
                if (finalize) Emit(_count,ProtocolFrameStatus.Incomplete,ProtocolCrcStatus.Valid,ProtocolCrcStatus.Unavailable,emit);
                return;
            }
            if (extended)
            { Emit(length,ProtocolFrameStatus.Undecoded,ProtocolCrcStatus.Valid,ProtocolCrcStatus.Unavailable,emit); continue; }
            bool valid = size == 0 || BacnetMstpCrc.Data(_buffer.AsSpan(8,size)) == BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(8+size));
            bool knownLayout = _buffer[4] != 255 && (_buffer[2] switch
            {
                0 or 1 or 2 or 7 => size == 0,
                3 or 4 => true,
                5 or 6 => size >= 2,
                _ => false
            });
            var status = !valid ? ProtocolFrameStatus.CrcError : knownLayout ? ProtocolFrameStatus.Decoded : ProtocolFrameStatus.Undecoded;
            Emit(length,status,ProtocolCrcStatus.Valid,size == 0 ? ProtocolCrcStatus.Unavailable : valid ? ProtocolCrcStatus.Valid : ProtocolCrcStatus.Invalid,emit);
        }
    }
    private void Emit(int length, ProtocolFrameStatus status, ProtocolCrcStatus header, ProtocolCrcStatus data, Action<BacnetMstpFrame> emit)
    {
        emit(new(0,0,_times[0],direction,_buffer.AsSpan(0,length).ToArray(),status,header,data));
        _allowPad = header == ProtocolCrcStatus.Valid && status != ProtocolFrameStatus.Incomplete;
        Consume(length);
    }
    private void Consume(int length)
    {
        _count -= length; _buffer.AsSpan(length,_count).CopyTo(_buffer); _times.AsSpan(length,_count).CopyTo(_times);
    }
}
