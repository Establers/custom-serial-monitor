using RJCP.IO.Ports;

namespace SerialMonitor.WinUI.Services;

internal sealed class SerialErrorAccumulator
{
    private readonly object _gate = new();
    private long _frameCount;
    private long _parityCount;
    private long _overrunCount;
    private long _rxOverCount;
    private string _lastSummary = "(none)";

    public long FrameCount => Interlocked.Read(ref _frameCount);

    public long ParityCount => Interlocked.Read(ref _parityCount);

    public long OverrunCount => Interlocked.Read(ref _overrunCount);

    public long RxOverCount => Interlocked.Read(ref _rxOverCount);

    public string LastSummary
    {
        get
        {
            lock (_gate)
            {
                return _lastSummary;
            }
        }
    }

    public void Record(
        SerialError error,
        DateTimeOffset observedAt,
        long receivedByteCount,
        long receivedChunkCount)
    {
        if ((error & SerialError.Frame) != 0)
        {
            Interlocked.Increment(ref _frameCount);
        }

        if ((error & SerialError.RXParity) != 0)
        {
            Interlocked.Increment(ref _parityCount);
        }

        if ((error & SerialError.Overrun) != 0)
        {
            Interlocked.Increment(ref _overrunCount);
        }

        if ((error & SerialError.RXOver) != 0)
        {
            Interlocked.Increment(ref _rxOverCount);
        }

        lock (_gate)
        {
            _lastSummary =
                $"Driver-reported {error} at {observedAt:yyyy-MM-dd HH:mm:ss.fff} " +
                $"(RX {receivedByteCount:N0} bytes / {receivedChunkCount:N0} chunks)";
        }
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _frameCount, 0);
        Interlocked.Exchange(ref _parityCount, 0);
        Interlocked.Exchange(ref _overrunCount, 0);
        Interlocked.Exchange(ref _rxOverCount, 0);
        lock (_gate)
        {
            _lastSummary = "(none)";
        }
    }
}
