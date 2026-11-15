using System.Buffers.Binary;
using System.Diagnostics;
using SerialMonitor.Core.Modbus;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed partial class ModbusRtuMonitorServiceTests
{
    [Fact]
    public async Task GroupPollingSeparatesIdenticalAddressesByStableGroupAndDeviceIdentity()
    {
        var (serial, device) = Device();
        var settings = Groups();
        settings.Groups.Add(new() { Id = "disabled", Enabled = false, Settings = GroupSettings(3) });
        device.OnSend = packet => { device.Emit(ReadReply(packet, [(ushort)(packet[0] * 100), 2])); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        Assert.Empty(device.Sent);
        var run = service.RunGroupsAsync(settings, default);
        await Until(() => GroupSnapshot(service, "b").SuccessCount == 1);
        var snapshot = service.GetPollingSnapshot();
        Assert.True(snapshot.IsBusy);
        Assert.True(service.GetSnapshot().IsPolling);
        Assert.Equal(new ushort[] { 100, 2 }, GroupSnapshot(service, "a").Values);
        Assert.Equal(new ushort[] { 200, 2 }, GroupSnapshot(service, "b").Values);
        Assert.Equal(ModbusMonitorState.Idle, GroupSnapshot(service, "disabled").State);
        Assert.Equal(new byte[] { 1, 2 }, device.Sent.Select(packet => packet[0]));
        await service.StopAsync();
        await run;
        Assert.All(service.GetPollingSnapshot().Groups, group => Assert.False(group.Snapshot.ValuesCurrent));
        Assert.True(snapshot.Groups[0].Snapshot.ValuesCurrent); // Earlier publication stays immutable.
    }

    [Fact]
    public async Task GroupPollingGivesEveryDueGroupATurnWithoutCatchupBursts()
    {
        var (serial, device) = Device();
        var settings = Groups();
        settings.Groups.Add(new() { Id = "c", Settings = GroupSettings(3, 100) });
        foreach (var group in settings.Groups) group.Settings.IntervalMs = 100;
        device.OnSend = async packet => { await Task.Delay(150); device.Emit(ReadReply(packet, [1, 2])); };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(settings, default);
        await Until(() => device.Sent.Count >= 6);
        await service.StopAsync();
        await run;
        Assert.Equal(new byte[] { 1, 2, 3, 1, 2, 3 }, device.Sent.Take(6).Select(packet => packet[0]));
    }

    [Fact]
    public async Task GroupIntervalsAreIndependentAndStartAfterResponseCompletion()
    {
        var (serial, device) = Device();
        var settings = Groups();
        settings.Groups[0].Settings.IntervalMs = 100;
        var firstReply = 0L;
        var secondSend = 0L;
        var firstCount = 0;
        device.OnSend = async packet =>
        {
            if (packet[0] == 1)
            {
                var count = Interlocked.Increment(ref firstCount);
                if (count == 2) secondSend = Stopwatch.GetTimestamp();
                await Task.Delay(60);
                device.Emit(ReadReply(packet, [1, 2]));
                if (count == 1) firstReply = Stopwatch.GetTimestamp();
            }
            else device.Emit(ReadReply(packet, [3, 4]));
        };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(settings, default);
        await Until(() => GroupSnapshot(service, "a").SuccessCount >= 3);
        await service.StopAsync();
        await run;
        Assert.True(Stopwatch.GetElapsedTime(firstReply, secondSend).TotalMilliseconds >= 95);
        Assert.Equal(1, GroupSnapshot(service, "b").SuccessCount);
    }

    [Fact]
    public async Task KnownGroupExceptionBacksOffOnlyAffectedGroup()
    {
        var (serial, device) = Device();
        var settings = Groups();
        settings.Groups[1].Settings.IntervalMs = 100;
        device.OnSend = packet =>
        {
            device.Emit(packet[0] == 1 ? Crc([1, 0x83, 2]) : ReadReply(packet, [3, 4]));
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(settings, default);
        await Until(() => GroupSnapshot(service, "b").SuccessCount >= 2);
        Assert.True(device.Connected);
        Assert.True(service.IsBusy);
        Assert.Equal(1, device.Sent.Count(packet => packet[0] == 1));
        Assert.Equal(1, GroupSnapshot(service, "a").ErrorCount);
        Assert.Equal(ModbusMonitorState.Error, GroupSnapshot(service, "a").State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteGroupAsync("a", 10, [1], default));
        await service.StopAsync();
        await run;
    }

    [Fact]
    public async Task AllKnownGroupFailuresKeepPollingUntilStoppedAndRetainIndividualErrors()
    {
        var (serial, device) = Device();
        device.OnSend = packet => { device.Emit(Crc([packet[0], 0x83, 2])); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(Groups(), default);
        await Until(() => GroupSnapshot(service, "b").ErrorCount == 1);
        Assert.True(service.IsBusy);
        Assert.NotNull(device.Owner);
        Assert.All(service.GetPollingSnapshot().Groups, group => Assert.True(group.Snapshot.ReadRetryDelayMs > 0));
        await service.StopAsync();
        await run;
        Assert.True(device.Connected);
        Assert.Null(device.Owner);
        Assert.False(service.IsBusy);
        Assert.All(service.GetPollingSnapshot().Groups, group => Assert.NotNull(group.Snapshot.Error));
        Assert.All(service.GetPollingSnapshot().Groups, group => Assert.Equal(1, group.Snapshot.ErrorCount));
    }

    [Fact]
    public async Task ReceiveOverflowDisconnectsBeforeReleaseAndStopsAllGroups()
    {
        var (serial, device) = Device();
        var settings = Groups();
        settings.Groups[0].Settings.TimeoutMs = 100;
        device.OnSend = packet =>
        {
            device.Emit(new byte[65537]);
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        await Assert.ThrowsAsync<ModbusExchangeException>(() => service.RunGroupsAsync(settings, default));
        Assert.Single(device.Sent);
        Assert.False(device.Connected);
        Assert.True(device.WasOwnedWhenDisconnected);
        Assert.Null(device.Owner);
        Assert.False(service.GetPollingSnapshot().IsBusy);
        Assert.All(service.GetPollingSnapshot().Groups, group => Assert.False(group.Snapshot.IsPolling));
        Assert.Equal(1, GroupSnapshot(service, "a").ErrorCount);
    }

    [Fact]
    public async Task GroupWriteUsesCapturedIdentityValuesAndPermissionsAndNeverInterleavesReadback()
    {
        var (serial, device) = Device();
        var settings = Groups();
        var sent = Signal();
        byte[]? firstRequest = null;
        device.OnSend = packet => { firstRequest = packet; sent.TrySetResult(); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(settings, default);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var values = new ushort[] { 456 };
        var write = service.WriteGroupAsync("b", 10, values, default);
        values[0] = 999;
        settings.Groups[1].Settings.UnitId = 3;
        settings.Groups[1].Settings.Address = 50;
        settings.SelectedGroupId = "a";
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteGroupAsync("a", 10, [7], default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteAsync(GroupSettings(1), 10, [7], default));
        device.OnSend = packet =>
        {
            device.Emit(packet[1] == 3 ? ReadReply(packet, [456, 7]) : WriteAck(packet));
            return Task.CompletedTask;
        };
        device.Emit(ReadReply(firstRequest!, [1, 2]));
        await write.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 1, 2, 2 }, device.Sent.Take(3).Select(packet => packet[0]));
        Assert.Equal(new byte[] { 3, 6, 3 }, device.Sent.Take(3).Select(packet => packet[1]));
        Assert.Equal((ushort)456, BinaryPrimitives.ReadUInt16BigEndian(device.Sent[1].AsSpan(4)));
        Assert.Equal(ModbusWriteState.Verified, GroupSnapshot(service, "b").WriteState);
        await service.StopAsync();
        await run;
    }

    [Fact]
    public async Task CompletedGroupWritePublishesResultAndReleasesWriteAdmissionBeforeContinuations()
    {
        var (serial, device) = Device();
        var values = new Dictionary<byte, ushort> { [1] = 1, [2] = 2 };
        device.OnSend = packet =>
        {
            if (packet[1] == 6)
            {
                values[packet[0]] = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4));
                device.Emit(WriteAck(packet));
            }
            else device.Emit(ReadReply(packet, [values[packet[0]], 7]));
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(Groups(), default);
        await Until(() => GroupSnapshot(service, "b").SuccessCount == 1);
        for (ushort value = 10; value < 20; value++)
        {
            await service.WriteGroupAsync("a", 10, [value], default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ModbusWriteState.Verified, GroupSnapshot(service, "a").WriteState);
            Assert.Equal(value, GroupSnapshot(service, "a").Values![0]);
        }
        await service.StopAsync();
        await run;
        Assert.Equal(10, device.Sent.Count(packet => packet[1] == 6));
    }

    [Fact]
    public async Task GroupStopCancelsQueuedWriteWithoutTransmittingAndRetainsOwnerForOutstandingRead()
    {
        var (serial, device) = Device();
        var sent = Signal();
        byte[]? request = null;
        device.OnSend = packet => { request = packet; sent.TrySetResult(); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(Groups(), default);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var write = service.WriteGroupAsync("b", 10, [123], default);
        var stop = service.StopAsync();
        Assert.False(stop.IsCompleted);
        Assert.True(service.IsBusy);
        Assert.NotNull(device.Owner);
        device.Emit(ReadReply(request!, [1, 2]));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await run;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Single(device.Sent);
        Assert.True(device.Connected);
        Assert.Null(device.Owner);
    }

    [Fact]
    public async Task CanceledQueuedGroupWriteDoesNotStopOtherGroupsOrTransmit()
    {
        var (serial, device) = Device();
        var sent = Signal();
        byte[]? request = null;
        device.OnSend = packet => { request = packet; sent.TrySetResult(); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(Groups(), default);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource();
        var write = service.WriteGroupAsync("b", 10, [123], cancel.Token);
        cancel.Cancel();
        device.OnSend = packet => { device.Emit(ReadReply(packet, [3, 4])); return Task.CompletedTask; };
        device.Emit(ReadReply(request!, [1, 2]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        await Until(() => GroupSnapshot(service, "b").SuccessCount == 1);
        Assert.DoesNotContain(device.Sent, packet => packet[1] != 3);
        Assert.True(service.IsBusy);
        await service.StopAsync();
        await run;
    }

    [Fact]
    public async Task GroupStopAfterWriteAcknowledgementDoesNotPretendReadbackWasVerified()
    {
        var (serial, device) = Device();
        device.OnSend = packet => { device.Emit(ReadReply(packet, [1, 2])); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(Groups(), default);
        await Until(() => GroupSnapshot(service, "b").SuccessCount == 1);
        var writing = Signal();
        byte[]? request = null;
        device.OnSend = packet => { request = packet; writing.TrySetResult(); return Task.CompletedTask; };
        var write = service.WriteGroupAsync("b", 10, [123], default);
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = service.StopAsync();
        Assert.False(stop.IsCompleted);
        Assert.NotNull(device.Owner);
        device.Emit(WriteAck(request!));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await write;
        await run;
        Assert.Equal(ModbusWriteState.NotVerified, GroupSnapshot(service, "b").WriteState);
        Assert.Equal(3, device.Sent.Count);
        Assert.Equal((byte)6, device.Sent[^1][1]);
    }

    [Fact]
    public async Task GroupReadbackMismatchStopsItsGroupWithoutRetryingOrDisconnectingOthers()
    {
        var (serial, device) = Device();
        var settings = Groups();
        settings.Groups[1].Settings.IntervalMs = 100;
        device.OnSend = packet => { device.Emit(packet[1] == 3 ? ReadReply(packet, [1, 2]) : WriteAck(packet)); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(settings, default);
        await Until(() => GroupSnapshot(service, "b").SuccessCount == 1);
        await Assert.ThrowsAsync<ModbusExchangeException>(() => service.WriteGroupAsync("a", 10, [777], default));
        await Until(() => GroupSnapshot(service, "b").SuccessCount >= 2);
        Assert.True(device.Connected);
        Assert.Equal(ModbusWriteState.NotVerified, GroupSnapshot(service, "a").WriteState);
        Assert.Equal(1, device.Sent.Count(packet => packet[1] == 6));
        Assert.Equal(1, GroupSnapshot(service, "a").ErrorCount);
        await service.StopAsync();
        await run;
    }

    [Fact]
    public async Task FailedGroupReadRejectsAlreadyQueuedWriteToThatGroup()
    {
        var (serial, device) = Device();
        var sent = Signal();
        device.OnSend = _ => { sent.TrySetResult(); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(Groups(), default);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var write = service.WriteGroupAsync("a", 10, [123], default);
        device.OnSend = packet => { device.Emit(ReadReply(packet, [3, 4])); return Task.CompletedTask; };
        device.Emit(Crc([1, 0x83, 2]));
        await Assert.ThrowsAsync<ModbusExchangeException>(() => write);
        await Until(() => GroupSnapshot(service, "b").SuccessCount == 1);
        Assert.DoesNotContain(device.Sent, packet => packet[1] != 3);
        await service.StopAsync();
        await run;
    }

    [Fact]
    public async Task GroupWritesCannotWeakenCapturedReadOnlyDefinitionsOrTargetDisabledGroups()
    {
        var (serial, device) = Device();
        var settings = Groups();
        settings.Groups[0].Settings.Items = [new() { Address = 10, Access = ModbusItemAccess.ReadOnly }];
        settings.Groups[1].Enabled = false;
        var sent = Signal();
        byte[]? request = null;
        device.OnSend = packet => { request = packet; sent.TrySetResult(); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(settings, default);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        settings.Groups[0].Settings.Items.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteGroupAsync("a", 10, [123], default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteGroupAsync("b", 10, [123], default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteGroupAsync("unknown", 10, [123], default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(GroupSettings(1), false, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunGroupsAsync(Groups(), default));
        var stop = service.StopAsync();
        device.Emit(ReadReply(request!, [1, 2]));
        await stop;
        await run;
        Assert.Single(device.Sent);
    }

    [Fact]
    public async Task GroupSnapshotInvalidatesEveryValueWhenConnectionGenerationChanges()
    {
        var (serial, device) = Device();
        device.OnSend = packet => { device.Emit(ReadReply(packet, [1, 2])); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(Groups(), default);
        await Until(() => GroupSnapshot(service, "b").SuccessCount == 1);
        device.Disconnect();
        device.Reconnect();
        Assert.All(service.GetPollingSnapshot().Groups, group => Assert.False(group.Snapshot.ValuesCurrent));
        await Assert.ThrowsAnyAsync<Exception>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(device.Connected);
        Assert.False(service.IsBusy);
        Assert.Equal(2, device.Sent.Count);
    }

    [Fact]
    public async Task GroupStartValidationAndPreCanceledRunNeverAcquireOrTransmit()
    {
        var (serial, device) = Device();
        await using var service = new ModbusRtuMonitorService(serial);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RunGroupsAsync(new(), default));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunGroupsAsync(Groups(), canceled.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteGroupAsync("a", 10, [1], default));
        Assert.Empty(device.Sent);
        Assert.Null(device.Owner);
    }

    private static ModbusPollingSettings Groups() => new()
    {
        SelectedGroupId = "a",
        Groups = [new() { Id = "a", Name = "Drive", Settings = GroupSettings(1) },
            new() { Id = "b", Name = "Output", Settings = GroupSettings(2) }]
    };

    private static ModbusMonitorSettings GroupSettings(int unit, int interval = 60000) => new()
    { UnitId = unit, Address = 10, Quantity = 2, IntervalMs = interval, TimeoutMs = 1000 };

    private static ModbusMonitorSnapshot GroupSnapshot(ModbusRtuMonitorService service, string id) =>
        service.GetPollingSnapshot().Groups.Single(group => group.GroupId == id).Snapshot;
}
