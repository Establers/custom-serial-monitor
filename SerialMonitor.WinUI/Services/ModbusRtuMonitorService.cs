using System.Diagnostics;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

// One owner, one outstanding RTU exchange. RX callbacks only offer bounded chunks.
public sealed partial class ModbusRtuMonitorService : IModbusRtuGroupMonitorService, IModbusRtuCommandService
{
    private readonly ISerialService _serial;
    private readonly Action<byte[], long>? _transmitted;
    private readonly object _gate = new();
    private ModbusMonitorSnapshot _snapshot = new(0, ModbusMonitorState.Idle, false, false,
        null, null, 0, 0, null, null, false);
    private CancellationTokenSource? _stop;
    private TaskCompletionSource? _completion;
    private PendingWrite? _pendingWrite;
    private ModbusMonitorSettings? _settings;
    private bool _disposed;
    private long _valueGeneration;

    public ModbusRtuMonitorService(ISerialService serial, Action<byte[], long>? transmitted = null)
    {
        _serial = serial;
        _transmitted = transmitted;
    }

    public bool IsBusy { get { lock (_gate) return _snapshot.IsBusy; } }

    // Snapshot arrays are published once and never modified by the engine.
    public ModbusMonitorSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            if (_snapshot.ValuesCurrent && (!_serial.IsConnected || _serial.ReceiveSessionGeneration != _valueGeneration))
                SetSnapshot(_snapshot with { ValuesCurrent = false,
                    State = _snapshot.IsBusy ? _snapshot.State : ModbusMonitorState.Disconnected });
            return _snapshot;
        }
    }

    public Task RunAsync(ModbusMonitorSettings settings, bool repeat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Clone();
        settings.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        return Begin(settings, repeat, null, cancellationToken);
    }

    public Task WriteAsync(ModbusMonitorSettings settings, int address, IReadOnlyList<ushort> values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(values);
        settings = settings.Clone();
        settings.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var copy = values.ToArray();
        settings.ValidateWrite(address, copy.Length);
        if (settings.Function is not (1 or 3)) throw new InvalidOperationException("This area is read-only.");
        if (address < settings.Address || copy.Length == 0 || (long)address + copy.Length > (long)settings.Address + settings.Quantity)
            throw new ArgumentOutOfRangeException(nameof(address), "The write must stay inside the monitored area.");
        var function = settings.Function == 1 ? (copy.Length == 1 ? (byte)5 : (byte)15)
            : copy.Length == 1 ? (byte)6 : (byte)16;
        var packet = copy.Length == 1 ? ModbusRtuRequestBuilder.WriteSingle(settings.UnitId, function, address, copy[0])
            : ModbusRtuRequestBuilder.WriteMultiple(settings.UnitId, function, address, copy);
        var write = new PendingWrite(address, copy, packet, cancellationToken);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (HasCommandMutation() || _legacyWriteInProgress)
                throw new InvalidOperationException("Another Modbus write or command is already active.");
            if (_groupsRunning) throw new InvalidOperationException("Use the captured polling group to write while group polling is active.");
            if (_snapshot.IsBusy)
            {
                if (!_snapshot.IsPolling || _stop is null || _stop.IsCancellationRequested || _pendingWrite is not null ||
                    _snapshot.WriteState is ModbusWriteState.Queued or ModbusWriteState.Writing || _snapshot.State == ModbusMonitorState.Error)
                    throw new InvalidOperationException("A Modbus operation is already active.");
                if (_settings?.Request != settings.Request)
                    throw new InvalidOperationException("The polling configuration changed.");
                // A caller cannot weaken the definitions captured by active polling.
                _settings.ValidateWrite(address, copy.Length);
                _pendingWrite = write;
                SetSnapshot(_snapshot with { WriteState = ModbusWriteState.Queued });
                return write.Completion.Task;
            }
        }
        return AwaitStandaloneWriteAsync(Begin(settings, false, write, cancellationToken), write.Completion.Task);
    }

    private static async Task AwaitStandaloneWriteAsync(Task operation, Task write)
    {
        try { await operation.ConfigureAwait(false); }
        catch
        {
            // The operation task carries the same failure to the caller.
            try { await write.ConfigureAwait(false); } catch (Exception) { }
            throw;
        }
        await write.ConfigureAwait(false);
    }

    private Task Begin(ModbusMonitorSettings settings, bool repeat, PendingWrite? firstWrite, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Bind the operation before either admission lock or the worker queue can
        // wait. A replacement connection must require a new explicit operation.
        var expectedGeneration = _serial.ReceiveSessionGeneration;
        if (!_serial.IsConnected || _serial.ReceiveSessionGeneration != expectedGeneration)
            throw new InvalidOperationException("The original serial connection changed before Modbus could start.");
        TaskCompletionSource completion;
        CancellationTokenSource stop;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_snapshot.IsBusy) throw new InvalidOperationException("A Modbus operation is already active.");
            if (!_serial.IsConnected || _serial.ReceiveSessionGeneration != expectedGeneration)
                throw new InvalidOperationException("The original serial connection changed before Modbus could start.");
            var sameRequest = _settings?.Request == settings.Request;
            _settings = settings;
            _operationGeneration = expectedGeneration;
            _stop = stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            _completion = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Clear before scheduling: old values must never acquire new addresses.
            SetSnapshot(_snapshot with { State = repeat ? ModbusMonitorState.Polling : ModbusMonitorState.Reading,
                IsBusy = true, IsPolling = repeat, Values = sameRequest ? _snapshot.Values : null,
                UpdatedAt = sameRequest ? _snapshot.UpdatedAt : null, ValuesCurrent = false,
                SuccessCount = 0, ErrorCount = 0, Error = null, ResponseMilliseconds = null,
                ReadRetryDelayMs = 0,
                WriteState = firstWrite is null ? ModbusWriteState.None : ModbusWriteState.Queued });
        }
        // No UI context, filesystem access or parsing in the serial callback.
        _ = Task.Run(() => ExecuteAsync(settings, expectedGeneration, repeat, firstWrite, stop, completion));
        return completion.Task;
    }

    public async Task StopAsync()
    {
        Task? completion;
        lock (_gate)
        {
            _stop?.Cancel();
            completion = _completion?.Task;
        }
        // Graceful stop: retain ownership until the current reply or its timeout.
        if (completion is not null)
        {
            try { await completion.ConfigureAwait(false); }
            catch (Exception) { /* The snapshot and the initiating task report the error. */ }
        }
    }

    private async Task ExecuteAsync(ModbusMonitorSettings settings, long expectedGeneration, bool repeat, PendingWrite? firstWrite,
        CancellationTokenSource stop, TaskCompletionSource completion)
    {
        Exception? failure = null;
        ISerialExclusiveSession? lease = null;
        ModbusReceiveInbox? inbox = null;
        PendingWrite? currentWrite = firstWrite;
        PendingCommand? currentCommand = null;
        var readFailures = 0;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            if (!_serial.IsConnected || _serial.ReceiveSessionGeneration != expectedGeneration)
                throw new InvalidOperationException("The original serial connection changed before Modbus could start.");
            lease = _serial.AcquireExclusiveSession("Modbus RTU");
            if (lease.Generation != expectedGeneration)
                throw new InvalidOperationException("The original serial connection changed before Modbus acquired it.");
            inbox = new ModbusReceiveInbox(_serial, lease.Generation);
            do
            {
                var delayMs = settings.IntervalMs;
                stop.Token.ThrowIfCancellationRequested();
                if (currentWrite is not null)
                {
                    if (currentWrite.Token.IsCancellationRequested)
                    {
                        currentWrite.Completion.TrySetCanceled(currentWrite.Token);
                        lock (_gate) SetSnapshot(_snapshot with { WriteState = ModbusWriteState.None });
                    }
                    else
                    {
                        lock (_gate) _legacyWriteInProgress = true;
                        try { await PerformWriteAsync(lease, inbox, settings, currentWrite, stop.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (currentWrite.Token.IsCancellationRequested && !stop.IsCancellationRequested)
                        {
                            lock (_gate)
                            {
                                _legacyWriteInProgress = false;
                                SetSnapshot(_snapshot with { WriteState = ModbusWriteState.None });
                                currentWrite.Completion.TrySetCanceled(currentWrite.Token);
                            }
                        }
                    }
                    currentWrite = null;
                    if (!repeat) break;
                }
                else
                {
                    try
                    {
                        var result = await ExchangeAsync(lease, inbox, settings.Request.BuildPacket(), settings.TimeoutMs,
                            settings, stop.Token, recoverableRead: repeat).ConfigureAwait(false);
                        readFailures = 0;
                        PublishValues(result.Values, result.ElapsedMs, lease.Generation);
                    }
                    catch (ModbusExchangeException ex) when (repeat && ex.ReadRecoveryAllowed)
                    {
                        readFailures = Math.Min(6, readFailures + 1);
                        delayMs = ReadRetryDelay(settings.IntervalMs, readFailures);
                        lock (_gate)
                        {
                            var queued = _pendingWrite;
                            _pendingWrite = null;
                            SetSnapshot(_snapshot with { State = ModbusMonitorState.Error, ValuesCurrent = false,
                                Error = ex.Message, ReadRetryDelayMs = delayMs, ErrorCount = _snapshot.ErrorCount + 1,
                                WriteState = queued is null ? _snapshot.WriteState : ModbusWriteState.Failed });
                            queued?.Completion.TrySetException(ex);
                        }
                        await RecoverReadAsync(lease, inbox, settings.TimeoutMs, ex, stop.Token).ConfigureAwait(false);
                    }
                    if (!repeat) break;
                }
                // Fixed delay, without catch-up bursts; write queue capacity is one.
                var until = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(until).TotalMilliseconds < delayMs)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    inbox.EnsureConnection();
                    lock (_gate)
                    {
                        if (_pendingWrite is { } queued) { currentWrite = queued; _pendingWrite = null; }
                        if (currentWrite is null) currentCommand = TakePendingCommand();
                    }
                    if (currentWrite is not null) break;
                    if (currentCommand is not null)
                    {
                        await ProcessQueuedCommandAsync(currentCommand, lease, inbox, stop.Token).ConfigureAwait(false);
                        currentCommand = null;
                        continue;
                    }
                    await Task.Delay(Math.Min(20, settings.IntervalMs), stop.Token).ConfigureAwait(false);
                }
            } while (repeat);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure = ex;
            // Unknown completion can leave a late reply on RTU. Fence the physical
            // session before releasing ownership to sequences or another request.
            if (lease is not null && ex is ModbusExchangeException { UnknownCompletion: true })
            {
                try { await lease.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception disconnectError) { failure = new AggregateException(ex, disconnectError); }
            }
            currentWrite?.Completion.TrySetException(failure);
        }
        finally
        {
            inbox?.Dispose();
            if (lease is not null)
            {
                try { await lease.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { failure ??= ex; }
            }
            lock (_gate)
            {
                currentWrite?.Completion.TrySetCanceled();
                _pendingWrite?.Completion.TrySetCanceled();
                _pendingWrite = null;
                _legacyWriteInProgress = false;
                SetSnapshot(_snapshot with { State = failure is not null ? ModbusMonitorState.Error
                        : !_serial.IsConnected ? ModbusMonitorState.Disconnected : ModbusMonitorState.Stopped,
                    IsBusy = false, IsPolling = false,
                    ValuesCurrent = failure is null && !repeat && _snapshot.ValuesCurrent && _serial.IsConnected &&
                        _serial.ReceiveSessionGeneration == _valueGeneration,
                    Error = failure?.Message ?? _snapshot.Error, ReadRetryDelayMs = 0,
                    ErrorCount = _snapshot.ErrorCount + (failure is null ? 0 : 1),
                    WriteState = failure is not null && _snapshot.WriteState is ModbusWriteState.Queued or ModbusWriteState.Writing
                        ? ModbusWriteState.Failed
                        : _snapshot.WriteState is ModbusWriteState.Queued or ModbusWriteState.Writing
                            ? ModbusWriteState.None : _snapshot.WriteState });
                _stop = null;
                _completion = null;
                FinishOwnedCommands(currentCommand, failure, stop.IsCancellationRequested);
            }
            stop.Dispose();
            if (failure is not null) completion.TrySetException(failure);
            else completion.TrySetResult();
        }
    }

    private async Task PerformWriteAsync(ISerialExclusiveSession lease, ModbusReceiveInbox inbox,
        ModbusMonitorSettings settings, PendingWrite write, CancellationToken stop)
    {
        lock (_gate) SetSnapshot(_snapshot with { WriteState = ModbusWriteState.Writing });
        using (var beforeTransmit = CancellationTokenSource.CreateLinkedTokenSource(stop, write.Token))
            await ExchangeAsync(lease, inbox, write.Packet, settings.TimeoutMs, null, beforeTransmit.Token).ConfigureAwait(false);
        // The write has been acknowledged. A stop during the following quiet
        // gap must retain the distinction between an applied and unsent write.
        lock (_gate) SetSnapshot(_snapshot with { WriteState = ModbusWriteState.NotVerified, ValuesCurrent = false });
        if (stop.IsCancellationRequested)
        {
            lock (_gate)
            {
                _legacyWriteInProgress = false;
                SetSnapshot(_snapshot with { WriteState = ModbusWriteState.NotVerified, ValuesCurrent = false });
                write.Completion.TrySetResult();
            }
            return;
        }
        var read = await ExchangeAsync(lease, inbox, settings.Request.BuildPacket(), settings.TimeoutMs, settings, stop).ConfigureAwait(false);
        PublishValues(read.Values, read.ElapsedMs, lease.Generation);
        var verified = read.Values.AsSpan(write.Address - settings.Address, write.Values.Length).SequenceEqual(write.Values);
        lock (_gate) SetSnapshot(_snapshot with { WriteState = verified ? ModbusWriteState.Verified : ModbusWriteState.NotVerified });
        if (!verified) throw new ModbusExchangeException("Write acknowledged, but the read-back value differs.", false);
        lock (_gate)
        {
            _legacyWriteInProgress = false;
            write.Completion.TrySetResult();
        }
    }

    private async Task<(ushort[] Values, double ElapsedMs)> ExchangeAsync(ISerialExclusiveSession lease,
        ModbusReceiveInbox inbox, byte[] packet, int timeoutMs, ModbusMonitorSettings? read, CancellationToken stop,
        bool recoverableRead = false)
    {
        var baud = Math.Max(1, _serial.AppliedBaudRate);
        var gapMs = Math.Max(5, (int)Math.Ceiling(3.5 * 12 * 1000 / baud));
        await inbox.WaitForQuietAsync(gapMs, timeoutMs, stop).ConfigureAwait(false);
        stop.ThrowIfCancellationRequested();
        inbox.EnsureConnection();
        var errorCount = LineErrors;
        var started = Stopwatch.GetTimestamp();
        // Once TX starts, Stop cannot release the owner until this exchange ends.
        // Only a transport disconnect/timeout aborts an outstanding response wait.
        using var deadline = new CancellationTokenSource(timeoutMs);
        var sent = false;
        try
        {
            await lease.SendAsync(packet, deadline.Token).ConfigureAwait(false);
            _transmitted?.Invoke(packet, lease.Generation);
            sent = true;
            var expectedLength = read is null ? 8 : 5 + (read.Function <= 2 ? (read.Quantity + 7) / 8 : read.Quantity * 2);
            var frame = recoverableRead
                ? await inbox.ReadPollingFrameAsync(packet[0], started, deadline.Token).ConfigureAwait(false)
                : await inbox.ReadFrameAsync(expectedLength, started, deadline.Token).ConfigureAwait(false);
            if (LineErrors != errorCount) throw new ModbusExchangeException("Serial line errors occurred during the request.", true);
            var decoded = read is null ? ModbusResponseDecoder.DecodeWrite(packet, frame)
                : ModbusResponseDecoder.DecodeRead(read.Request, frame);
            return decoded.Status switch
            {
                ModbusResponseStatus.Success => (decoded.Values, Stopwatch.GetElapsedTime(started).TotalMilliseconds),
                ModbusResponseStatus.Exception => throw new ModbusExchangeException($"Modbus exception 0x{decoded.ExceptionCode:X2}.", false),
                ModbusResponseStatus.CrcError => throw new ModbusExchangeException("Modbus response CRC error; reconnect before continuing.", true),
                _ => throw new ModbusExchangeException("Unexpected Modbus response; reconnect before continuing.", true)
            };
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            inbox.EnsureConnection();
            throw new ModbusExchangeException(recoverableRead && sent ? "Modbus response timed out."
                : "Modbus response timed out; reconnect before continuing.", true, readRecoveryAllowed: recoverableRead && sent);
        }
        catch (ModbusExchangeException ex) when (recoverableRead && sent)
        {
            // Overflow and replaced/disconnected sessions must remain fatal.
            inbox.EnsureConnection();
            throw new ModbusExchangeException(ex.Message.Replace("; reconnect before continuing.", "."),
                ex.UnknownCompletion, ex, readRecoveryAllowed: true);
        }
        catch (ModbusExchangeException) { throw; }
        catch (Exception ex) { throw new ModbusExchangeException("Modbus exchange failed: " + ex.Message, true, ex); }
    }

    private long LineErrors => _serial.SerialFrameErrorCount + _serial.SerialParityErrorCount +
        _serial.SerialOverrunErrorCount + _serial.SerialRxOverErrorCount;

    private void PublishValues(ushort[] values, double elapsedMs, long generation)
    {
        lock (_gate)
        {
            _valueGeneration = generation;
            SetSnapshot(_snapshot with { Values = values, UpdatedAt = DateTimeOffset.Now,
                State = _snapshot.IsPolling ? ModbusMonitorState.Polling : _snapshot.State, Error = null, ReadRetryDelayMs = 0,
                ValuesCurrent = true, SuccessCount = _snapshot.SuccessCount + 1, ResponseMilliseconds = elapsedMs });
        }
    }

    private void SetSnapshot(ModbusMonitorSnapshot snapshot) => _snapshot = snapshot with { Revision = _snapshot.Revision + 1 };

    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    private sealed record PendingWrite(int Address, ushort[] Values, byte[] Packet, CancellationToken Token)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed class ModbusExchangeException(string message, bool unknownCompletion, Exception? inner = null,
    bool readRecoveryAllowed = false)
    : Exception(message, inner)
{
    public bool UnknownCompletion { get; } = unknownCompletion;
    public bool ReadRecoveryAllowed { get; } = readRecoveryAllowed;
}
