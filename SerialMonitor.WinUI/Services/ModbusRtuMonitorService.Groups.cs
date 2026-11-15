using System.Diagnostics;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

public sealed partial class ModbusRtuMonitorService
{
    private ModbusPollingSnapshot _pollingSnapshot = new(0, false, false, [], null, null);
    private PollingGroupRun[] _groupOrder = [];
    private bool _groupsRunning, _groupWriteInProgress;
    private long _groupGeneration;
    private PendingGroupWrite? _pendingGroupWrite;

    public ModbusPollingSnapshot GetPollingSnapshot()
    {
        lock (_gate)
        {
            if (!_serial.IsConnected || _serial.ReceiveSessionGeneration != _groupGeneration)
            {
                var changed = false;
                foreach (var group in _groupOrder)
                {
                    if (!group.Snapshot.ValuesCurrent) continue;
                    SetGroupSnapshot(group, group.Snapshot with
                    {
                        ValuesCurrent = false,
                        State = _groupsRunning ? group.Snapshot.State : ModbusMonitorState.Disconnected
                    });
                    changed = true;
                }
                if (changed) PublishPollingSnapshot();
            }
            return _pollingSnapshot;
        }
    }

    public Task RunGroupsAsync(ModbusPollingSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Clone();
        settings.Validate(requireEnabled: true);
        cancellationToken.ThrowIfCancellationRequested();
        var generation = _serial.ReceiveSessionGeneration;
        if (!_serial.IsConnected || _serial.ReceiveSessionGeneration != generation)
            throw new InvalidOperationException("The original serial connection changed before Modbus could start.");
        CancellationTokenSource stop;
        TaskCompletionSource completion;
        PollingGroupRun[] groups;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_snapshot.IsBusy) throw new InvalidOperationException("A Modbus operation is already active.");
            if (!_serial.IsConnected || _serial.ReceiveSessionGeneration != generation)
                throw new InvalidOperationException("The original serial connection changed before Modbus could start.");
            groups = settings.Groups.Select(group => new PollingGroupRun(group)).ToArray();
            _groupOrder = groups;
            _groupGeneration = generation;
            _operationGeneration = generation;
            _groupsRunning = true;
            _groupWriteInProgress = false;
            _pendingGroupWrite = null;
            _settings = null;
            _stop = stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _completion = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            SetSnapshot(new(0, ModbusMonitorState.Polling, true, true, null, null, 0, 0, null, null, false));
            _pollingSnapshot = _pollingSnapshot with { ActiveGroupId = null, Error = null };
            PublishPollingSnapshot();
        }
        _ = Task.Run(() => ExecuteGroupsAsync(groups, generation, stop, completion));
        return completion.Task;
    }

    public Task WriteGroupAsync(string groupId, int address, IReadOnlyList<ushort> values, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_groupsRunning || !_snapshot.IsPolling || _stop is null || _stop.IsCancellationRequested)
                throw new InvalidOperationException("Start group polling before writing to a group.");
            if (!_serial.IsConnected || _serial.ReceiveSessionGeneration != _groupGeneration)
                throw new InvalidOperationException("The original Modbus connection changed.");
            var group = _groupOrder.FirstOrDefault(candidate => candidate.Definition.Id == groupId);
            if (group is null || !group.Definition.Enabled || group.Failed || group.ReadFailures > 0)
                throw new InvalidOperationException("The requested polling group is not active.");
            if (_pendingGroupWrite is not null || _groupWriteInProgress)
                throw new InvalidOperationException("A Modbus group write is already active.");
            if (HasCommandMutation()) throw new InvalidOperationException("A Modbus command is already active.");
            var settings = group.Definition.Settings;
            settings.ValidateWrite(address, values.Count);
            // Validate before copying so an oversized caller cannot allocate an unbounded queue entry.
            var copy = values.ToArray();
            var function = settings.Function == 1 ? (copy.Length == 1 ? (byte)5 : (byte)15)
                : copy.Length == 1 ? (byte)6 : (byte)16;
            var packet = copy.Length == 1
                ? ModbusRtuRequestBuilder.WriteSingle(settings.UnitId, function, address, copy[0])
                : ModbusRtuRequestBuilder.WriteMultiple(settings.UnitId, function, address, copy);
            var write = new PendingWrite(address, copy, packet, cancellationToken);
            _pendingGroupWrite = new(group, write);
            SetGroupSnapshot(group, group.Snapshot with { WriteState = ModbusWriteState.Queued });
            PublishPollingSnapshot();
            return write.Completion.Task;
        }
    }

    private async Task ExecuteGroupsAsync(PollingGroupRun[] groups, long generation,
        CancellationTokenSource stop, TaskCompletionSource completion)
    {
        Exception? failure = null;
        ISerialExclusiveSession? lease = null;
        ModbusReceiveInbox? inbox = null;
        PendingGroupWrite? writing = null;
        PendingCommand? command = null;
        PollingGroupRun? activeGroup = null;
        var nextGroup = 0;
        var lastWasWrite = false;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            if (!_serial.IsConnected || _serial.ReceiveSessionGeneration != generation)
                throw new InvalidOperationException("The original serial connection changed before Modbus could start.");
            lease = _serial.AcquireExclusiveSession("Modbus RTU");
            if (lease.Generation != generation)
                throw new InvalidOperationException("The original serial connection changed before Modbus acquired it.");
            inbox = new ModbusReceiveInbox(_serial, generation);
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                inbox.EnsureConnection();
                PollingGroupRun? group;
                lock (_gate)
                {
                    group = lastWasWrite ? SelectDueGroup(groups, ref nextGroup) : null;
                    command = group is null ? TakePendingCommand() : null;
                    writing = group is null && command is null ? _pendingGroupWrite : null;
                    if (writing is not null) _pendingGroupWrite = null;
                    _groupWriteInProgress = writing is not null;
                    group ??= writing?.Group ?? (command is null ? SelectDueGroup(groups, ref nextGroup) : null);
                    activeGroup = group;
                    if (group is not null)
                    {
                        _pollingSnapshot = _pollingSnapshot with { ActiveGroupId = group.Definition.Id };
                        PublishPollingSnapshot();
                    }
                }
                if (command is not null)
                {
                    await ProcessQueuedCommandAsync(command, lease, inbox, stop.Token).ConfigureAwait(false);
                    command = null;
                    lastWasWrite = true;
                }
                else if (writing is not null)
                {
                    var reschedule = false;
                    var canceled = false;
                    Exception? writeFailure = null;
                    try
                    {
                        if (writing.Write.Token.IsCancellationRequested)
                        {
                            canceled = true;
                            lock (_gate) SetGroupSnapshot(writing.Group, writing.Group.Snapshot with { WriteState = ModbusWriteState.None });
                        }
                        else
                        {
                            await PerformGroupWriteAsync(lease, inbox, writing, stop.Token).ConfigureAwait(false);
                            reschedule = true;
                        }
                    }
                    catch (OperationCanceledException) when (writing.Write.Token.IsCancellationRequested && !stop.IsCancellationRequested)
                    {
                        canceled = true;
                        lock (_gate) SetGroupSnapshot(writing.Group, writing.Group.Snapshot with { WriteState = ModbusWriteState.None });
                    }
                    catch (ModbusExchangeException ex) when (!ex.UnknownCompletion)
                    {
                        FailGroup(writing.Group, ex);
                        writeFailure = ex;
                    }
                    if (reschedule) group!.DueAt = NextDue(group.Definition.Settings.IntervalMs);
                    lock (_gate)
                    {
                        _groupWriteInProgress = false;
                        _pollingSnapshot = _pollingSnapshot with { ActiveGroupId = null };
                        PublishPollingSnapshot();
                        if (canceled) writing.Write.Completion.TrySetCanceled(writing.Write.Token);
                        else if (writeFailure is not null) writing.Write.Completion.TrySetException(writeFailure);
                        else writing.Write.Completion.TrySetResult();
                    }
                    writing = null;
                    activeGroup = null;
                    lastWasWrite = true;
                }
                else if (group is not null)
                {
                    try
                    {
                        var settings = group.Definition.Settings;
                        var result = await ExchangeAsync(lease, inbox, settings.Request.BuildPacket(), settings.TimeoutMs,
                            settings, stop.Token, recoverableRead: true).ConfigureAwait(false);
                        PublishGroupValues(group, result.Values, result.ElapsedMs);
                    }
                    catch (ModbusExchangeException ex) when (ex.ReadRecoveryAllowed)
                    {
                        FailGroup(group, ex, retryRead: true);
                        await RecoverReadAsync(lease, inbox, group.Definition.Settings.TimeoutMs, ex, stop.Token).ConfigureAwait(false);
                    }
                    group.DueAt = NextDue(group.ReadFailures == 0 ? group.Definition.Settings.IntervalMs
                        : ReadRetryDelay(group.Definition.Settings.IntervalMs, group.ReadFailures));
                    lock (_gate)
                    {
                        _pollingSnapshot = _pollingSnapshot with { ActiveGroupId = null };
                        PublishPollingSnapshot();
                    }
                    activeGroup = null;
                    lastWasWrite = false;
                }
                else
                {
                    if (!groups.Any(candidate => candidate.Definition.Enabled && !candidate.Failed)) break;
                    // No queued poll tasks: inspect due times again after a bounded, cancellable wait.
                    await Task.Delay(20, stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure = ex;
            if (activeGroup is not null) FailGroup(activeGroup, ex);
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
                var queued = _pendingGroupWrite;
                _pendingGroupWrite = null;
                _groupWriteInProgress = false;
                _groupsRunning = false;
                var allFailed = groups.Where(group => group.Definition.Enabled).All(group => group.Failed);
                var error = failure?.Message ?? (allFailed ? "All Modbus polling groups stopped after errors." : null);
                foreach (var group in groups)
                {
                    var snapshot = group.Snapshot;
                    SetGroupSnapshot(group, snapshot with
                    {
                        IsBusy = false, IsPolling = false, ValuesCurrent = false,
                        State = group.Failed ? ModbusMonitorState.Error
                            : !group.Definition.Enabled ? ModbusMonitorState.Idle
                            : failure is not null ? ModbusMonitorState.Error
                            : !_serial.IsConnected ? ModbusMonitorState.Disconnected : ModbusMonitorState.Stopped,
                        Error = group.Failed || group.ReadFailures > 0 ? snapshot.Error : failure?.Message,
                        ReadRetryDelayMs = 0,
                        WriteState = snapshot.WriteState is ModbusWriteState.Queued or ModbusWriteState.Writing
                            ? failure is null ? ModbusWriteState.None : ModbusWriteState.Failed : snapshot.WriteState
                    });
                }
                SetSnapshot(_snapshot with
                {
                    IsBusy = false, IsPolling = false, ValuesCurrent = false,
                    State = error is not null ? ModbusMonitorState.Error
                        : !_serial.IsConnected ? ModbusMonitorState.Disconnected : ModbusMonitorState.Stopped,
                    Error = error
                });
                _pollingSnapshot = _pollingSnapshot with { ActiveGroupId = null, Error = error };
                PublishPollingSnapshot();
                _stop = null;
                _completion = null;
                FinishOwnedCommands(command, failure, stop.IsCancellationRequested);
                // Continuations may submit another operation: release admission and publish first.
                if (failure is not null) writing?.Write.Completion.TrySetException(failure);
                else writing?.Write.Completion.TrySetCanceled();
                queued?.Write.Completion.TrySetCanceled();
            }
            stop.Dispose();
            if (failure is not null) completion.TrySetException(failure);
            else completion.TrySetResult();
        }
    }

    private async Task PerformGroupWriteAsync(ISerialExclusiveSession lease, ModbusReceiveInbox inbox,
        PendingGroupWrite pending, CancellationToken stop)
    {
        var group = pending.Group;
        var settings = group.Definition.Settings;
        var write = pending.Write;
        lock (_gate)
        {
            SetGroupSnapshot(group, group.Snapshot with { WriteState = ModbusWriteState.Writing });
            PublishPollingSnapshot();
        }
        using (var beforeTransmit = CancellationTokenSource.CreateLinkedTokenSource(stop, write.Token))
            await ExchangeAsync(lease, inbox, write.Packet, settings.TimeoutMs, null, beforeTransmit.Token).ConfigureAwait(false);
        lock (_gate)
        {
            SetGroupSnapshot(group, group.Snapshot with { WriteState = ModbusWriteState.NotVerified, ValuesCurrent = false });
            PublishPollingSnapshot();
        }
        if (stop.IsCancellationRequested)
            return;
        var read = await ExchangeAsync(lease, inbox, settings.Request.BuildPacket(), settings.TimeoutMs, settings, stop).ConfigureAwait(false);
        PublishGroupValues(group, read.Values, read.ElapsedMs);
        var verified = read.Values.AsSpan(write.Address - settings.Address, write.Values.Length).SequenceEqual(write.Values);
        lock (_gate)
        {
            SetGroupSnapshot(group, group.Snapshot with { WriteState = verified ? ModbusWriteState.Verified : ModbusWriteState.NotVerified });
            PublishPollingSnapshot();
        }
        if (!verified) throw new ModbusExchangeException("Write acknowledged, but the read-back value differs.", false);
    }

    private static PollingGroupRun? SelectDueGroup(PollingGroupRun[] groups, ref int next)
    {
        var now = Stopwatch.GetTimestamp();
        for (var offset = 0; offset < groups.Length; offset++)
        {
            var index = (next + offset) % groups.Length;
            var candidate = groups[index];
            if (!candidate.Definition.Enabled || candidate.Failed || candidate.DueAt > now) continue;
            next = (index + 1) % groups.Length;
            return candidate;
        }
        return null;
    }

    private static long NextDue(int intervalMs) => Stopwatch.GetTimestamp() + (long)(intervalMs * (double)Stopwatch.Frequency / 1000);

    private void PublishGroupValues(PollingGroupRun group, ushort[] values, double elapsedMs)
    {
        lock (_gate)
        {
            group.ReadFailures = 0;
            SetGroupSnapshot(group, group.Snapshot with
            {
                Values = values, UpdatedAt = DateTimeOffset.Now, ValuesCurrent = true,
                State = ModbusMonitorState.Polling, IsBusy = true, IsPolling = true, Error = null, ReadRetryDelayMs = 0,
                SuccessCount = group.Snapshot.SuccessCount + 1, ResponseMilliseconds = elapsedMs
            });
            PublishPollingSnapshot();
        }
    }

    private void FailGroup(PollingGroupRun group, Exception error, bool retryRead = false)
    {
        lock (_gate)
        {
            group.Failed = !retryRead;
            if (retryRead) group.ReadFailures = Math.Min(6, group.ReadFailures + 1);
            PendingGroupWrite? rejected = null;
            // A write admitted during this group's outstanding read must not run after that read failed.
            if (_pendingGroupWrite?.Group == group)
            {
                rejected = _pendingGroupWrite;
                _pendingGroupWrite = null;
            }
            SetGroupSnapshot(group, group.Snapshot with
            {
                State = ModbusMonitorState.Error, IsBusy = retryRead, IsPolling = retryRead, ValuesCurrent = false,
                Error = error.Message,
                ReadRetryDelayMs = retryRead ? ReadRetryDelay(group.Definition.Settings.IntervalMs, group.ReadFailures) : 0,
                ErrorCount = group.Snapshot.ErrorCount + 1,
                WriteState = group.Snapshot.WriteState is ModbusWriteState.Queued or ModbusWriteState.Writing
                    ? ModbusWriteState.Failed : group.Snapshot.WriteState
            });
            PublishPollingSnapshot();
            rejected?.Write.Completion.TrySetException(error);
        }
    }

    // Every caller holds _gate. Published snapshots and value arrays are never mutated by the worker.
    private void PublishPollingSnapshot()
    {
        _pollingSnapshot = _pollingSnapshot with
        {
            Revision = _pollingSnapshot.Revision + 1,
            IsBusy = _groupsRunning && _snapshot.IsBusy,
            IsPolling = _groupsRunning && _snapshot.IsPolling,
            Groups = Array.AsReadOnly(_groupOrder.Select(group => new ModbusGroupSnapshot(group.Definition.Id, group.Snapshot)).ToArray())
        };
        SetSnapshot(_snapshot with
        {
            SuccessCount = _groupOrder.Sum(group => group.Snapshot.SuccessCount),
            ErrorCount = _groupOrder.Sum(group => group.Snapshot.ErrorCount)
        });
    }

    private static void SetGroupSnapshot(PollingGroupRun group, ModbusMonitorSnapshot snapshot) =>
        group.Snapshot = snapshot with { Revision = group.Snapshot.Revision + 1 };

    private sealed class PollingGroupRun(ModbusPollingGroup definition)
    {
        public ModbusPollingGroup Definition { get; } = definition;
        public long DueAt { get; set; }
        public bool Failed { get; set; }
        public int ReadFailures { get; set; }
        public ModbusMonitorSnapshot Snapshot { get; set; } = new(0,
            definition.Enabled ? ModbusMonitorState.Polling : ModbusMonitorState.Idle,
            definition.Enabled, definition.Enabled, null, null, 0, 0, null, null, false);
    }

    private sealed record PendingGroupWrite(PollingGroupRun Group, PendingWrite Write);
}
