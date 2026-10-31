using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace SerialMonitor.Core.Automation;

public sealed record CaptureConnection(bool Connected, long Generation, long SerialErrorCount);
public sealed record CaptureOptions(int DurationMs = 2000, string? Command = null, string TxMode = "text", string LineEnding = "crlf",
    string? PreviewFormat = null);
public sealed record CaptureResult(
    string CaptureId, string CaptureStatus, string SendStatus, string? Command, string Port,
    long SessionGeneration, DateTimeOffset StartedAt, DateTimeOffset EndedAt,
    int RequestedDurationMs, long ReceivedBytes, long StoredBytes, long DroppedBytes,
    long SerialErrors, bool RxGapDetected, string? Error, string RawFile, string TextFile,
    string TxMode = "text", string LineEnding = "crlf", string? PreviewFormat = null);
public sealed record CapturePage(string CaptureId, string Format, long Offset, long NextOffset,
    long TotalBytes, bool HasMore, string Text);

// A capture has its own bounded non-blocking RX subscription. It never consumes
// the parser's channel and never performs disk I/O in the receive callback.
public sealed class RxCaptureService(string directory) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly object _storageGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private CaptureSession? _active;
    private CancellationTokenSource? _operationStop;
    private bool _disposed;
    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    public void Offer(long generation, byte[] bytes, long receivedTimestamp)
    {
        lock (_gate) _active?.Offer(generation, bytes, receivedTimestamp);
    }

    // Raw-file positions reflect accepted bytes, including queued disk writes.
    // Taking this under the same lock as Offer gives sequence observation windows
    // stable byte boundaries without reading or writing files in the RX path.
    public long SnapshotActiveAcceptedBytes()
    {
        lock (_gate) return _active?.AcceptedBytes ?? -1;
    }

    public string? ActiveInterruptionReason
    {
        get { lock (_gate) return _active?.InterruptionReason; }
    }

    public void ConnectionChanged(CaptureConnection connection)
    {
        lock (_gate) ApplyConnectionChanged(connection);
    }

    // Status callbacks may wait behind another callback or a new capture. Read
    // the live state under the capture lock rather than carrying a stale snapshot.
    public void RefreshConnection(Func<CaptureConnection> getConnection)
    {
        lock (_gate) { if (_active is not null) ApplyConnectionChanged(getConnection()); }
    }

    private void ApplyConnectionChanged(CaptureConnection connection)
    {
        if (_active is { } active && connection.Generation >= active.Generation &&
            (!connection.Connected || connection.Generation != active.Generation))
            active.Interrupt("disconnected");
    }

    public bool Cancel()
    {
        lock (_gate)
        {
            if (_active is not null) { _active.Interrupt("canceled"); return true; }
            if (_operationStop is null) return false;
            CancelPreparation(_operationStop);
            return true;
        }
    }

    public async Task<CaptureResult> CaptureAsync(CaptureOptions options, string port, Encoding encoding,
        Func<CaptureConnection> getConnection, Func<CancellationToken, Task>? send,
        CancellationToken cancellationToken)
    {
        using var operationStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // This is a zero-wait probe. Publish preparation ownership in the
            // same critical section, leaving no acquired-but-uncancelable gap.
            if (!_operationGate.Wait(0, cancellationToken))
                throw new AutomationException("busy", "Another capture is active.");
            _operationStop = operationStop;
        }
        try
        {
            return await CaptureCoreAsync(options, port, encoding, getConnection, send, operationStop.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_operationStop, operationStop)) _operationStop = null;
                _operationGate.Release();
            }
        }
    }

    private async Task<CaptureResult> CaptureCoreAsync(CaptureOptions options, string port, Encoding encoding,
        Func<CaptureConnection> getConnection, Func<CancellationToken, Task>? send,
        CancellationToken cancellationToken)
    {
        if (options.DurationMs is < 0 or > 60000)
            throw new AutomationException("invalid_argument", "durationMs must be between 0 and 60000.");
        var initial = getConnection();
        cancellationToken.ThrowIfCancellationRequested();
        if (!initial.Connected) throw new AutomationException("disconnected", "Connect a device first.");
        Directory.CreateDirectory(DirectoryPath);
        await Task.Run(Prune, cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("N");
        var rawPath = Path.Combine(DirectoryPath, id + ".bin");
        var textPath = Path.Combine(DirectoryPath, id + ".txt");
        await using var session = new CaptureSession(initial.Generation, rawPath, textPath, encoding);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null) throw new AutomationException("busy", "Another capture is active.");
            _active = session;
        }
        var sendStatus = send is null ? "not_requested" : "not_sent";
        var status = "completed";
        string? error = null;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Interrupted.Token);
        try
        {
            // Subscribe first, but do not TX until the raw/text files and
            // decoder have actually initialized successfully.
            await session.Ready.WaitAsync(operation.Token).ConfigureAwait(false);
            if (session.WriteError is { } initializationError)
                throw new AutomationException("write_error", initializationError);
            cancellationToken.ThrowIfCancellationRequested();
            if (getConnection() is var current && (!current.Connected || current.Generation != initial.Generation))
                throw new AutomationException("disconnected", "The receive session changed before TX.");
            if (send is not null)
            {
                // After this point a failure may still have sent bytes. Never retry.
                sendStatus = "unknown";
                await send(operation.Token).ConfigureAwait(false);
                sendStatus = "sent";
            }
            await Task.Delay(options.DurationMs, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            status = session.InterruptionReason ?? "canceled";
        }
        catch (Exception ex)
        {
            status = ex is AutomationException a ? a.Code : "send_error";
            error = ex.Message;
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_active, session)) _active = null; }
            await session.FinishAsync().ConfigureAwait(false);
        }
        var final = getConnection();
        if (status == "completed" && (!final.Connected || final.Generation != initial.Generation)) status = "disconnected";
        if (session.WriteError is not null) { status = "write_error"; error = session.WriteError; }
        if (session.DroppedBytes > 0 && status == "completed") status = "rx_gap";
        var serialErrors = Math.Max(0, final.SerialErrorCount - initial.SerialErrorCount);
        if (serialErrors > 0 && status == "completed") status = "rx_gap";
        var result = new CaptureResult(id, status, sendStatus, options.Command, port,
            initial.Generation, session.StartedAt, session.EndedAt, options.DurationMs,
            session.ReceivedBytes, session.StoredBytes, session.DroppedBytes, serialErrors,
            session.DroppedBytes > 0 || serialErrors > 0 || session.WriteError is not null, error, rawPath, textPath,
            options.TxMode, options.LineEnding, options.PreviewFormat);
        try
        {
            await File.WriteAllTextAsync(MetadataPath(id), JsonSerializer.Serialize(result, AutomationJson.Options), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preserve the actual TX outcome even when the disk cannot persist
            // metadata. The caller must not mistake this for an unsent command.
            result = result with { CaptureStatus = "write_error", Error = "Capture metadata: " + ex.Message };
        }
        return result;
    }

    public CaptureResult GetResult(string id)
    {
        ValidateId(id);
        lock (_storageGate)
        {
            try
            {
                return JsonSerializer.Deserialize<CaptureResult>(File.ReadAllText(MetadataPath(id)), AutomationJson.Options)
                    ?? throw new AutomationException("capture_not_found", "Capture metadata is unavailable.");
            }
            catch (FileNotFoundException) { throw new AutomationException("capture_not_found", "Capture not found or expired."); }
        }
    }

    public async Task<CapturePage> ReadAsync(string id, long offset = 0, int limit = 16384, string format = "text",
        CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        if (limit is < 1 or > 65536 || offset < 0 || format is not ("text" or "hex"))
            throw new AutomationException("invalid_argument", "Use a nonnegative offset, limit 1..65536, and format text or hex.");
        var path = Path.Combine(DirectoryPath, id + (format == "hex" ? ".bin" : ".txt"));
        FileStream file;
        // Open the read handle atomically with the metadata check relative to
        // pruning. FileShare.Read then pins the entire capture until page I/O ends.
        lock (_storageGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = GetResult(id);
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        }
        await using var readLease = file;
        if (offset > file.Length) throw new AutomationException("invalid_argument", "Offset is beyond the capture.");
        file.Position = offset;
        var bytes = new byte[Math.Min(limit, file.Length - offset)];
        var count = 0;
        while (count < bytes.Length)
        {
            var n = await file.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (n == 0) break;
            count += n;
        }
        if (format == "text" && count > 0)
        {
            if ((bytes[0] & 0xC0) == 0x80)
                throw new AutomationException("invalid_argument", "Use nextOffset from the previous page (UTF-8 boundary).");
            // A text page must not split a UTF-8 character. Very small limits
            // are rejected if no complete character fits, rather than looping.
            var start = count - 1;
            while (start > 0 && (bytes[start] & 0xC0) == 0x80) start--;
            var lead = bytes[start];
            var needed = lead < 0x80 ? 1 : lead < 0xE0 ? 2 : lead < 0xF0 ? 3 : 4;
            if (count - start < needed) count = start;
            if (count == 0) throw new AutomationException("invalid_argument", "Increase limit to at least 4 bytes.");
        }
        var text = format == "hex" ? Convert.ToHexString(bytes.AsSpan(0, count)) : Encoding.UTF8.GetString(bytes, 0, count);
        return new(id, format, offset, offset + count, file.Length, offset + count < file.Length, text);
    }

    private string MetadataPath(string id) => Path.Combine(DirectoryPath, id + ".json");
    private static void ValidateId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new AutomationException("invalid_argument", "Invalid captureId.");
    }

    private void Prune()
    {
        lock (_storageGate)
        {
            var groups = Directory.EnumerateFiles(DirectoryPath, "*.json").Select(p => new FileInfo(p))
                .Where(f => Guid.TryParseExact(Path.GetFileNameWithoutExtension(f.Name), "N", out _))
                .OrderBy(f => f.CreationTimeUtc)
                .Select(f => new[] { ".bin", ".txt", ".json" }
                    .Select(ext => new FileInfo(Path.Combine(DirectoryPath, Path.GetFileNameWithoutExtension(f.Name) + ext))).ToArray())
                .ToArray();
            var retainedCount = groups.Length;
            var retainedBytes = groups.Sum(files => files.Where(f => f.Exists).Sum(f => f.Length));
            foreach (var files in groups)
            {
                if (retainedCount <= 31 && retainedBytes <= 512L * 1024 * 1024) break;
                var size = files.Where(f => f.Exists).Sum(f => f.Length);
                if (!TryDeleteCapture(files)) continue;
                retainedCount--;
                retainedBytes -= size;
            }
            // A locked orphan from an interrupted write must not prevent a new
            // capture either. Completed, pinned captures keep their metadata.
            foreach (var file in Directory.EnumerateFiles(DirectoryPath))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (!Guid.TryParseExact(id, "N", out _) || File.Exists(MetadataPath(id))) continue;
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static bool TryDeleteCapture(FileInfo[] files)
    {
        var leases = new List<FileStream>();
        try
        {
            // Acquire every file before changing any of them. Denying read/write
            // sharing detects both our page readers and external viewers. Delete
            // sharing lets us remove the files while retaining these leases,
            // closing the check/delete race. Metadata is always removed last.
            foreach (var file in files.Where(f => f.Exists))
            {
                if ((file.Attributes & FileAttributes.ReadOnly) != 0) return false;
                leases.Add(new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Delete));
            }
            foreach (var file in files.Where(f => f.Exists)) File.Delete(file.FullName);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _active?.Interrupt("canceled");
            if (_operationStop is { } preparing) CancelPreparation(preparing);
        }
        await _operationGate.WaitAsync().ConfigureAwait(false);
        _operationGate.Release();
        _operationGate.Dispose();
    }

    private static void CancelPreparation(CancellationTokenSource stop)
    {
        var cancellation = stop.CancelAsync();
        if (!cancellation.IsCompletedSuccessfully)
            _ = cancellation.ContinueWith(task => System.Diagnostics.Trace.TraceError("Capture cancellation: {0}", task.Exception),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private sealed class CaptureSession : IAsyncDisposable
    {
        private const long MaxCaptureBytes = 64L * 1024 * 1024;
        private const long MaxPendingBytes = 8L * 1024 * 1024;
        private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1024)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        private readonly Task _writer;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Ready => _ready.Task;
        private readonly CancellationTokenSource _writeStop = new();
        private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
        private long _pendingBytes;
        private long _acceptedBytes;
        public long AcceptedBytes => _acceptedBytes;
        public long Generation { get; }
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset EndedAt { get; private set; }
        public long ReceivedBytes { get; private set; }
        public long StoredBytes { get; private set; }
        public long DroppedBytes { get; private set; }
        public string? WriteError { get; private set; }
        public string? InterruptionReason { get; private set; }
        public CancellationTokenSource Interrupted { get; } = new();

        public CaptureSession(long generation, string rawPath, string textPath, Encoding encoding)
        {
            Generation = generation;
            _writer = Task.Run(() => WriteAsync(rawPath, textPath, encoding));
        }

        // Caller serializes offers, interruption and removal with the service lock.
        public void Offer(long generation, byte[] bytes, long timestamp)
        {
            if (generation != Generation || timestamp < _startedTimestamp || Interrupted.IsCancellationRequested) return;
            ReceivedBytes += bytes.Length;
            if (_acceptedBytes + bytes.Length > MaxCaptureBytes)
            {
                DroppedBytes += bytes.Length;
                Interrupt("size_limit");
                return;
            }
            if (Interlocked.Add(ref _pendingBytes, bytes.Length) > MaxPendingBytes)
            {
                Interlocked.Add(ref _pendingBytes, -bytes.Length);
                DroppedBytes += bytes.Length;
                Interrupt("rx_gap");
                return;
            }
            if (_queue.Writer.TryWrite(bytes)) _acceptedBytes += bytes.Length;
            else
            {
                Interlocked.Add(ref _pendingBytes, -bytes.Length);
                DroppedBytes += bytes.Length;
                Interrupt("rx_gap");
            }
        }

        public void Interrupt(string reason)
        {
            InterruptionReason ??= reason;
            // Do not run arbitrary cancellation continuations in the RX callback.
            _ = Interrupted.CancelAsync();
        }

        public async Task FinishAsync()
        {
            EndedAt = DateTimeOffset.UtcNow;
            _queue.Writer.TryComplete();
            _writeStop.CancelAfter(TimeSpan.FromSeconds(5));
            await _writer.ConfigureAwait(false);
        }

        private async Task WriteAsync(string rawPath, string textPath, Encoding encoding)
        {
            try
            {
                await using var raw = new FileStream(rawPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
                await using var text = new StreamWriter(new FileStream(textPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.Read, 65536, FileOptions.Asynchronous), new UTF8Encoding(false));
                var decoder = encoding.GetDecoder();
                _ready.TrySetResult();
                await foreach (var bytes in _queue.Reader.ReadAllAsync(_writeStop.Token).ConfigureAwait(false))
                {
                    await raw.WriteAsync(bytes, _writeStop.Token).ConfigureAwait(false);
                    StoredBytes += bytes.Length;
                    var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
                    var count = decoder.GetChars(bytes, chars, false);
                    await text.WriteAsync(chars.AsMemory(0, count), _writeStop.Token).ConfigureAwait(false);
                    Interlocked.Add(ref _pendingBytes, -bytes.Length);
                }
                var tail = new char[encoding.GetMaxCharCount(0)];
                var tailCount = decoder.GetChars([], tail, true);
                await text.WriteAsync(tail.AsMemory(0, tailCount), _writeStop.Token).ConfigureAwait(false);
                await text.FlushAsync(_writeStop.Token).ConfigureAwait(false);
                await raw.FlushAsync(_writeStop.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WriteError = ex.Message;
                _ready.TrySetResult();
                _queue.Writer.TryComplete();
                while (_queue.Reader.TryRead(out _)) { }
                Interrupt("write_error");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _queue.Writer.TryComplete();
            _writeStop.CancelAfter(TimeSpan.FromSeconds(5));
            await _writer.ConfigureAwait(false);
            _writeStop.Dispose();
            Interrupted.Dispose();
        }
    }
}
