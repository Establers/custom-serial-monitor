using System.Diagnostics;
using System.Threading.Channels;

namespace SerialMonitor.WinUI.Services;

internal sealed class BoundedByteQueue<T>
{
    private readonly object _gate = new();
    private readonly Channel<T> _channel;
    private readonly Queue<(int ByteCount, long EnqueuedTimestamp)> _metadata = new();
    private readonly int _maxBytes;
    private int _byteCount;

    public BoundedByteQueue(int maxChunks, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChunks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _maxBytes = maxBytes;
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(maxChunks)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _metadata.Count;
            }
        }
    }

    public int ByteCount
    {
        get
        {
            lock (_gate)
            {
                return _byteCount;
            }
        }
    }

    public double OldestAgeMilliseconds
    {
        get
        {
            lock (_gate)
            {
                if (_metadata.Count == 0)
                {
                    return 0;
                }

                return Stopwatch.GetElapsedTime(_metadata.Peek().EnqueuedTimestamp).TotalMilliseconds;
            }
        }
    }

    public bool TryEnqueue(T item, int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        lock (_gate)
        {
            if (byteCount > _maxBytes - _byteCount || !_channel.Writer.TryWrite(item))
            {
                return false;
            }

            _byteCount += byteCount;
            _metadata.Enqueue((byteCount, Stopwatch.GetTimestamp()));
            return true;
        }
    }

    public bool TryDequeue(out T? item)
    {
        lock (_gate)
        {
            if (!_channel.Reader.TryRead(out item))
            {
                return false;
            }

            var metadata = _metadata.Dequeue();
            _byteCount -= metadata.ByteCount;
            return true;
        }
    }

    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
        _channel.Reader.WaitToReadAsync(cancellationToken);

    public bool TryComplete(Exception? error = null) => _channel.Writer.TryComplete(error);
}
