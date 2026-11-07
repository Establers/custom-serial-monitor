using System.Diagnostics;
using System.Threading.Channels;

namespace SerialMonitor.Core.Protocols.VirtualModbus;

public sealed record VirtualModbusRuntimeSnapshot(bool Running, bool Stopping, long Session, long Requests,
    long Responses, long Ignored, long Invalid, long Exceptions, long Broadcasts, long Echoes, long PendingBytes, string? Error);

public interface IVirtualModbusDeviceRuntime : IAsyncDisposable
{
    event EventHandler<string>? Error;
    void Start(long session, double frameGapMilliseconds, bool suppressLocalEcho = false);
    void RequestStop();
    Task StopAsync(CancellationToken cancellationToken = default);
    bool TryEnqueue(ReadOnlyMemory<byte> bytes, long session, long timestamp = 0, bool qualifiedBoundary = false);
    VirtualModbusRuntimeSnapshot Snapshot();
}

// One bounded queue and one worker per active connection. The RX observer only
// offers immutable bytes. Neither UI work nor file I/O runs on that callback.
public sealed class VirtualModbusDeviceRuntime(VirtualModbusDeviceStore store,
    Func<byte[], long, CancellationToken, Task> send) : IVirtualModbusDeviceRuntime
{
    public const int QueueByteLimit = 256 * 1024;
    private sealed record Input(byte[] Bytes, long Timestamp, bool Boundary);
    private sealed class Run(long session, double frameGapMilliseconds, bool suppressLocalEcho)
    {
        public long Session { get; } = session;
        public TimeSpan FrameGap { get; } = TimeSpan.FromMilliseconds(Math.Max(1.75, frameGapMilliseconds));
        public bool SuppressLocalEcho { get; } = suppressLocalEcho;
        public CancellationTokenSource Stop { get; } = new();
        public Channel<Input> Queue { get; } = Channel.CreateBounded<Input>(new BoundedChannelOptions(128)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        public Task Worker { get; set; } = Task.CompletedTask;
        public bool Running = true;
        public long Pending;
        public string? Failure;
    }
    private readonly object _gate = new();
    private Run? _run;
    private bool _disposed;
    private long _requests, _responses, _ignored, _invalid, _exceptions, _broadcasts, _echoes;
    public event EventHandler<string>? Error;
    public event EventHandler? StateChanged;
    public bool IsActive { get { lock (_gate) return _run is { } run && (run.Running || !run.Worker.IsCompleted); } }

    public void Start(long session, double frameGapMilliseconds, bool suppressLocalEcho = false)
    {
        if (session < 0 || !double.IsFinite(frameGapMilliseconds) || frameGapMilliseconds < 0 || frameGapMilliseconds > 1000)
            throw new ArgumentOutOfRangeException(nameof(frameGapMilliseconds));
        if (store.Devices().Count == 0) throw new InvalidOperationException("Add a device JSON before starting.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_run is { Worker.IsCompleted: false }) throw new InvalidOperationException("The previous virtual device session is still stopping.");
            _run?.Stop.Dispose();
            var run = new Run(session, frameGapMilliseconds, suppressLocalEcho);
            _run = run;
            _requests = _responses = _ignored = _invalid = _exceptions = _broadcasts = _echoes = 0;
            run.Worker = Task.Run(() => ProcessAsync(run));
        }
    }

    public bool TryEnqueue(ReadOnlyMemory<byte> bytes, long session, long timestamp = 0, bool qualifiedBoundary = false)
    {
        if (bytes.IsEmpty) return false;
        lock (_gate)
        {
            var run = _run;
            if (_disposed || run is null || !run.Running || run.Session != session) return false;
            if (bytes.Length <= QueueByteLimit - run.Pending && run.Queue.Writer.TryWrite(
                new Input(bytes.ToArray(), timestamp > 0 ? timestamp : Stopwatch.GetTimestamp(), qualifiedBoundary)))
            { run.Pending += bytes.Length; return true; }
            run.Failure = "Virtual Modbus Device stopped: receive queue overflow; no further responses will be sent.";
            StopRun(run);
            return false;
        }
    }

    private static void StopRun(Run run)
    { run.Running = false; run.Stop.Cancel(); run.Queue.Writer.TryComplete(); }

    public void RequestStop() { lock (_gate) { if (_run is { } run) StopRun(run); } }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task worker;
        lock (_gate) { if (_run is not { } run) return; StopRun(run); worker = run.Worker; }
        await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public VirtualModbusRuntimeSnapshot Snapshot()
    {
        lock (_gate) return new(_run?.Running == true, _run is { Running: false, Worker.IsCompleted: false }, _run?.Session ?? -1,
            Interlocked.Read(ref _requests), Interlocked.Read(ref _responses), Interlocked.Read(ref _ignored), Interlocked.Read(ref _invalid),
            Interlocked.Read(ref _exceptions), Interlocked.Read(ref _broadcasts), Interlocked.Read(ref _echoes), _run?.Pending ?? 0, _run?.Failure);
    }

    private async Task ProcessAsync(Run run)
    {
        var framer = new VirtualModbusRequestFramer();
        long lastTimestamp = 0;
        byte[]? expectedEcho = null;
        async Task HandleAsync(byte[]? frame)
        {
            if (frame is null) return;
            run.Stop.Token.ThrowIfCancellationRequested();
            if (run.SuppressLocalEcho && expectedEcho is not null && frame.AsSpan().SequenceEqual(expectedEcho))
            {
                expectedEcho = null; // Only one matching echo per transmit.
                Interlocked.Increment(ref _echoes);
                return;
            }
            expectedEcho = null;
            var reply = store.ProcessRequest(frame);
            if (!reply.Valid) { Interlocked.Increment(ref _invalid); return; }
            Interlocked.Increment(ref _requests);
            if (reply.Broadcast) Interlocked.Increment(ref _broadcasts);
            if (reply.Response is null) { Interlocked.Increment(ref _ignored); return; }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(run.Stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            expectedEcho = reply.Response;
            await send(reply.Response, run.Session, deadline.Token).ConfigureAwait(false);
            Interlocked.Increment(ref _responses);
            if (reply.IsException) Interlocked.Increment(ref _exceptions);
        }
        try
        {
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(run.Stop.Token);
                if (framer.HasPending) idle.CancelAfter(run.FrameGap);
                try { if (!await run.Queue.Reader.WaitToReadAsync(idle.Token).ConfigureAwait(false)) break; }
                catch (OperationCanceledException) when (!run.Stop.IsCancellationRequested)
                { await HandleAsync(framer.Flush()).ConfigureAwait(false); continue; }
                while (run.Queue.Reader.TryRead(out var input))
                {
                    try
                    {
                        if (lastTimestamp > 0 && Stopwatch.GetElapsedTime(lastTimestamp, input.Timestamp) >= run.FrameGap)
                            await HandleAsync(framer.Flush()).ConfigureAwait(false);
                        for (var i = 0; i < input.Bytes.Length; i++)
                            framer.Append(input.Bytes[i]);
                        if (input.Boundary) await HandleAsync(framer.Flush()).ConfigureAwait(false);
                        lastTimestamp = input.Timestamp;
                    }
                    finally { lock (_gate) run.Pending -= input.Bytes.Length; }
                    run.Stop.Token.ThrowIfCancellationRequested();
                }
            }
        }
        catch (OperationCanceledException) when (run.Stop.IsCancellationRequested) { }
        catch (Exception ex)
        { lock (_gate) run.Failure = $"Virtual Modbus Device stopped: {ex.Message}"; }
        finally
        {
            string? error;
            lock (_gate)
            {
                run.Running = false;
                run.Queue.Writer.TryComplete();
                while (run.Queue.Reader.TryRead(out var abandoned)) run.Pending -= abandoned.Bytes.Length;
                error = run.Failure;
            }
            if (error is not null) try { Error?.Invoke(this, error); } catch { /* A reporter cannot fault the worker. */ }
            try { StateChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        await StopAsync().ConfigureAwait(false);
        lock (_gate) { _run?.Stop.Dispose(); _run = null; }
    }
}
