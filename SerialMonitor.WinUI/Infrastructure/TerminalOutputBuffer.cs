using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

// RX only offers immutable byte copies here. The UI drains a bounded batch and
// decodes it as a stream, preserving CR/BS/ANSI and split multibyte characters.
internal sealed class TerminalOutputBuffer(long serialGeneration, RxEncodingMode encoding)
{
    internal const int MaxPendingBytes = 1024 * 1024;
    internal const int MaxBatchBytes = 32 * 1024;
    private const int MaxPendingChunks = 1024;
    private readonly object _gate = new();
    private readonly Queue<byte[]> _chunks = new();
    private readonly StreamingEncodingDecoder _decoder = new(encoding);
    private int _headOffset;
    private int _pendingBytes;
    private bool _stopped;
    private string? _error;

    internal int PendingBytes { get { lock (_gate) return _pendingBytes; } }

    internal void Offer(long generation, ReceivedByteChunk chunk)
    {
        if (generation != serialGeneration || chunk.Bytes.Length == 0) return;
        lock (_gate)
        {
            if (_stopped) return;
            if (chunk.Bytes.Length > MaxPendingBytes - _pendingBytes || _chunks.Count >= MaxPendingChunks)
            {
                _error = "Direct input stopped: receive display queue is full. RX logging continues.";
                StopCore();
                return;
            }
            _chunks.Enqueue(chunk.Bytes.ToArray());
            _pendingBytes += chunk.Bytes.Length;
        }
    }

    // Called serially on the UI thread; no decode/file/UI work in Offer.
    internal string Drain(out string? error)
    {
        byte[] bytes;
        lock (_gate)
        {
            error = _error;
            if (_stopped || _pendingBytes == 0) return string.Empty;
            bytes = new byte[Math.Min(MaxBatchBytes, _pendingBytes)];
            var count = 0;
            while (count < bytes.Length)
            {
                var head = _chunks.Peek();
                var take = Math.Min(head.Length - _headOffset, bytes.Length - count);
                head.AsSpan(_headOffset, take).CopyTo(bytes.AsSpan(count));
                count += take;
                _headOffset += take;
                _pendingBytes -= take;
                if (_headOffset == head.Length) { _chunks.Dequeue(); _headOffset = 0; }
            }
        }
        return _decoder.Decode(bytes, flush: false).Text;
    }

    internal void Stop() { lock (_gate) StopCore(); }

    private void StopCore()
    {
        _stopped = true;
        _chunks.Clear();
        _headOffset = _pendingBytes = 0;
    }
}
