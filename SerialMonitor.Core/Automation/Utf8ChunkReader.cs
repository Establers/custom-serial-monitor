using System.Text;

namespace SerialMonitor.Core.Automation;

// Return the characters decoded from available native bytes, without filling a
// larger character request from another native read. StreamReader can otherwise
// wait for more bytes after a complete Unicode JSON line at a 4096-byte boundary.
internal sealed class Utf8ChunkReader(Stream stream, bool leaveOpen = true) : TextReader
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly Decoder _decoder = Utf8.GetDecoder();
    private readonly byte[] _bytes = new byte[4096];
    private readonly char[] _characters = new char[Utf8.GetMaxCharCount(4096)];
    private int _position, _count, _reading;
    private bool _eof, _disposed;

    public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (Interlocked.Exchange(ref _reading, 1) != 0) throw new InvalidOperationException("Only one pipe read may be active.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length == 0) return 0;
            while (_position == _count)
            {
                if (_eof) return 0;
                var read = await stream.ReadAsync(_bytes, cancellationToken).ConfigureAwait(false);
                _eof = read == 0;
                _count = _decoder.GetChars(_bytes, 0, read, _characters, 0, flush: _eof);
                _position = 0;
                // Only a split code point with no decoded characters requires
                // another native read. A complete line never requires peer EOF.
            }
            var count = Math.Min(buffer.Length, _count - _position);
            _characters.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        finally { Volatile.Write(ref _reading, 0); }
    }

    protected override void Dispose(bool disposing)
    {
        Volatile.Write(ref _disposed, true);
        if (disposing && !leaveOpen) stream.Dispose();
        base.Dispose(disposing);
    }
}
