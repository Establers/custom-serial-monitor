using System.Text;
using System.Text.Json;

namespace SerialMonitor.Core.Automation;

// Sequence ownership spans preflight, every TX, RX persistence and offline
// evidence evaluation. Only the explicit run cancellation (or GUI shutdown)
// stops an accepted background run; a polling/starting client may disconnect.
public sealed class AutomationSequenceRunner(IAutomationDevice device, RxCaptureService captures) : IAsyncDisposable
{
    private static readonly TimeSpan NotificationTimeout = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private string? _latest;
    private Preparation? _preparing;
    private bool _disposed;
    private Task? _disposeTask;

    public bool IsActive
    {
        get { lock (_gate) return _preparing is not null || _jobs.Values.Any(job => job.State == "running"); }
    }

    public bool CancelCaptureWhenIdle()
    {
        // StartAsync cannot acquire sequence ownership between this check and
        // the standalone capture cancellation.
        lock (_gate)
        {
            if (_preparing is not null || _jobs.Values.Any(job => job.State == "running"))
                throw new AutomationException("busy", "A sequence run owns RX capture. Use sequence_cancel with its runId.");
            return captures.Cancel();
        }
    }

    public async Task<AutomationSequenceRun> StartAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var name = arguments.String("name");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            throw new AutomationException("invalid_argument", "A sequence name of 1..128 characters is required.");
        cancellationToken.ThrowIfCancellationRequested();
        Preparation preparation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_preparing is not null || _jobs.Values.Any(job => job.State == "running"))
                throw new AutomationException("busy", "Another sequence is preparing or running.");
            preparation = new(cancellationToken);
            _preparing = preparation;
        }
        var transferred = false;
        var reserved = false;
        try
        {
            cancellationToken = preparation.Stop.Token;
            var state = (AutomationDeviceState)await device.InvokeAsync("reserve", arguments, cancellationToken).ConfigureAwait(false);
            reserved = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (!state.Connected) throw new AutomationException("disconnected", "Connect a device first.");
            if (arguments.String("port") is { } port && !string.Equals(port, state.Port, StringComparison.OrdinalIgnoreCase))
                throw new AutomationException("port_mismatch", "GUI is connected to " + state.Port + ", not the requested port.");
            var supplied = await device.InvokeAsync("sequence_get", arguments, cancellationToken).ConfigureAwait(false);
            var definition = AutomationSequenceValidation.Parse(JsonSerializer.SerializeToElement(supplied, AutomationJson.Options));
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var encoding = Encoding.GetEncoding(state.Encoding switch { "Ascii" => "us-ascii", "Cp949" => "ks_c_5601-1987", _ => "utf-8" },
                EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
            // Validate every command and expected literal before any device TX.
            foreach (var step in definition.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (step.Expect is { } expect) _ = AutomationSequenceMatcher.PatternBytes(expect, encoding);
                await device.InvokeAsync("validate_send", StepArguments(step), cancellationToken).ConfigureAwait(false);
            }
            EnsureConnection(state);
            cancellationToken.ThrowIfCancellationRequested();
            var job = new Job(definition, state, encoding);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                _jobs.Add(job.RunId, job);
                _order.Enqueue(job.RunId);
                _latest = job.RunId;
                _preparing = null;
                TrimHistory();
                job.Task = Task.Run(() => RunAsync(job), CancellationToken.None);
                transferred = true;
                return Snapshot(job);
            }
        }
        finally
        {
            try
            {
                if (reserved && !transferred) await device.InvokeAsync("release", default, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) { if (ReferenceEquals(_preparing, preparation)) _preparing = null; }
                preparation.Stop.Dispose();
                preparation.Completed.TrySetResult();
            }
        }
    }

    public AutomationSequenceRun Status(string? runId)
    {
        lock (_gate) return Snapshot(Find(runId));
    }

    public object Cancel(string runId)
    {
        lock (_gate)
        {
            if (_disposed) throw new AutomationException("shutting_down", "Sequence control is shutting down.");
            var job = Find(runId);
            var canceled = job.State == "running";
            if (canceled) RequestCancellation(job);
            return new { canceled, run = Snapshot(job) };
        }
    }

    public bool CancelActive()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            var job = _latest is { } latest && _jobs.TryGetValue(latest, out var value) && value.State == "running" ? value : null;
            if (job is not null) { RequestCancellation(job); return true; }
            var stop = _preparing?.Stop;
            if (stop is null) return false;
            // CancelAsync marks the token before ownership can transfer under
            // this lock, but runs callback continuations outside this lock.
            var canceled = stop.CancelAsync();
            if (!canceled.IsCompletedSuccessfully)
                _ = canceled.ContinueWith(task =>
                    System.Diagnostics.Trace.TraceError("Sequence preflight cancellation: {0}", task.Exception),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return true;
        }
    }

    // Called under _gate: mark cancellation before a run can finish/retire, but
    // never execute device callbacks synchronously in a CLI/MCP request handler.
    private void RequestCancellation(Job job)
    {
        if (job.Cancellation is not null) return;
        job.Cancellation = ObserveCancellationAsync(job, job.Stop.CancelAsync());
    }

    private async Task ObserveCancellationAsync(Job job, Task cancellation)
    {
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception ex)
        {
            lock (_gate)
                job.Error = BoundedError("Cancellation: " + ex.GetBaseException().Message + (job.Error is null ? "" : ". " + job.Error));
        }
    }

    private async Task RunAsync(Job job)
    {
        var finalState = "failed";
        string? error = null;
        try
        {
            await ProgressAsync(job).ConfigureAwait(false);
            EnsureConnection(job.Connection);
            var modes = job.Definition.Steps.Select(step => step.TxMode).Distinct().ToArray();
            var endings = job.Definition.Steps.Select(step => step.TxMode == "hex" ? "none" : step.LineEnding).Distinct().ToArray();
            var previewFormat = modes.Contains("hex") ? "hex" : "text";
            // Aggregate capture metadata must not label a HEX/mixed run as a
            // single UTF-8/CRLF command. Exact settings remain on each step.
            var capture = await captures.CaptureAsync(new(0, TxMode: modes.Length == 1 ? modes[0] : "mixed",
                LineEnding: endings.Length == 1 ? endings[0] : "mixed", PreviewFormat: previewFormat), job.Connection.Port, job.Encoding,
                () => device.Connection, token => SendStepsAsync(job, token), job.Stop.Token).ConfigureAwait(false);
            lock (_gate) job.Capture = capture;
            finalState = capture.CaptureStatus switch { "completed" => "completed", "canceled" => "canceled", _ => "failed" };
            if (finalState != "completed") error = capture.Error ?? "RX capture ended with status '" + capture.CaptureStatus + "'. Inspect each sendStatus; do not retry TX automatically.";
            var inconclusive = capture.CaptureStatus != "completed" || capture.RxGapDetected;
            var textContext = new AutomationSequenceMatcher.TextRangeContext(job.Encoding);
            for (var index = 0; index < job.Results.Count; index++)
            {
                AutomationSequenceStepResult step;
                lock (_gate) step = job.Results[index];
                var expectation = job.Definition.Steps[step.DefinitionStepIndex].Expect;
                var evidence = await AutomationSequenceMatcher.EvaluateAsync(capture.RawFile, step.RxStartOffset,
                    step.RxEndOffset, expectation, job.Encoding, inconclusive, job.Stop.Token, textContext).ConfigureAwait(false);
                lock (_gate) job.Results[index] = step with { MatchStatus = evidence };
            }
        }
        catch (OperationCanceledException) { finalState = "canceled"; error = "Sequence canceled. Already started TX cannot be undone."; }
        catch (Exception ex) { finalState = "failed"; error = ex.Message; }
        finally
        {
            if (job.Capture is { } capture)
            {
                try { await InvokeNotificationAsync("capture_result", JsonSerializer.SerializeToElement(capture, AutomationJson.Options), CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { error ??= ex.Message; }
            }
            lock (_gate)
            {
                job.Error = BoundedError(job.Error is null ? error : error is null ? job.Error : job.Error + ". " + error);
                if (finalState != "completed")
                    for (var index = 0; index < job.Results.Count; index++)
                        if (job.Definition.Steps[job.Results[index].DefinitionStepIndex].Expect is not null)
                            job.Results[index] = job.Results[index] with { MatchStatus = "inconclusive" };
            }
            try { await ProgressAsync(job, finalState).ConfigureAwait(false); }
            catch (Exception ex) { lock (_gate) job.Error ??= BoundedError(ex.Message); }
            try
            {
                // If a progress callback timed out, eventual UI release must
                // also clear that run's sequence state before releasing TX.
                await InvokeNotificationAsync("release", JsonSerializer.SerializeToElement(new
                { sequenceProgress = ProgressArguments(job, finalState) }, AutomationJson.Options), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                finalState = "failed";
                lock (_gate) job.Error = BoundedError("Control release: " + ex.Message);
            }
            lock (_gate) { job.State = finalState; job.EndedAt = DateTimeOffset.UtcNow; }
        }
    }

    private async Task SendStepsAsync(Job job, CancellationToken token)
    {
        for (var repeat = 0; repeat < job.Definition.RepeatCount; repeat++)
        {
            for (var stepIndex = 0; stepIndex < job.Definition.Steps.Count; stepIndex++)
            {
                token.ThrowIfCancellationRequested();
                EnsureConnection(job.Connection);
                var definition = job.Definition.Steps[stepIndex];
                int index;
                lock (_gate)
                {
                    index = job.Results.Count;
                    job.StepIndex = index;
                    job.Results.Add(new(index, repeat, stepIndex, definition.Name, "not_sent", -1, -1,
                        definition.Expect is null ? "not_requested" : "inconclusive", TxMode: definition.TxMode,
                        LineEnding: definition.TxMode == "hex" ? "none" : definition.LineEnding));
                }
                await ProgressAsync(job).ConfigureAwait(false);
                var txProgress = Task.CompletedTask;
                try
                {
                    token.ThrowIfCancellationRequested();
                    EnsureConnection(job.Connection);
                    var start = captures.SnapshotActiveAcceptedBytes();
                    if (start < 0) throw new AutomationException("capture_unavailable", "The run RX subscription is unavailable.");
                    lock (_gate) job.Results[index] = job.Results[index] with { SendStatus = "unknown", RxStartOffset = start, RxEndOffset = start };
                    await device.InvokeAsync("send", AutomationSendContext.Bind(StepArguments(definition), job.Connection.SessionGeneration), token).ConfigureAwait(false);
                    lock (_gate) job.Results[index] = job.Results[index] with { SendStatus = "sent" };
                    // Publish actual TX counts immediately, while the fixed RX
                    // observation timer proceeds independently of UI dispatch.
                    txProgress = ProgressAsync(job);
                    await ObserveAsync(definition.CaptureMs, job.Connection, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    lock (_gate) job.Results[index] = job.Results[index] with { Error = BoundedError(ex.Message) };
                    throw;
                }
                finally
                {
                    var end = captures.SnapshotActiveAcceptedBytes();
                    lock (_gate) job.Results[index] = job.Results[index] with { RxEndOffset = end };
                    await txProgress.ConfigureAwait(false);
                }
                await ObserveAsync(definition.DelayAfterMs, job.Connection, token).ConfigureAwait(false);
                lock (_gate) job.CompletedSteps++;
                await ProgressAsync(job).ConfigureAwait(false);
            }
        }
    }

    private async Task ObserveAsync(int milliseconds, AutomationDeviceState expected, CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            EnsureConnection(expected);
            var remaining = milliseconds - System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (remaining <= 0) return;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(100, remaining)), token).ConfigureAwait(false);
        }
    }

    private void EnsureConnection(AutomationDeviceState expected)
    {
        var current = device.Connection;
        if (!current.Connected || current.Generation != expected.SessionGeneration)
            throw new AutomationException("disconnected", "The sequence connection/session changed; no further TX will be sent.");
        if (current.SerialErrorCount > expected.SerialErrors)
            throw new AutomationException("rx_gap", "Serial errors were detected; no further TX will be sent.");
        if (captures.ActiveInterruptionReason is { } reason)
            throw new AutomationException(reason, "RX capture was interrupted; no further TX will be sent.");
    }

    private JsonElement ProgressArguments(Job job, string? state = null)
    {
        lock (_gate) return JsonSerializer.SerializeToElement(new { job.RunId, name = job.Definition.Name, state = state ?? job.State, job.StepIndex,
            job.CompletedSteps, totalSteps = job.TotalSteps,
            sentCount = job.Results.Count(step => step.SendStatus == "sent"),
            failedAttempts = job.Results.Count(step => step.SendStatus == "unknown" && step.Error is not null), job.Error }, AutomationJson.Options);
    }

    private async Task ProgressAsync(Job job, string? state = null) =>
        _ = await InvokeNotificationAsync("sequence_progress", ProgressArguments(job, state),
            state is null ? job.Stop.Token : CancellationToken.None).ConfigureAwait(false);

    private async Task<object> InvokeNotificationAsync(string operation, JsonElement args, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(NotificationTimeout);
        // A late release still owns the real reservation. Let it execute, while
        // bounding our wait; never pretend the GUI has already released TX.
        var pending = device.InvokeAsync(operation, args, operation == "release" ? CancellationToken.None : deadline.Token);
        try { return await pending.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new AutomationException("gui_timeout", "GUI " + operation + " notification timed out. Already started TX is not replayed.");
        }
        finally
        {
            if (!pending.IsCompletedSuccessfully)
                _ = pending.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static JsonElement StepArguments(AutomationSequenceStep step) => JsonSerializer.SerializeToElement(new
    { command = step.Command, txMode = step.TxMode, lineEnding = step.LineEnding }, AutomationJson.Options);

    private Job Find(string? runId)
    {
        runId ??= _latest;
        if (runId is null || !_jobs.TryGetValue(runId, out var job))
            throw new AutomationException("run_not_found", "Sequence run not found or expired. Query the latest run before starting again; TX is never replayed automatically.");
        return job;
    }

    private static AutomationSequenceRun Snapshot(Job job) => new(job.RunId, job.Definition.Name, job.State,
        job.StepIndex, job.CompletedSteps, job.TotalSteps, job.StartedAt, job.EndedAt, job.Error,
        job.Connection.Port, job.Connection.SessionGeneration, job.Results.ToArray(), job.Capture);

    private void TrimHistory()
    {
        while (_order.Count > 8)
        {
            var id = _order.Dequeue();
            var job = _jobs[id];
            _jobs.Remove(id);
            _ = DisposeRetiredJobAsync(job);
        }
    }

    private static string? BoundedError(string? error) => error is { Length: > 2048 } ? error[..2048] : error;

    private static async Task DisposeRetiredJobAsync(Job job)
    {
        try { await Task.WhenAll(job.Task, job.Cancellation ?? Task.CompletedTask).ConfigureAwait(false); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Retired sequence cleanup: {0}", ex); }
        finally { job.Stop.Dispose(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Job[] jobs;
        Preparation? preparation;
        lock (_gate)
        {
            _disposed = true;
            jobs = _jobs.Values.ToArray();
            preparation = _preparing;
            foreach (var job in jobs) if (!job.Task.IsCompleted) RequestCancellation(job);
        }
        if (preparation is not null)
        {
            try { await preparation.Stop.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError("Sequence preflight cleanup: {0}", ex); }
            await preparation.Completed.Task.ConfigureAwait(false);
        }
        try { await Task.WhenAll(jobs.SelectMany(job => new[] { job.Task, job.Cancellation ?? Task.CompletedTask })).ConfigureAwait(false); }
        finally { foreach (var job in jobs) job.Stop.Dispose(); }
    }

    private sealed class Preparation(CancellationToken token)
    {
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Job(AutomationSequenceDefinition definition, AutomationDeviceState connection, Encoding encoding)
    {
        public string RunId { get; } = Guid.NewGuid().ToString("N");
        public AutomationSequenceDefinition Definition { get; } = definition;
        public AutomationDeviceState Connection { get; } = connection;
        public Encoding Encoding { get; } = encoding;
        public CancellationTokenSource Stop { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
        public Task? Cancellation { get; set; }
        public string State { get; set; } = "running";
        public int StepIndex { get; set; } = -1;
        public int CompletedSteps { get; set; }
        public int TotalSteps { get; } = definition.Steps.Count * definition.RepeatCount;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? EndedAt { get; set; }
        public string? Error { get; set; }
        public List<AutomationSequenceStepResult> Results { get; } = [];
        public CaptureResult? Capture { get; set; }
    }
}
