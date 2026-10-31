using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace SerialMonitor.Core.Sequences;

// One bounded ordered queue owns RX, matching and disk writes. The serial callback
// only offers immutable chunks; it never runs a matcher, UI callback or file I/O.
public sealed class SequenceRunSession : IAsyncDisposable
{
    private sealed record Work(byte[]? Bytes, long Generation, long Timestamp, Func<Task>? Action);
    private readonly Channel<Work> _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(2048)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly object _ingress = new();
    private readonly CancellationTokenSource _failure = new();
    private readonly CancellationTokenSource _ioStop = new();
    private readonly FileStream _lease;
    private readonly FileStream _results;
    private readonly FileStream _index;
    private readonly FileStream _rxTimes;
    private readonly long _clockTimestamp = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset _clockTime = DateTimeOffset.UtcNow;
    private SequenceRunStep? _lastTx;
    private long _lastTxOffset, _lastTxTimestamp, _lastTxGeneration;
    private long _lastTxConnectionEnd = -1, _lastTxSerialErrorTimestamp;
    private readonly Encoding _encoding;
    private TextDecoderContext _textContext;
    private long _textContextOffset, _textContextGeneration = long.MinValue;
    private byte[] _lastTxTextPrefix = [];
    private readonly Task _worker;
    private readonly int _lastStepNumber;
    private int _repeatNumber;
    private bool _repeatHasCheck, _repeatMiss, _repeatUncertain;
    private FileStream? _raw;
    private long _rawSegment = -1, _bytes, _pendingBytes;
    private string? _fault;
    private bool _closed;
    private bool _connectionObserved, _connectionConnected;
    private long _connectionGeneration;
    private Task? _disposeTask;
    private SequenceRunSummary _summary;
    private SequenceStepResult? _active;
    private ResponsePattern? _pattern;
    private ResponseCheck? _check;
    private CancellationToken _checkCancellation;
    private int _matchingStopped;
    private long _generation, _startedTimestamp, _deadline = long.MaxValue, _matchBase, _checkEnd;
    private TaskCompletionSource? _matched;
    private bool _checkClosed;
    private bool _observationStarted;
    private long _nextId;
    public string RunId { get; }
    public string DirectoryPath { get; }
    public string? Fault => Volatile.Read(ref _fault);
    public CancellationToken FailureToken => _failure.Token;
    public SequenceRunSummary Summary => Volatile.Read(ref _summary);

    private SequenceRunSession(string path, SequenceRunDefinition definition, Encoding encoding)
    {
        DirectoryPath = path;
        RunId = Path.GetFileName(path);
        _encoding = encoding;
        _textContext = new(encoding);
        _summary = new(RunId, definition.Name, DateTimeOffset.Now, null, "running", 0, 0, 0, 0, 0, 0, 0,
            Encoding: definition.Encoding, TotalRepeats: definition.RepeatCount);
        _lastStepNumber = definition.Steps.Last().Number;
        _lease = new(Path.Combine(path, "run.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            _results = Create("steps.jsonl");
            try
            {
                _index = Create("steps.idx");
                try { _rxTimes = Create("rx-times.idx"); }
                catch { _index.Dispose(); throw; }
            }
            catch { _results.Dispose(); throw; }
        }
        catch { _lease.Dispose(); throw; }
        _worker = Task.Run(WorkerAsync);
    }

    public static Task<SequenceRunSession> StartAsync(SequenceRunStore store, SequenceRunDefinition definition,
        Encoding encoding, CancellationToken token) => StartAsync(store, definition, encoding, token, null);

    // The internal preparation seam permits deterministic startup I/O failures.
    internal static async Task<SequenceRunSession> StartAsync(SequenceRunStore store, SequenceRunDefinition definition,
        Encoding encoding, CancellationToken token, Action<SequenceRunSession>? beforeInitialSummary)
    {
        await Task.Run(() =>
        {
            try { store.Prune(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }, token).ConfigureAwait(false);
        var path = store.RunPath(Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        SequenceRunSession? session = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(path, "definition.json"), JsonSerializer.Serialize(definition), token).ConfigureAwait(false);
            session = new SequenceRunSession(path, definition, encoding);
            beforeInitialSummary?.Invoke(session);
            await session.EnqueueAsync(session.SaveSummaryAsync, token).ConfigureAwait(false);
            return session;
        }
        catch
        {
            if (session is not null)
                try { await session.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
            // This freshly generated GUID directory has never been exposed to RX
            // or TX. Remove only this failed preparation, not a previous run.
            try { Directory.Delete(path, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private FileStream Create(string name) => new(Path.Combine(DirectoryPath, name), FileMode.CreateNew,
        FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);

    public void Offer(long generation, byte[] bytes, long timestamp)
    {
        if (bytes.Length == 0) return;
        lock (_ingress)
        {
            if (_closed || Fault is not null) return;
            if (Interlocked.Add(ref _pendingBytes, bytes.Length) > 8L * 1024 * 1024)
            { Interlocked.Add(ref _pendingBytes, -bytes.Length); Fail("Sequence RX queue exceeded 8 MiB; evidence is incomplete."); return; }
            if (!_queue.Writer.TryWrite(new(bytes, generation, timestamp, null)))
            { Interlocked.Add(ref _pendingBytes, -bytes.Length); Fail("Sequence RX queue is full; evidence is incomplete."); }
        }
    }

    private DateTimeOffset WallTime(long timestamp) => _clockTime + Stopwatch.GetElapsedTime(_clockTimestamp, timestamp);

    public Task BeginAsync(SequenceRunStep step, int repeat, int attempt, long generation, CancellationToken token)
    {
        var beginning = Stopwatch.GetTimestamp();
        return EnqueueAsync(async () =>
        {
            if (_active is not null) await EndActiveAsync("inconclusive", "Previous attempt interrupted.").ConfigureAwait(false);
            if (_repeatNumber != repeat)
            { _repeatNumber = repeat; _repeatHasCheck = false; _repeatMiss = false; _repeatUncertain = false; _lastTx = null; }
            var receiveOnly = step.StepKind == "RxCheck";
            if (receiveOnly && (step.DelayMs is < 0 or > 600000 || step.ResponseCheck is null))
                throw new ArgumentException("RX Check needs a pattern and a duration of 0..600000 ms.");
            _generation = generation;
            _startedTimestamp = beginning;
            _deadline = long.MaxValue;
            _check = step.ResponseCheck;
            _checkCancellation = token;
            _pattern = _check is null ? null : ResponsePattern.Compile(_check, _encoding);
            _matched = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _checkClosed = false;
            _observationStarted = receiveOnly;
            _matchBase = -1; _checkEnd = _bytes;
            var commandPreview = step.Command.Length <= 4096 ? step.Command : step.Command[..4096] + "… [full TX: definition.json]";
            var hexPreview = step.TxHex.Length <= 32768 ? step.TxHex : step.TxHex[..32768] + "… [full TX: definition.json]";
            var previousTx = receiveOnly ? _lastTx : null;
            var startOffset = previousTx is null ? _bytes : _lastTxOffset;
            if (previousTx is not null)
            {
                // The caller can observe the replacement before its status/RX
                // marker arrives. Seal once; late old callbacks cannot extend it.
                if (_lastTxConnectionEnd < 0 && _lastTxGeneration != generation)
                    _lastTxConnectionEnd = _bytes;
                _generation = _lastTxGeneration;
                _startedTimestamp = _lastTxTimestamp;
                if (_lastTxConnectionEnd >= 0) _checkEnd = _lastTxConnectionEnd;
            }
            _active = new(++_nextId, repeat, step.Number, attempt, commandPreview, hexPreview, receiveOnly ? "not_requested" : "unknown",
                _check is null ? "not_requested" : "inconclusive", _check?.Pattern, _check?.Kind,
                receiveOnly ? step.DelayMs : _check?.TimeoutMs ?? 0, WallTime(beginning), WallTime(beginning), null,
                startOffset, _checkEnd, _checkEnd, null, 0, null, StepKind: step.StepKind,
                RelatedTxStep: previousTx?.Number, RelatedTxAt: previousTx is null ? null : WallTime(_lastTxTimestamp),
                RelatedTxHex: previousTx is null ? null : Preview(previousTx.TxHex, 32768),
                RelatedTxCommand: previousTx is null ? null : Preview(previousTx.Command, 4096), TxMode: step.TxMode);
            if (receiveOnly)
            {
                var prefix = previousTx is null
                    ? await CaptureTextPrefixAsync(_bytes, generation, token).ConfigureAwait(false) : _lastTxTextPrefix;
                _pattern?.PrimeText(prefix);
                _active = _active with { RxDecoderPrefix = prefix.Length == 0 ? null : prefix };
            }
            if (!receiveOnly) _pendingTx = step;
            if (previousTx is not null)
            {
                // Replay only the preceding TX's connection, up to its first
                // disconnect. A complete earlier response survives reconnect;
                // a partial response cannot borrow bytes from the new connection.
                await ReplayAsync(startOffset, _checkEnd, token).ConfigureAwait(false);
                if (_lastTxSerialErrorTimestamp >= _lastTxTimestamp)
                    InvalidateActive("Serial RX error after the preceding TX.");
                else if (_lastTxConnectionEnd >= 0 || _lastTxGeneration != generation)
                    InvalidateActive("Connection changed after the preceding TX.", preserveMatch: true);
            }
            if (_connectionObserved && (!_connectionConnected || _connectionGeneration != _generation))
                InvalidateActive("Connection changed before response observation was ready.", preserveMatch: true);
            Volatile.Write(ref _summary, Summary with { CurrentRepeat = repeat, CurrentStep = step.Number, Phase = receiveOnly ? "RX Check" : "TX" });
            await SaveSummaryAsync().ConfigureAwait(false);
        }, token);
    }

    private SequenceRunStep? _pendingTx;
    private static string Preview(string text, int limit) => text.Length <= limit ? text : text[..limit] + "… [full TX: definition.json]";

    private async Task<byte[]> CaptureTextPrefixAsync(long end, long generation, CancellationToken token)
    {
        // Advance once through recorded RX in bounded pages. Generation changes
        // reset the decoder; no text state can cross a reconnect.
        var store = new SequenceRunStore(Path.GetDirectoryName(DirectoryPath)!);
        while (_textContextOffset < end)
        {
            token.ThrowIfCancellationRequested();
            var slices = await store.ReadRxTimelineAsync(RunId, _textContextOffset, end, token).ConfigureAwait(false);
            if (slices.Count == 0) throw new IOException("RX decoder context is incomplete.");
            foreach (var slice in slices)
            {
                token.ThrowIfCancellationRequested();
                if (slice.Offset != _textContextOffset) throw new IOException("RX decoder context contains a gap.");
                if (_textContextGeneration != slice.Generation)
                { _textContext = new(_encoding); _textContextGeneration = slice.Generation; }
                _textContext.Observe(slice.Bytes);
                _textContextOffset = slice.Offset + slice.Bytes.Length;
            }
        }
        if (_textContextGeneration != generation)
        { _textContext = new(_encoding); _textContextGeneration = generation; }
        token.ThrowIfCancellationRequested();
        return _textContext.PendingBytes();
    }

    private async Task ReplayAsync(long start, long end, CancellationToken token)
    {
        var store = new SequenceRunStore(Path.GetDirectoryName(DirectoryPath)!);
        while (start < end && _active?.MatchStart is null)
        {
            token.ThrowIfCancellationRequested();
            if (!CanMatch) return;
            var rows = await store.ReadRxTimelineAsync(RunId, start, end, token).ConfigureAwait(false);
            if (rows.Count == 0) throw new IOException("RX timestamp history is incomplete.");
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                if (row.Generation == _generation && row.Timestamp >= _startedTimestamp)
                    Feed(row.Bytes, row.Offset, row.Timestamp);
                start = row.Offset + row.Bytes.Length;
                if (_active?.MatchStart is not null) break;
            }
        }
    }

    public async Task CheckFullWindowAsync(CancellationToken token)
    {
        var item = Volatile.Read(ref _active);
        if (item is null || item.StepKind != "RxCheck") throw new InvalidOperationException("No active RX Check.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, FailureToken);
        // Preparing metadata/replaying earlier RX must not consume the user's
        // configured wait. Observation is already armed; now wait the full interval.
        Volatile.Write(ref _deadline, Stopwatch.GetTimestamp() + (long)(item.TimeoutMs * (double)Stopwatch.Frequency / 1000));
        await Task.Delay(item.TimeoutMs, linked.Token).ConfigureAwait(false);
        await EnqueueAsync(async () =>
        {
            if (_active is null || _active.StepKind != "RxCheck") throw new InvalidOperationException("No active RX Check.");
            if (!_checkClosed) _active = _active with { CheckStatus = _active.MatchStart.HasValue ? "matched" : "not_matched", CheckRxEnd = _checkEnd };
            _checkClosed = true;
            await SaveSummaryAsync().ConfigureAwait(false);
        }, linked.Token).ConfigureAwait(false);
    }

    // Ordered marker immediately before invoking TX, without an asynchronous
    // dispatcher round trip. RX queued during preparation is not response evidence.
    public void ArmForSend()
    {
        var started = Stopwatch.GetTimestamp();
        lock (_ingress)
        {
            if (_closed || Fault is not null) throw new IOException(Fault ?? "Sequence recording closed.");
            if (!_queue.Writer.TryWrite(new(null, 0, 0, async () =>
            {
                byte[] prefix;
                try { prefix = await CaptureTextPrefixAsync(_bytes, _generation, _checkCancellation).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_checkCancellation.IsCancellationRequested) { return; }
                _pattern?.PrimeText(prefix);
                _startedTimestamp = started; _observationStarted = true;
                _matchBase = -1; _checkEnd = _bytes;
                _lastTx = _pendingTx; _lastTxOffset = _bytes; _lastTxTimestamp = started; _lastTxGeneration = _generation;
                _lastTxConnectionEnd = -1; _lastTxSerialErrorTimestamp = 0;
                _lastTxTextPrefix = prefix;
                if (_active is not null) _active = _active with { RxStart = _bytes, CheckRxEnd = _bytes, StartedAt = WallTime(started),
                    RxDecoderPrefix = prefix.Length == 0 ? null : prefix };
            })))
            { Fail("Unable to arm sequence RX observation; no TX should be sent."); throw new IOException(Fault); }
        }
    }

    public async Task SentAsync(bool sent, long sentTimestamp, CancellationToken token)
    {
        if (_check is { } check) Volatile.Write(ref _deadline,
            sentTimestamp + (long)(check.TimeoutMs * (double)Stopwatch.Frequency / 1000));
        Task? matched = null;
        int waitMs = 0;
        await EnqueueAsync(async () =>
        {
            if (_active is null) throw new InvalidOperationException("No active sequence attempt.");
            _active = _active with { SendStatus = sent ? "sent" : "unknown" };
            if (!sent) { await EndActiveAsync("inconclusive", "TX did not complete; device receipt is unknown.").ConfigureAwait(false); return; }
            Volatile.Write(ref _summary, Summary with { Sent = Summary.Sent + 1, Phase = _check is null ? "delay" : "checking" });
            await SaveSummaryAsync().ConfigureAwait(false);
            if (_check is null) { _checkClosed = true; return; }
            _deadline = sentTimestamp + (long)(_check.TimeoutMs * (double)Stopwatch.Frequency / 1000);
            matched = _matched!.Task;
            waitMs = Math.Max(0, (int)Math.Ceiling((_deadline - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency));
        }, token).ConfigureAwait(false);
        if (!sent || matched is null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, FailureToken);
        using var timerStop = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        var timer = Task.Delay(waitMs, timerStop.Token);
        await Task.WhenAny(matched, timer).WaitAsync(linked.Token).ConfigureAwait(false);
        timerStop.Cancel();
        await EnqueueAsync(async () =>
        {
            if (_active is not null)
            {
                if (!_checkClosed)
                    _active = _active with { CheckStatus = _active.MatchStart.HasValue ? "matched" : "not_matched",
                        CheckRxEnd = _active.MatchStart.HasValue ? _active.MatchStart.Value + _active.MatchLength : _checkEnd };
                _checkClosed = true;
            }
            Volatile.Write(ref _summary, Summary with { Phase = "delay" });
            await SaveSummaryAsync().ConfigureAwait(false);
        }, linked.Token).ConfigureAwait(false);
    }

    public Task EndStepAsync(CancellationToken token) => EnqueueAsync(() => EndActiveAsync(null, null), token);

    public void SetPhase(string phase)
    {
        lock (_ingress)
        {
            if (_closed || Fault is not null) return;
            _queue.Writer.TryWrite(new(null, 0, 0, async () =>
            {
                Volatile.Write(ref _summary, Summary with { Phase = phase });
                await SaveSummaryAsync().ConfigureAwait(false);
            }));
        }
    }

    public void ConnectionChanged(long generation, bool connected, bool serialError = false)
    {
        var timestamp = Stopwatch.GetTimestamp();
        // Resolve the active step on the worker, after any pending BeginAsync.
        // Connection markers share RX ordering and the same bounded queue.
        lock (_ingress)
        {
            if (_closed || Fault is not null) return;
            if (!_queue.Writer.TryWrite(new(null, generation, timestamp, () =>
            {
                _connectionObserved = true; _connectionGeneration = generation; _connectionConnected = connected;
                if (serialError)
                { _textContext = new(_encoding); _textContextOffset = _bytes; _textContextGeneration = generation; }
                if (_lastTx is not null && _lastTxConnectionEnd < 0)
                {
                    if (serialError && generation == _lastTxGeneration) _lastTxSerialErrorTimestamp = timestamp;
                    if (!connected || generation != _lastTxGeneration) _lastTxConnectionEnd = _bytes;
                }
                var responseSerialError = serialError && generation == _generation;
                if (responseSerialError || !connected || generation != _generation)
                    InvalidateActive(responseSerialError ? "Serial RX error." : "Connection changed during response observation.", preserveMatch: !responseSerialError);
                return Task.CompletedTask;
            }))) Fail("Sequence event queue is full; connection evidence is incomplete.");
        }
    }

    private void InvalidateActive(string error, bool preserveMatch = false)
    {
        if (_active is null || _checkClosed) return;
        var matched = preserveMatch && _active.MatchStart.HasValue;
        _active = _active with { CheckStatus = matched ? "matched" : _check is null ? "not_requested" : "inconclusive",
            CheckRxEnd = _checkEnd, Error = matched ? _active.Error : error };
        _checkClosed = true;
        _matched?.TrySetResult();
    }

    public async Task FinishAsync(string state, string? error = null)
    {
        Interlocked.Exchange(ref _matchingStopped, 1);
        lock (_ingress) _closed = true;
        _ioStop.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await EnqueueAsync(async () =>
            {
                try { await EndActiveAsync(state == "canceled" ? "canceled" : "inconclusive", error ?? Fault).ConfigureAwait(false); }
                catch (Exception ex) { Fail(ex.Message); }
                Volatile.Write(ref _summary, Summary with { EndedAt = DateTimeOffset.Now,
                    State = Fault is null ? state : "failed", Error = Fault ?? error, RxBytes = _bytes, Phase = null });
                await SaveSummaryAsync().ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _queue.Writer.TryComplete(); await _worker.ConfigureAwait(false); }
    }

    private async Task EndActiveAsync(string? overrideStatus, string? error)
    {
        if (_active is not { } item) return;
        item = item with { EndedAt = DateTimeOffset.Now, RxEnd = item.StepKind == "RxCheck" ? _checkEnd : _bytes, Error = error ?? item.Error,
            CheckStatus = item.Pattern is null ? "not_requested" : (!_checkClosed ? overrideStatus ?? item.CheckStatus : item.CheckStatus),
            StepState = overrideStatus ?? "completed" };
        var json = JsonSerializer.SerializeToUtf8Bytes(item);
        if (_results.Position + json.Length > 256L * 1024 * 1024)
            throw new IOException("Sequence metadata storage limit reached (256 MiB per run).");
        var entry = new byte[24];
        BinaryPrimitives.WriteInt64LittleEndian(entry, _results.Position);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(8), json.Length);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(12), item.Repeat);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(16), SequenceRunStore.StatusCode(item.CheckStatus));
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(20), item.Step);
        await _results.WriteAsync(json, _ioStop.Token).ConfigureAwait(false);
        await _results.WriteAsync(new byte[] { 10 }, _ioStop.Token).ConfigureAwait(false);
        await _results.FlushAsync(_ioStop.Token).ConfigureAwait(false);
        await _index.WriteAsync(entry, _ioStop.Token).ConfigureAwait(false);
        await _index.FlushAsync(_ioStop.Token).ConfigureAwait(false);
        _repeatHasCheck |= item.Pattern is not null;
        _repeatMiss |= item.CheckStatus == "not_matched";
        _repeatUncertain |= (item.StepKind == "Tx" && item.SendStatus != "sent") || item.CheckStatus is "inconclusive" or "canceled";
        var completedRepeat = item.Step == _lastStepNumber && (item.SendStatus == "sent" || item.StepKind == "RxCheck") && overrideStatus is null;
        Volatile.Write(ref _summary, Summary with {
            Matched = Summary.Matched + (item.CheckStatus == "matched" ? 1 : 0),
            NotMatched = Summary.NotMatched + (item.CheckStatus == "not_matched" ? 1 : 0),
            NotChecked = Summary.NotChecked + (item.CheckStatus == "not_requested" ? 1 : 0),
            Inconclusive = Summary.Inconclusive + (item.CheckStatus == "inconclusive" ? 1 : 0),
            Attempts = Summary.Attempts + 1, RxBytes = _bytes, Phase = "between steps",
            CompletedRepeats = Summary.CompletedRepeats + (completedRepeat ? 1 : 0),
            MatchedRepeats = Summary.MatchedRepeats + (completedRepeat && _repeatHasCheck && !_repeatMiss && !_repeatUncertain ? 1 : 0),
            MissedRepeats = Summary.MissedRepeats + (completedRepeat && _repeatMiss ? 1 : 0),
            UncertainRepeats = Summary.UncertainRepeats + (completedRepeat && !_repeatMiss && _repeatUncertain ? 1 : 0),
            UncheckedRepeats = Summary.UncheckedRepeats + (completedRepeat && !_repeatHasCheck && !_repeatUncertain ? 1 : 0) });
        _active = null; _pattern = null; _check = null;
        await SaveSummaryAsync().ConfigureAwait(false);
    }

    private async Task WorkerAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (work.Action is not null) await work.Action().ConfigureAwait(false);
                else if (work.Bytes is { } bytes)
                {
                    try
                    {
                        if (_bytes + bytes.Length > 1024L * 1024 * 1024 || _results.Position + _index.Position + _rxTimes.Position > 256L * 1024 * 1024)
                            throw new IOException("Sequence result storage limit reached (1 GiB RX / 256 MiB metadata per run).");
                        if (_lastTx is not null && _lastTxConnectionEnd < 0 && work.Generation != _lastTxGeneration)
                            _lastTxConnectionEnd = _bytes;
                        if (_active is not null && !_checkClosed && work.Generation != _generation)
                            InvalidateActive("RX connection generation changed.", preserveMatch: true);
                        if (CanMatch && _active is not null && _observationStarted && !_checkClosed && work.Generation == _generation &&
                            work.Timestamp >= _startedTimestamp && work.Timestamp <= Volatile.Read(ref _deadline))
                        {
                            _checkEnd = _bytes + bytes.Length;
                            Feed(bytes, _bytes, work.Timestamp);
                        }
                        var chunkStart = _bytes;
                        var consumed = 0;
                        while (consumed < bytes.Length)
                        {
                            var segment = _bytes / SequenceRunStore.SegmentBytes;
                            if (_rawSegment != segment)
                            {
                                if (_raw is not null) await _raw.DisposeAsync().ConfigureAwait(false);
                                _raw = Create($"rx-{segment:D6}.bin"); _rawSegment = segment;
                            }
                            var count = (int)Math.Min(bytes.Length - consumed, SequenceRunStore.SegmentBytes - _bytes % SequenceRunStore.SegmentBytes);
                            await _raw!.WriteAsync(bytes.AsMemory(consumed, count), _ioStop.Token).ConfigureAwait(false);
                            consumed += count; _bytes += count;
                        }
                        await _raw!.FlushAsync(_ioStop.Token).ConfigureAwait(false);
                        var timing = new byte[SequenceRunStore.RxTimeEntryBytes];
                        BinaryPrimitives.WriteInt64LittleEndian(timing, chunkStart);
                        BinaryPrimitives.WriteInt32LittleEndian(timing.AsSpan(8), bytes.Length);
                        BinaryPrimitives.WriteInt64LittleEndian(timing.AsSpan(16), WallTime(work.Timestamp).UtcTicks);
                        BinaryPrimitives.WriteInt64LittleEndian(timing.AsSpan(24), work.Timestamp);
                        BinaryPrimitives.WriteInt64LittleEndian(timing.AsSpan(32), work.Generation);
                        await _rxTimes.WriteAsync(timing, _ioStop.Token).ConfigureAwait(false);
                        await _rxTimes.FlushAsync(_ioStop.Token).ConfigureAwait(false);
                    }
                    finally { Interlocked.Add(ref _pendingBytes, -bytes.Length); }
                }
            }
            catch (Exception ex) { Fail(ex.Message); }
        }
    }

    private void Feed(byte[] bytes, long offset, long timestamp)
    {
        if (!CanMatch || _active is null || _active.MatchStart is not null || _pattern is null) return;
        if (_matchBase < 0) _matchBase = offset;
        // Bound CPU work between cancellation checks even for one large RX chunk.
        // Pending RX still goes to disk; cancellation only ends response matching.
        for (var consumed = 0; consumed < bytes.Length && CanMatch; consumed += 1024)
        {
            if (!_pattern.Feed(bytes.AsSpan(consumed, Math.Min(1024, bytes.Length - consumed)))) continue;
            if (!CanMatch) return;
            _active = _active with { MatchStart = _matchBase + _pattern.MatchOffset,
                MatchLength = _pattern.Length, ResponseMs = Stopwatch.GetElapsedTime(_startedTimestamp, timestamp).TotalMilliseconds,
                MatchedAt = WallTime(timestamp) };
            _matched?.TrySetResult();
            return;
        }
    }

    private bool CanMatch => Volatile.Read(ref _matchingStopped) == 0 &&
        !_checkCancellation.IsCancellationRequested && !_ioStop.IsCancellationRequested && !_failure.IsCancellationRequested;

    internal async Task EnqueueAsync(Func<Task> action, CancellationToken token)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new(null, 0, 0, async () =>
        {
            try { token.ThrowIfCancellationRequested(); await action().ConfigureAwait(false); done.TrySetResult(); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { done.TrySetCanceled(token); }
            catch (Exception ex) { Fail(ex.Message); done.TrySetException(ex); }
        }), token).ConfigureAwait(false);
        await done.Task.WaitAsync(token).ConfigureAwait(false);
        if (Fault is { } fault && !_closed) throw new IOException(fault);
    }

    private Task SaveSummaryAsync() => SaveAsync();
    private async Task SaveAsync()
    {
        var temporary = Path.Combine(DirectoryPath, "summary.tmp");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(Summary), _ioStop.Token).ConfigureAwait(false);
        await SequenceSummaryFileCommit.CommitAsync(temporary, Path.Combine(DirectoryPath, "summary.json"), _ioStop.Token).ConfigureAwait(false);
    }
    private void Fail(string error)
    {
        if (Interlocked.CompareExchange(ref _fault, error, null) is not null) return;
        try { _ = _failure.CancelAsync(); } catch (ObjectDisposedException) { }
    }
    public ValueTask DisposeAsync()
    {
        lock (_ingress) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        List<Exception> errors = [];
        try
        {
            if (!_closed) await FinishAsync("canceled").ConfigureAwait(false);
            else await _worker.ConfigureAwait(false);
        }
        catch (Exception ex) { errors.Add(ex); }
        finally
        {
            // A failed summary/flush must never strand the lease or skip another handle.
            await CloseAsync(_raw).ConfigureAwait(false);
            await CloseAsync(_results).ConfigureAwait(false);
            await CloseAsync(_index).ConfigureAwait(false);
            await CloseAsync(_rxTimes).ConfigureAwait(false);
            Close(_lease); Close(_failure); Close(_ioStop);
        }
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException(errors);

        async ValueTask CloseAsync(IAsyncDisposable? resource)
        {
            if (resource is null) return;
            try { await resource.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { errors.Add(ex); }
        }
        void Close(IDisposable resource)
        {
            try { resource.Dispose(); }
            catch (Exception ex) { errors.Add(ex); }
        }
    }
}
