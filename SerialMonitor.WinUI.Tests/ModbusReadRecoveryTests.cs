using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed partial class ModbusRtuMonitorServiceTests
{
    [Theory]
    [InlineData("timeout")]
    [InlineData("crc")]
    [InlineData("exception")]
    [InlineData("partial")]
    public async Task FailedDeviceRecoversWhileHealthyDeviceKeepsPolling(string fault)
    {
        var (serial, device) = Device();
        var settings = Groups();
        foreach (var group in settings.Groups) { group.Settings.IntervalMs = 100; group.Settings.TimeoutMs = 100; }
        var first = true;
        device.OnSend = packet =>
        {
            if (packet[0] == 1 && first)
            {
                first = false;
                if (fault == "exception") device.Emit(Crc([1, 0x83, 6]));
                if (fault == "partial") device.Emit([1, 3, 4, 0]);
                if (fault == "crc") { var reply = ReadReply(packet, [1, 2]); reply[^1] ^= 1; device.Emit(reply); }
            }
            else device.Emit(ReadReply(packet, [(ushort)(packet[0] * 100), 2]));
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(settings, default);
        await Until(() => GroupSnapshot(service, "b").SuccessCount >= 2);
        var failed = GroupSnapshot(service, "a");
        Assert.False(failed.ValuesCurrent);
        Assert.Equal(1000, failed.ReadRetryDelayMs);
        Assert.True(failed.IsPolling);
        Assert.True(device.Connected);
        Assert.Contains("1000", ModbusRtuViewModel.DescribeSnapshotError(failed));
        var row = new ModbusPollingGroupRow(settings.Groups[0]) { Snapshot = failed };
        Assert.True(row.HasError);
        Assert.Contains("1000", row.StatusText);
        await Until(() => GroupSnapshot(service, "a").SuccessCount >= 2);
        var recovered = GroupSnapshot(service, "a");
        Assert.True(recovered.ValuesCurrent);
        Assert.Equal(new ushort[] { 100, 2 }, recovered.Values);
        Assert.Null(recovered.Error);
        Assert.Equal(0, recovered.ReadRetryDelayMs);
        Assert.Equal(1, recovered.ErrorCount);
        Assert.Equal(ModbusMonitorState.Polling, recovered.State);
        row.Snapshot = recovered;
        Assert.False(row.HasError);
        Assert.DoesNotContain("1000", row.StatusText);
        Assert.True(GroupSnapshot(service, "b").SuccessCount > 2);
        await service.StopAsync();
        await run;
        Assert.True(device.Connected);
    }

    [Fact]
    public async Task LateReplyFromFailedDeviceCannotPopulateAnotherDeviceEvenWhenCoalescedOrSplit()
    {
        var (serial, device) = Device();
        var settings = Groups();
        foreach (var group in settings.Groups) { group.Settings.IntervalMs = 100; group.Settings.TimeoutMs = 100; }
        byte[]? old = null;
        var healthyReads = 0;
        device.OnSend = packet =>
        {
            if (packet[0] == 1) old = ReadReply(packet, [999, 999]);
            else
            {
                var late = old!;
                var good = ReadReply(packet, [20, 21]);
                if (++healthyReads % 2 == 0) device.Emit([.. late, .. good]);
                else { device.Emit(late[..2]); device.Emit(late[2..]); device.Emit(good); }
            }
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(settings, default);
        await Until(() => GroupSnapshot(service, "b").SuccessCount >= 4);
        Assert.Equal(new ushort[] { 20, 21 }, GroupSnapshot(service, "b").Values);
        Assert.Equal(0, GroupSnapshot(service, "b").ErrorCount);
        Assert.Null(GroupSnapshot(service, "a").Values);
        await service.StopAsync();
        await run;
    }

    [Fact]
    public async Task RecoveryDrainsLateSameDeviceReplyBeforeReadingAnotherAddress()
    {
        var (serial, device) = Device();
        var settings = Groups();
        settings.Groups[0].Settings.TimeoutMs = 200;
        settings.Groups[1].Settings.UnitId = 1;
        settings.Groups[1].Settings.Address = 30;
        var sent = Signal();
        byte[]? old = null;
        device.OnSend = packet =>
        {
            if (packet[3] == 10) { old = ReadReply(packet, [999, 999]); sent.TrySetResult(); }
            else device.Emit(ReadReply(packet, [30, 31]));
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(settings, default);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => GroupSnapshot(service, "a").ErrorCount == 1);
        Assert.Single(device.Sent);
        device.Emit(old![..3]);
        device.Emit(old[3..]);
        await Until(() => GroupSnapshot(service, "b").SuccessCount == 1);
        Assert.Equal(new ushort[] { 30, 31 }, GroupSnapshot(service, "b").Values);
        await service.StopAsync();
        await run;
    }

    [Fact]
    public async Task SingleRangeRetainsStaleValueThenAutomaticallyReturnsToNormalInterval()
    {
        var (serial, device) = Device();
        var reads = 0;
        device.OnSend = packet =>
        {
            if (Interlocked.Increment(ref reads) != 2) device.Emit(ReadReply(packet, [(ushort)reads, 2]));
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunAsync(Settings(100), true, default);
        await Until(() => service.GetSnapshot().ErrorCount == 1);
        Assert.False(service.GetSnapshot().ValuesCurrent);
        Assert.Equal(new ushort[] { 1, 2 }, service.GetSnapshot().Values);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteAsync(Settings(100), 10, [1], default));
        await Until(() => service.GetSnapshot().SuccessCount >= 3);
        Assert.Null(service.GetSnapshot().Error);
        Assert.Equal(0, service.GetSnapshot().ReadRetryDelayMs);
        Assert.True(service.GetSnapshot().ValuesCurrent);
        await service.StopAsync();
        await run;
        Assert.True(device.Connected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteOrItsReadbackTimeoutNeverEntersReadRecovery(bool acknowledgeWrite)
    {
        var (serial, device) = Device();
        var settings = Groups();
        foreach (var group in settings.Groups) group.Settings.TimeoutMs = 100;
        var writeStarted = false;
        device.OnSend = packet =>
        {
            if (packet[1] == 6)
            {
                writeStarted = true;
                if (acknowledgeWrite) device.Emit(WriteAck(packet));
            }
            else if (!writeStarted) device.Emit(ReadReply(packet, [1, 2]));
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(settings, default);
        await Until(() => GroupSnapshot(service, "b").SuccessCount == 1);
        await Assert.ThrowsAsync<ModbusExchangeException>(() => service.WriteGroupAsync("a", 10, [77], default));
        await Assert.ThrowsAsync<ModbusExchangeException>(() => run);
        Assert.Single(device.Sent.Where(packet => packet[1] == 6));
        Assert.False(device.Connected);
        Assert.True(device.WasOwnedWhenDisconnected);
        Assert.Equal(0, GroupSnapshot(service, "a").ReadRetryDelayMs);
    }

    [Fact]
    public async Task StopDuringRecoveryDisconnectsBeforeReleasingOwnershipAndDoesNotRetry()
    {
        var (serial, device) = Device();
        var settings = Groups();
        settings.Groups[0].Settings.TimeoutMs = 200;
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(settings, default);
        await Until(() => GroupSnapshot(service, "a").ErrorCount == 1);
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await run;
        Assert.Single(device.Sent);
        Assert.False(device.Connected);
        Assert.True(device.WasOwnedWhenDisconnected);
        Assert.Null(device.Owner);
    }

    [Fact]
    public async Task TransportSendFailureRemainsFatalInsteadOfBeingRetriedAsARead()
    {
        var (serial, device) = Device();
        device.OnSend = _ => throw new IOException("USB send failed");
        await using var service = new ModbusRtuMonitorService(serial);
        await Assert.ThrowsAsync<ModbusExchangeException>(() => service.RunGroupsAsync(Groups(), default));
        Assert.Single(device.Sent);
        Assert.False(device.Connected);
    }

    [Theory]
    [InlineData(100, 1, 1000)]
    [InlineData(100, 2, 2000)]
    [InlineData(100, 6, 30000)]
    [InlineData(100, int.MaxValue, 30000)]
    [InlineData(60000, 1, 60000)]
    public void ReadRetryBackoffIsBoundedAndRespectsSlowerUserInterval(int interval, int failures, int expected) =>
        Assert.Equal(expected, ModbusRtuMonitorService.ReadRetryDelay(interval, failures));

    [Fact]
    public async Task AllSilentGroupsKeepRetryingAndRecoverWithoutRestart()
    {
        var (serial, device) = Device();
        var settings = Groups();
        foreach (var group in settings.Groups) { group.Settings.IntervalMs = 100; group.Settings.TimeoutMs = 100; }
        var restored = 0;
        device.OnSend = packet =>
        {
            if (Volatile.Read(ref restored) != 0) device.Emit(ReadReply(packet, [1, 2]));
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(settings, default);
        await Until(() => GroupSnapshot(service, "b").ErrorCount >= 2);
        Assert.True(service.IsBusy);
        Assert.Equal(2000, GroupSnapshot(service, "a").ReadRetryDelayMs);
        Assert.Equal(2000, GroupSnapshot(service, "b").ReadRetryDelayMs);
        Assert.True(device.Connected);
        Interlocked.Exchange(ref restored, 1);
        await Until(() => GroupSnapshot(service, "a").SuccessCount > 0 && GroupSnapshot(service, "b").SuccessCount > 0);
        Assert.All(service.GetPollingSnapshot().Groups, group => Assert.Null(group.Snapshot.Error));
        await service.StopAsync();
        await run;
    }

    [Fact]
    public async Task ReplacementDuringReadRecoveryNeverReceivesAnOldPoll()
    {
        var (serial, device) = Device();
        var settings = Groups();
        settings.Groups[0].Settings.TimeoutMs = 200;
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(settings, default);
        await Until(() => GroupSnapshot(service, "a").ErrorCount == 1);
        device.Disconnect();
        device.Reconnect();
        await Assert.ThrowsAnyAsync<Exception>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(device.Sent);
        Assert.True(device.Connected);
        Assert.False(service.IsBusy);
        Assert.Null(device.Owner);
    }

    [Fact]
    public async Task TimeoutRejectsQueuedWriteButHealthyGroupContinues()
    {
        var (serial, device) = Device();
        var settings = Groups();
        foreach (var group in settings.Groups) group.Settings.TimeoutMs = 200;
        var sent = Signal();
        device.OnSend = packet =>
        {
            if (packet[0] == 1) sent.TrySetResult();
            else device.Emit(ReadReply(packet, [3, 4]));
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        var run = service.RunGroupsAsync(settings, default);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var write = service.WriteGroupAsync("a", 10, [123], default);
        await Assert.ThrowsAsync<ModbusExchangeException>(() => write);
        await Until(() => GroupSnapshot(service, "b").SuccessCount == 1);
        Assert.DoesNotContain(device.Sent, packet => packet[1] != 3);
        Assert.Equal(ModbusWriteState.Failed, GroupSnapshot(service, "a").WriteState);
        await service.StopAsync();
        await run;
        Assert.True(device.Connected);
    }
}
