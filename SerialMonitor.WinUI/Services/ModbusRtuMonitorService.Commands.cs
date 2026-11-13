using System.Diagnostics;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed partial class ModbusRtuMonitorService
{
    private ModbusCommandSnapshot _commandSnapshot = new(0, null, null, ModbusCommandState.Idle, false, 0, [], null);
    private PendingCommand? _pendingCommand;
    private bool _commandInProgress, _legacyWriteInProgress;
    private long _operationGeneration;

    public ModbusCommandSnapshot GetCommandSnapshot() { lock (_gate) return _commandSnapshot; }

    public Task ExecuteCommandAsync(ModbusCommandDefinition command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        command = command.Clone();
        command.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        // Preflight every later step before the first write can reach the wire.
        var steps = command.Steps.Select(step => new PreparedCommandStep(step, step.EncodeValues(), step.BuildPacket())).ToArray();
        var generation = _serial.ReceiveSessionGeneration;
        if (!_serial.IsConnected || _serial.ReceiveSessionGeneration != generation)
            throw new InvalidOperationException("The original serial connection changed before the command could start.");
        CancellationTokenSource? standaloneStop = null;
        PendingCommand pending;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_serial.IsConnected || _serial.ReceiveSessionGeneration != generation)
                throw new InvalidOperationException("The original serial connection changed before the command could start.");
            if (HasCommandMutation() || _pendingWrite is not null || _pendingGroupWrite is not null ||
                _groupWriteInProgress || _legacyWriteInProgress ||
                _snapshot.WriteState is ModbusWriteState.Queued or ModbusWriteState.Writing)
                throw new InvalidOperationException("Another Modbus write or command is already active.");
            if (_snapshot.IsBusy && (!_snapshot.IsPolling || _stop is null || _stop.IsCancellationRequested))
                throw new InvalidOperationException("A Modbus operation is already active.");
            if (_snapshot.IsBusy)
            {
                if (generation != _operationGeneration)
                    throw new InvalidOperationException("The original polling connection changed.");
                foreach (var step in steps)
                {
                    if (_groupsRunning)
                        foreach (var group in _groupOrder) ValidateCommandOverlap(group.Definition.Settings, step);
                    else if (_settings is not null) ValidateCommandOverlap(_settings, step);
                }
            }
            pending = new(command, steps, generation, cancellationToken);
            _commandSnapshot = new(_commandSnapshot.Revision + 1, command.Id, command.Name,
                ModbusCommandState.Queued, true, 0, Array.AsReadOnly(steps.Select((step, index) =>
                    CreateCommandStepSnapshot(step, index + 1)).ToArray()), null);
            if (_snapshot.IsBusy) _pendingCommand = pending;
            else
            {
                _commandInProgress = true;
                _operationGeneration = generation;
                _stop = standaloneStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _completion = pending.Completion;
                SetSnapshot(new(0, ModbusMonitorState.Reading, true, false, null, null, 0, 0, null, null, false));
            }
        }
        if (standaloneStop is not null)
            _ = Task.Run(() => ExecuteStandaloneCommandAsync(pending, standaloneStop));
        return pending.Completion.Task;
    }

    private bool HasCommandMutation() => _pendingCommand is not null || _commandInProgress;

    private static void ValidateCommandOverlap(ModbusMonitorSettings monitor, PreparedCommandStep step)
    {
        if (monitor.UnitId != step.Definition.UnitId || monitor.Function != step.Definition.Function) return;
        var start = Math.Max(monitor.Address, step.Definition.Item.Address);
        var end = Math.Min((long)monitor.Address + monitor.Quantity, (long)step.Definition.Item.Address + step.Values.Length);
        if (end > start) monitor.ValidateWrite(start, checked((int)(end - start)));
    }

    private PendingCommand? TakePendingCommand()
    {
        var pending = _pendingCommand;
        if (pending is null) return null;
        _pendingCommand = null;
        _commandInProgress = true;
        return pending;
    }

    private async Task ExecuteStandaloneCommandAsync(PendingCommand command, CancellationTokenSource stop)
    {
        Exception? failure = null;
        ISerialExclusiveSession? lease = null;
        ModbusReceiveInbox? inbox = null;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            if (!_serial.IsConnected || _serial.ReceiveSessionGeneration != command.Generation)
                throw new InvalidOperationException("The original serial connection changed before the command acquired it.");
            lease = _serial.AcquireExclusiveSession("Modbus RTU");
            if (lease.Generation != command.Generation)
                throw new InvalidOperationException("The original serial connection changed before the command acquired it.");
            inbox = new ModbusReceiveInbox(_serial, lease.Generation);
            await PerformCommandAsync(command, lease, inbox, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || command.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure = ex;
            if (lease is not null && ex is ModbusExchangeException { UnknownCompletion: true })
            {
                try { await lease.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception disconnectError) { failure = new AggregateException(ex, disconnectError); }
            }
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
                SetSnapshot(_snapshot with
                {
                    IsBusy = false, IsPolling = false, ValuesCurrent = false,
                    State = failure is not null ? ModbusMonitorState.Error
                        : !_serial.IsConnected ? ModbusMonitorState.Disconnected : ModbusMonitorState.Stopped,
                    Error = failure?.Message, ErrorCount = failure is null ? 0 : 1
                });
                _stop = null;
                _completion = null;
                FinishCommand(command, failure, stop.IsCancellationRequested || command.Token.IsCancellationRequested);
            }
            stop.Dispose();
        }
    }

    // Known command failures affect the command only; a healthy polling engine resumes.
    // Unknown completion propagates to the owner's disconnect-before-release fence.
    private async Task ProcessQueuedCommandAsync(PendingCommand command, ISerialExclusiveSession lease,
        ModbusReceiveInbox inbox, CancellationToken stop)
    {
        Exception? failure = null;
        var canceled = false;
        try { await PerformCommandAsync(command, lease, inbox, stop).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested && command.Token.IsCancellationRequested) { canceled = true; }
        catch (ModbusExchangeException ex) when (!ex.UnknownCompletion) { failure = ex; }
        lock (_gate) FinishCommand(command, failure, canceled);
    }

    private async Task PerformCommandAsync(PendingCommand command, ISerialExclusiveSession lease,
        ModbusReceiveInbox inbox, CancellationToken stop)
    {
        if (lease.Generation != command.Generation)
            throw new InvalidOperationException("The command's original serial connection changed.");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(stop, command.Token);
        for (var index = 0; index < command.Steps.Length; index++)
        {
            operation.Token.ThrowIfCancellationRequested();
            var prepared = command.Steps[index];
            var step = prepared.Definition;
            lock (_gate)
            {
                InvalidateCommandDevice(step.UnitId);
                UpdateCommandStep(index, _commandSnapshot.Steps[index] with { State = ModbusCommandStepState.Writing });
            }
            await ExchangeAsync(lease, inbox, prepared.Packet, step.TimeoutMs, null, operation.Token).ConfigureAwait(false);
            lock (_gate) UpdateCommandStep(index, _commandSnapshot.Steps[index] with
            { State = ModbusCommandStepState.Acknowledged, WriteAcknowledged = true });
            operation.Token.ThrowIfCancellationRequested();
            if (step.Verification.Mode == ModbusCommandVerificationMode.AcknowledgementOnly) continue;
            lock (_gate) UpdateCommandStep(index, _commandSnapshot.Steps[index] with { State = ModbusCommandStepState.Checking });
            if (step.Verification.Mode == ModbusCommandVerificationMode.ReadBack)
            {
                var read = CommandReadSettings(step.UnitId, step.Function, step.Item.Address, prepared.Values.Length);
                var observed = await ExchangeAsync(lease, inbox, read.Request.BuildPacket(), step.TimeoutMs, read, operation.Token).ConfigureAwait(false);
                var verified = observed.Values.AsSpan().SequenceEqual(prepared.Values);
                lock (_gate) UpdateCommandStep(index, _commandSnapshot.Steps[index] with
                {
                    State = verified ? ModbusCommandStepState.Verified : ModbusCommandStepState.NotVerified,
                    ObservedValues = observed.Values,
                    Error = verified ? null : "Write acknowledged, but the command read-back value differs."
                });
                if (!verified) throw new ModbusExchangeException("Write acknowledged, but the command read-back value differs.", false);
            }
            else await CheckCommandStatusAsync(step, index, lease, inbox, operation.Token).ConfigureAwait(false);
        }
    }

    private async Task CheckCommandStatusAsync(ModbusCommandStep step, int index,
        ISerialExclusiveSession lease, ModbusReceiveInbox inbox, CancellationToken stop)
    {
        var verification = step.Verification;
        var started = Stopwatch.GetTimestamp();
        var read = CommandReadSettings(step.UnitId, verification.Function, verification.Address, 1);
        while (true)
        {
            stop.ThrowIfCancellationRequested();
            var remaining = verification.TimeoutMs - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (remaining <= 0)
            {
                const string error = "Write acknowledged, but the configured status condition was not met before the deadline.";
                lock (_gate) UpdateCommandStep(index, _commandSnapshot.Steps[index] with
                { State = ModbusCommandStepState.NotVerified, Error = error });
                throw new ModbusExchangeException(error, false);
            }
            // Do not begin a new request if the remaining budget cannot even fit
            // its required quiet gap. An earlier valid sample still times out normally.
            var gap = Math.Max(5, (int)Math.Ceiling(3.5 * 12 * 1000 / Math.Max(1, _serial.AppliedBaudRate)));
            if (remaining <= gap)
            {
                await Task.Delay((int)Math.Ceiling(remaining), stop).ConfigureAwait(false);
                continue;
            }
            // An already-started exchange drains within its own bounded timeout.
            var timeout = Math.Min(step.TimeoutMs, (int)Math.Ceiling(remaining));
            var observed = await ExchangeAsync(lease, inbox, read.Request.BuildPacket(), timeout, read, stop).ConfigureAwait(false);
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > verification.TimeoutMs)
            {
                const string error = "The status response arrived after the configured result deadline.";
                lock (_gate) UpdateCommandStep(index, _commandSnapshot.Steps[index] with
                { State = ModbusCommandStepState.NotVerified, ObservedValues = observed.Values, Error = error });
                throw new ModbusExchangeException(error, false);
            }
            var matches = (observed.Values[0] & verification.Mask) == verification.ExpectedValue;
            lock (_gate) UpdateCommandStep(index, _commandSnapshot.Steps[index] with
            { State = matches ? ModbusCommandStepState.Verified : ModbusCommandStepState.Checking, ObservedValues = observed.Values });
            if (matches) return;
            remaining = verification.TimeoutMs - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (remaining > 0)
                await Task.Delay((int)Math.Ceiling(Math.Min(verification.IntervalMs, remaining)), stop).ConfigureAwait(false);
        }
    }

    private static ModbusMonitorSettings CommandReadSettings(int unitId, byte function, int address, int quantity) =>
        new() { UnitId = unitId, Function = function, Address = address, Quantity = quantity };

    private static ModbusCommandStepSnapshot CreateCommandStepSnapshot(PreparedCommandStep prepared, int number)
    {
        var step = prepared.Definition;
        var readback = step.Verification.Mode == ModbusCommandVerificationMode.ReadBack;
        var noCheck = step.Verification.Mode == ModbusCommandVerificationMode.AcknowledgementOnly;
        return new(number, step.Name, step.UnitId, prepared.Packet[1], step.Item.Address,
            noCheck ? null : readback ? step.Function : step.Verification.Function,
            noCheck ? null : readback ? step.Item.Address : step.Verification.Address,
            ModbusCommandStepState.Pending, false, null, null);
    }

    private void UpdateCommandStep(int index, ModbusCommandStepSnapshot step)
    {
        var steps = _commandSnapshot.Steps.ToArray();
        steps[index] = step;
        _commandSnapshot = _commandSnapshot with
        {
            Revision = _commandSnapshot.Revision + 1, State = ModbusCommandState.Running,
            CurrentStep = index + 1, Steps = Array.AsReadOnly(steps)
        };
    }

    private void InvalidateCommandDevice(int unitId)
    {
        if (_settings?.UnitId == unitId && _snapshot.ValuesCurrent)
            SetSnapshot(_snapshot with { ValuesCurrent = false });
        var changed = false;
        foreach (var group in _groupOrder)
            if (group.Definition.Settings.UnitId == unitId && group.Snapshot.ValuesCurrent)
            {
                SetGroupSnapshot(group, group.Snapshot with { ValuesCurrent = false });
                changed = true;
            }
        if (changed) PublishPollingSnapshot();
    }

    private void FinishCommand(PendingCommand command, Exception? error, bool canceled)
    {
        var steps = _commandSnapshot.Steps.ToArray();
        if (error is not null || canceled)
        {
            for (var index = 0; index < steps.Length; index++)
            {
                var step = steps[index];
                if (step.State == ModbusCommandStepState.Pending)
                    steps[index] = step with { State = ModbusCommandStepState.Canceled };
                else if (step.State is ModbusCommandStepState.Writing or ModbusCommandStepState.Checking ||
                    (canceled && step.StepNumber == _commandSnapshot.CurrentStep && step.State == ModbusCommandStepState.Acknowledged))
                    steps[index] = step with
                    {
                        State = canceled ? step.WriteAcknowledged ? ModbusCommandStepState.NotVerified : ModbusCommandStepState.Canceled
                            : ModbusCommandStepState.Failed,
                        Error = error?.Message ?? "Command canceled; an acknowledged write may already have been applied."
                    };
            }
        }
        var state = canceled ? ModbusCommandState.Canceled
            : error is not null ? steps.Any(step => step.State == ModbusCommandStepState.NotVerified)
                ? ModbusCommandState.NotVerified : ModbusCommandState.Failed
            : steps.All(step => step.State == ModbusCommandStepState.Verified)
                ? ModbusCommandState.Verified : ModbusCommandState.Acknowledged;
        _commandInProgress = false;
        _commandSnapshot = _commandSnapshot with
        {
            Revision = _commandSnapshot.Revision + 1, State = state, IsBusy = false,
            Steps = Array.AsReadOnly(steps), Error = error?.Message ?? (canceled ? "Command canceled." : null)
        };
        if (error is not null) command.Completion.TrySetException(error);
        else if (canceled) command.Completion.TrySetCanceled(command.Token.IsCancellationRequested ? command.Token : new CancellationToken(true));
        else command.Completion.TrySetResult();
    }

    private void FinishOwnedCommands(PendingCommand? active, Exception? error, bool canceled)
    {
        var command = active ?? _pendingCommand;
        _pendingCommand = null;
        if (command is not null) FinishCommand(command, error, canceled || error is null);
    }

    private sealed record PreparedCommandStep(ModbusCommandStep Definition, ushort[] Values, byte[] Packet);
    private sealed record PendingCommand(ModbusCommandDefinition Definition, PreparedCommandStep[] Steps, long Generation, CancellationToken Token)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
