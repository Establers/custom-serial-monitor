using System.Threading.Channels;

namespace SerialMonitor.WinUI.Infrastructure;

internal sealed class BoundedIncidentWriter : IAsyncDisposable
{
    private readonly object _enqueueGate = new();
    private readonly int _capacity;
    private readonly Action<string> _sink;
    private readonly Channel<string> _channel;
    private readonly Task _pumpTask;
    private int _pendingWorkCount;
    private long _droppedIncidentCount;
    private long _droppedSinceLastAccepted;
    private long _sinkErrorCount;
    private bool _completed;

    public BoundedIncidentWriter(int capacity, Action<string> sink)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentNullException.ThrowIfNull(sink);

        _capacity = capacity;
        _sink = sink;
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _pumpTask = Task.Run(PumpAsync, CancellationToken.None);
    }

    public int Capacity => _capacity;

    public int PendingWorkCount => Volatile.Read(ref _pendingWorkCount);

    public long DroppedIncidentCount => Interlocked.Read(ref _droppedIncidentCount);

    public long SinkErrorCount => Interlocked.Read(ref _sinkErrorCount);

    internal bool IsCompleted
    {
        get
        {
            lock (_enqueueGate)
            {
                return _completed;
            }
        }
    }

    internal Task Completion => _pumpTask;

    public bool TryEnqueue(string message)
    {
        lock (_enqueueGate)
        {
            if (_completed)
            {
                _droppedIncidentCount++;
                return false;
            }

            if (Volatile.Read(ref _pendingWorkCount) >= _capacity)
            {
                _droppedIncidentCount++;
                _droppedSinceLastAccepted++;
                return false;
            }

            var entry = _droppedSinceLastAccepted == 0
                ? message
                : $"[Dropped {_droppedSinceLastAccepted:N0} newer incident(s) while the diagnostic queue was full.] {message}";
            Interlocked.Increment(ref _pendingWorkCount);
            if (!_channel.Writer.TryWrite(entry))
            {
                Interlocked.Decrement(ref _pendingWorkCount);
                _droppedIncidentCount++;
                _droppedSinceLastAccepted++;
                return false;
            }

            _droppedSinceLastAccepted = 0;
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Complete();
        await _pumpTask.ConfigureAwait(false);
    }

    public async Task<bool> CompleteAndDrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        Complete();
        try
        {
            await _pumpTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private void Complete()
    {
        lock (_enqueueGate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _channel.Writer.TryComplete();
        }
    }

    private async Task PumpAsync()
    {
        await foreach (var entry in _channel.Reader.ReadAllAsync())
        {
            try
            {
                _sink(entry);
            }
            catch
            {
                Interlocked.Increment(ref _sinkErrorCount);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingWorkCount);
            }
        }
    }
}
