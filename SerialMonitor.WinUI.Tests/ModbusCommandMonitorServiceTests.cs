using System.Buffers.Binary;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed partial class ModbusRtuMonitorServiceTests
{
    [Fact]
    public async Task CommandPreflightsEveryLaterStepBeforeAnyWriteReachesTheWire()
    {
        var (serial, device) = Device();
        await using var service = new ModbusRtuMonitorService(serial);
        var command = Command();
        command.Steps.Add(CommandStep());
        command.Steps[1].Value = "65536";
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.ExecuteCommandAsync(command, default));
        Assert.Empty(device.Sent);
        Assert.Null(device.Owner);
        Assert.Equal(ModbusCommandState.Idle, service.GetCommandSnapshot().State);
    }

    [Fact]
    public async Task CommandWriteOnlyAddressReportsAcknowledgementWithoutPretendingPhysicalCompletion()
    {
        var (serial, device) = Device();
        device.OnSend = packet => { device.Emit(WriteAck(packet)); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var command = Command();
        command.Steps[0].UnitId = 7;
        await service.ExecuteCommandAsync(command, default);
        Assert.Single(device.Sent);
        var result = service.GetCommandSnapshot();
        Assert.Equal(ModbusCommandState.Acknowledged, result.State);
        Assert.False(result.IsBusy);
        Assert.False(service.IsBusy);
        var step = Assert.Single(result.Steps);
        Assert.True(step.WriteAcknowledged);
        Assert.Equal(ModbusCommandStepState.Acknowledged, step.State);
        Assert.Equal(7, step.UnitId);
        Assert.Equal((byte)6, step.WriteFunction);
        Assert.Equal(900, step.WriteAddress);
        Assert.Null(step.VerificationAddress);
        Assert.Null(step.ObservedValues);
    }

    [Fact]
    public async Task CommandFloatReadbackWritesBothWordsThenChecksTheExplicitSameAddress()
    {
        var (serial, device) = Device();
        var command = Command(ModbusCommandVerificationMode.ReadBack);
        command.Steps[0].Item.ValueType = ModbusValueType.Float32;
        command.Steps[0].Value = "1.5";
        device.OnSend = packet => { device.Emit(packet[1] == 3 ? ReadReply(packet, [0x3FC0, 0]) : WriteAck(packet)); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        await service.ExecuteCommandAsync(command, default);
        Assert.Equal(new byte[] { 16, 3 }, device.Sent.Select(packet => packet[1]));
        Assert.All(device.Sent, packet => Assert.Equal(900, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2))));
        Assert.Equal(ModbusCommandState.Verified, service.GetCommandSnapshot().State);
        Assert.Equal(new ushort[] { 0x3FC0, 0 }, Assert.Single(service.GetCommandSnapshot().Steps).ObservedValues);
    }

    [Fact]
    public async Task CommandAutoResetWriteCanVerifyADifferentInputAddressAndBitMask()
    {
        var (serial, device) = Device();
        var command = Command(ModbusCommandVerificationMode.Status);
        var definition = command.Steps[0];
        definition.UnitId = 9;
        definition.Verification.Function = 4;
        definition.Verification.Address = 100;
        definition.Verification.Mask = 4;
        definition.Verification.ExpectedValue = 4;
        var reads = 0;
        device.OnSend = packet =>
        {
            device.Emit(packet[1] == 6 ? WriteAck(packet) : ReadReply(packet, [++reads == 1 ? (ushort)0x20 : (ushort)0xA4]));
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        await service.ExecuteCommandAsync(command, default);
        Assert.Equal(new byte[] { 6, 4, 4 }, device.Sent.Select(packet => packet[1]));
        Assert.All(device.Sent, packet => Assert.Equal((byte)9, packet[0]));
        var result = Assert.Single(service.GetCommandSnapshot().Steps);
        Assert.True(result.WriteAcknowledged);
        Assert.Equal(900, result.WriteAddress);
        Assert.Equal(100, result.VerificationAddress);
        Assert.Equal(new ushort[] { 0xA4 }, result.ObservedValues);
        Assert.Equal(ModbusCommandStepState.Verified, result.State);
    }

    [Fact]
    public async Task CommandMinimumStatusDeadlineStillPerformsAnImmediateFirstRead()
    {
        var (serial, device) = Device();
        var command = Command(ModbusCommandVerificationMode.Status);
        command.Steps[0].Verification.TimeoutMs = 100;
        device.OnSend = packet => { device.Emit(packet[1] == 6 ? WriteAck(packet) : ReadReply(packet, [1])); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        await service.ExecuteCommandAsync(command, default);
        Assert.Equal(2, device.Sent.Count);
        Assert.Equal(ModbusCommandState.Verified, service.GetCommandSnapshot().State);
    }

    [Fact]
    public async Task CommandDrainsLateMatchingStatusButDoesNotExtendItsSuccessDeadline()
    {
        var (serial, device) = Device();
        device.BaudRate = 600; // Quiet gap70ms plus reply50ms exceeds overall100ms, not the reply timeout.
        var command = Command(ModbusCommandVerificationMode.Status);
        command.Steps[0].Verification.TimeoutMs = 100;
        command.Steps.Add(CommandStep());
        device.OnSend = async packet =>
        {
            if (packet[1] == 6) device.Emit(WriteAck(packet));
            else { await Task.Delay(50); device.Emit(ReadReply(packet, [1])); }
        };
        await using var service = new ModbusRtuMonitorService(serial);
        await Assert.ThrowsAsync<ModbusExchangeException>(() => service.ExecuteCommandAsync(command, default));
        Assert.True(device.Connected);
        Assert.Equal(ModbusCommandState.NotVerified, service.GetCommandSnapshot().State);
        Assert.Equal(new ushort[] { 1 }, service.GetCommandSnapshot().Steps[0].ObservedValues);
        Assert.Equal(2, device.Sent.Count);
        Assert.Equal(ModbusCommandStepState.Canceled, service.GetCommandSnapshot().Steps[1].State);
    }

    [Fact]
    public async Task CommandValidStatusNeverMatchingStopsSequenceWithoutRetryOrDisconnect()
    {
        var (serial, device) = Device();
        var command = Command(ModbusCommandVerificationMode.Status);
        command.Steps[0].Verification.TimeoutMs = 200;
        command.Steps.Add(CommandStep());
        device.OnSend = packet => { device.Emit(packet[1] == 6 ? WriteAck(packet) : ReadReply(packet, [0])); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        await Assert.ThrowsAsync<ModbusExchangeException>(() => service.ExecuteCommandAsync(command, default));
        Assert.True(device.Connected);
        Assert.Null(device.Owner);
        Assert.Equal(1, device.Sent.Count(packet => packet[1] == 6));
        Assert.Equal(ModbusCommandState.NotVerified, service.GetCommandSnapshot().State);
        Assert.True(service.GetCommandSnapshot().Steps[0].WriteAcknowledged);
        Assert.Equal(ModbusCommandStepState.Canceled, service.GetCommandSnapshot().Steps[1].State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandMissingOrCorruptResponseRetiresConnectionBeforeReleasingOwner(bool crc)
    {
        var (serial, device) = Device();
        var command = Command(ModbusCommandVerificationMode.Status);
        command.Steps[0].TimeoutMs = 100;
        device.OnSend = packet =>
        {
            if (packet[1] == 6) device.Emit(WriteAck(packet));
            else if (crc)
            {
                var reply = ReadReply(packet, [1]);
                reply[^1] ^= 1;
                device.Emit(reply);
            }
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        await Assert.ThrowsAsync<ModbusExchangeException>(() => service.ExecuteCommandAsync(command, default));
        Assert.False(device.Connected);
        Assert.True(device.WasOwnedWhenDisconnected);
        Assert.Null(device.Owner);
        Assert.True(Assert.Single(service.GetCommandSnapshot().Steps).WriteAcknowledged);
        Assert.Equal(ModbusCommandState.Failed, service.GetCommandSnapshot().State);
        Assert.Equal(2, device.Sent.Count);
    }

    [Fact]
    public async Task CommandSequenceIsAtomicDuringGroupsAndKeepsCapturedTargetsAndValues()
    {
        var (serial, device) = Device();
        var sent = Signal();
        byte[]? request = null;
        device.OnSend = packet => { request = packet; sent.TrySetResult(); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var polling = service.RunGroupsAsync(Groups(), default);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var command = Command();
        command.Steps[0].UnitId = 9;
        command.Steps.Add(CommandStep());
        command.Steps[1].UnitId = 8;
        command.Steps[1].Item.Address = 901;
        var executing = service.ExecuteCommandAsync(command, default);
        command.Steps[0].UnitId = 7;
        command.Steps[0].Value = "999";
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteGroupAsync("a", 10, [1], default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteCommandAsync(Command(), default));
        device.OnSend = packet => { device.Emit(packet[1] == 3 ? ReadReply(packet, [1, 2]) : WriteAck(packet)); return Task.CompletedTask; };
        device.Emit(ReadReply(request!, [1, 2]));
        await executing.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => GroupSnapshot(service, "b").SuccessCount == 1);
        Assert.Equal(new byte[] { 1, 9, 8, 2 }, device.Sent.Take(4).Select(packet => packet[0]));
        Assert.Equal(new byte[] { 3, 6, 6, 3 }, device.Sent.Take(4).Select(packet => packet[1]));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(device.Sent[1].AsSpan(4)));
        await service.StopAsync();
        await polling;
    }

    [Fact]
    public async Task CommandRunsInsideLegacyPollingOwnershipAndKeepsItsOriginalPollDeadline()
    {
        var (serial, device) = Device();
        var sent = Signal();
        byte[]? request = null;
        device.OnSend = packet => { request = packet; sent.TrySetResult(); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var polling = service.RunAsync(GroupSettings(1), true, default);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var executing = service.ExecuteCommandAsync(Command(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteAsync(GroupSettings(1), 10, [1], default));
        device.OnSend = packet => { device.Emit(WriteAck(packet)); return Task.CompletedTask; };
        device.Emit(ReadReply(request!, [1, 2]));
        await executing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.IsBusy);
        Assert.NotNull(device.Owner);
        Assert.Equal(new byte[] { 3, 6 }, device.Sent.Select(packet => packet[1]));
        await service.StopAsync();
        await polling;
    }

    [Fact]
    public async Task KnownCommandFailureLeavesHealthyGroupPollingRunning()
    {
        var (serial, device) = Device();
        var settings = Groups();
        settings.Groups.ForEach(group => group.Settings.IntervalMs = 100);
        device.OnSend = packet =>
        {
            device.Emit(packet[1] == 6 ? WriteAck(packet) : ReadReply(packet,
                BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)) == 100 ? [0] : [1, 2]));
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        var polling = service.RunGroupsAsync(settings, default);
        await Until(() => GroupSnapshot(service, "b").SuccessCount == 1);
        var command = Command(ModbusCommandVerificationMode.Status);
        command.Steps[0].Verification.TimeoutMs = 100;
        await Assert.ThrowsAsync<ModbusExchangeException>(() => service.ExecuteCommandAsync(command, default));
        await Until(() => GroupSnapshot(service, "a").SuccessCount >= 2);
        Assert.True(service.IsBusy);
        Assert.True(device.Connected);
        Assert.All(service.GetPollingSnapshot().Groups, group => Assert.Equal(0, group.Snapshot.ErrorCount));
        await service.StopAsync();
        await polling;
    }

    [Fact]
    public async Task CommandStatusSamplesNeverReplaceMonitoredValuesAndOnlyAffectedDeviceBecomesStale()
    {
        var (serial, device) = Device();
        device.OnSend = packet => { device.Emit(ReadReply(packet, [11, 22])); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var polling = service.RunGroupsAsync(Groups(), default);
        await Until(() => GroupSnapshot(service, "b").SuccessCount == 1);
        var checking = Signal();
        byte[]? statusRequest = null;
        device.OnSend = packet =>
        {
            if (packet[1] == 6) device.Emit(WriteAck(packet));
            else { statusRequest = packet; checking.TrySetResult(); }
            return Task.CompletedTask;
        };
        var command = Command(ModbusCommandVerificationMode.Status);
        command.Steps[0].UnitId = 1;
        command.Steps[0].Verification.Function = 4;
        var executing = service.ExecuteCommandAsync(command, default);
        await checking.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(GroupSnapshot(service, "a").ValuesCurrent);
        Assert.True(GroupSnapshot(service, "b").ValuesCurrent);
        device.Emit(ReadReply(statusRequest!, [1]));
        await executing;
        Assert.Equal(new ushort[] { 11, 22 }, GroupSnapshot(service, "a").Values);
        Assert.Equal(new ushort[] { 1 }, Assert.Single(service.GetCommandSnapshot().Steps).ObservedValues);
        await service.StopAsync();
        await polling;
    }

    [Fact]
    public async Task UnknownCommandCompletionStopsEntireGroupEngineAndRetiresOwnedConnection()
    {
        var (serial, device) = Device();
        device.OnSend = packet => { device.Emit(ReadReply(packet, [1, 2])); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var polling = service.RunGroupsAsync(Groups(), default);
        await Until(() => GroupSnapshot(service, "b").SuccessCount == 1);
        device.OnSend = packet =>
        {
            var response = WriteAck(packet);
            response[^1] ^= 1;
            device.Emit(response);
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<ModbusExchangeException>(() => service.ExecuteCommandAsync(Command(), default));
        await Assert.ThrowsAsync<ModbusExchangeException>(() => polling);
        Assert.False(device.Connected);
        Assert.True(device.WasOwnedWhenDisconnected);
        Assert.Null(device.Owner);
        Assert.All(service.GetPollingSnapshot().Groups, group => Assert.False(group.Snapshot.ValuesCurrent));
        Assert.False(service.GetCommandSnapshot().IsBusy);
    }

    [Fact]
    public async Task CommandCannotBypassActiveReadOnlyOrPartialMultiwordItemsButOutsideAddressIsAllowed()
    {
        var (serial, device) = Device();
        var settings = GroupSettings(1);
        settings.Quantity = 3;
        settings.Items = [new() { Address = 10, Access = ModbusItemAccess.ReadOnly },
            new() { Address = 11, ValueType = ModbusValueType.Int32, Access = ModbusItemAccess.ReadWrite }];
        var sent = Signal();
        byte[]? request = null;
        device.OnSend = packet => { request = packet; sent.TrySetResult(); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var polling = service.RunAsync(settings, true, default);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var command = Command();
        command.Steps[0].UnitId = 1;
        command.Steps[0].Item.Address = 10;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteCommandAsync(command, default));
        command.Steps[0].Item.Address = 12;
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.ExecuteCommandAsync(command, default));
        command.Steps[0].Item.Address = 900;
        var executing = service.ExecuteCommandAsync(command, default);
        device.OnSend = packet => { device.Emit(WriteAck(packet)); return Task.CompletedTask; };
        device.Emit(ReadReply(request!, [1, 2, 3]));
        await executing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, device.Sent.Count);
        await service.StopAsync();
        await polling;
    }

    [Fact]
    public async Task CommandStopDuringAcknowledgementDrainsItAndNeverExecutesNextStep()
    {
        var (serial, device) = Device();
        var sent = Signal();
        byte[]? request = null;
        device.OnSend = packet => { request = packet; sent.TrySetResult(); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var command = Command();
        command.Steps.Add(CommandStep());
        var executing = service.ExecuteCommandAsync(command, default);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = service.StopAsync();
        Assert.False(stopping.IsCompleted);
        Assert.NotNull(device.Owner);
        device.Emit(WriteAck(request!));
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executing);
        Assert.Single(device.Sent);
        Assert.True(service.GetCommandSnapshot().Steps[0].WriteAcknowledged);
        Assert.Equal(ModbusCommandStepState.NotVerified, service.GetCommandSnapshot().Steps[0].State);
        Assert.Equal(ModbusCommandStepState.Canceled, service.GetCommandSnapshot().Steps[1].State);
    }

    [Fact]
    public async Task CanceledQueuedCommandNeverWritesAndDoesNotStopGroupPolling()
    {
        var (serial, device) = Device();
        var sent = Signal();
        byte[]? request = null;
        device.OnSend = packet => { request = packet; sent.TrySetResult(); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var polling = service.RunGroupsAsync(Groups(), default);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var canceled = new CancellationTokenSource();
        var executing = service.ExecuteCommandAsync(Command(), canceled.Token);
        canceled.Cancel();
        device.OnSend = packet => { device.Emit(ReadReply(packet, [1, 2])); return Task.CompletedTask; };
        device.Emit(ReadReply(request!, [1, 2]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executing);
        await Until(() => GroupSnapshot(service, "b").SuccessCount == 1);
        Assert.All(device.Sent, packet => Assert.Equal((byte)3, packet[1]));
        Assert.True(service.IsBusy);
        await service.StopAsync();
        await polling;
    }

    [Fact]
    public async Task CommandCancellationAfterAcknowledgementOnlyCancelsThatCommandDuringPolling()
    {
        var (serial, device) = Device();
        device.OnSend = packet => { device.Emit(ReadReply(packet, [1, 2])); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var polling = service.RunGroupsAsync(Groups(), default);
        await Until(() => GroupSnapshot(service, "b").SuccessCount == 1);
        using var canceled = new CancellationTokenSource();
        device.OnSend = packet =>
        {
            if (packet[1] == 6) { device.Emit(WriteAck(packet)); canceled.Cancel(); }
            else device.Emit(ReadReply(packet, [1, 2]));
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteCommandAsync(Command(ModbusCommandVerificationMode.Status), canceled.Token));
        Assert.True(service.IsBusy);
        Assert.True(device.Connected);
        Assert.True(Assert.Single(service.GetCommandSnapshot().Steps).WriteAcknowledged);
        Assert.DoesNotContain(device.Sent, packet => packet[1] == 3 && BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)) == 100);
        await service.StopAsync();
        await polling;
    }

    [Fact]
    public async Task CommandCancellationDuringStatusReadDrainsCurrentExchangeBeforeReleasingOwnership()
    {
        var (serial, device) = Device();
        var checking = Signal();
        byte[]? request = null;
        device.OnSend = packet =>
        {
            if (packet[1] == 6) device.Emit(WriteAck(packet));
            else { request = packet; checking.TrySetResult(); }
            return Task.CompletedTask;
        };
        await using var service = new ModbusRtuMonitorService(serial);
        using var canceled = new CancellationTokenSource();
        var command = Command(ModbusCommandVerificationMode.Status);
        command.Steps.Add(CommandStep());
        var executing = service.ExecuteCommandAsync(command, canceled.Token);
        await checking.Task.WaitAsync(TimeSpan.FromSeconds(5));
        canceled.Cancel();
        Assert.False(executing.IsCompleted);
        Assert.NotNull(device.Owner);
        device.Emit(ReadReply(request!, [1]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executing);
        Assert.Equal(2, device.Sent.Count);
        Assert.Null(device.Owner);
        Assert.True(device.Connected);
        Assert.Equal(ModbusCommandStepState.Canceled, service.GetCommandSnapshot().Steps[1].State);
    }

    [Fact]
    public async Task CommandQueuedOnOriginalSessionNeverTransmitsOnReplacementConnection()
    {
        var (serial, device) = Device();
        var sent = Signal();
        device.OnSend = _ => { sent.TrySetResult(); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var polling = service.RunGroupsAsync(Groups(), default);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var executing = service.ExecuteCommandAsync(Command(), default);
        device.Disconnect();
        device.Reconnect();
        await Assert.ThrowsAnyAsync<Exception>(() => polling);
        await Assert.ThrowsAnyAsync<Exception>(() => executing);
        Assert.Single(device.Sent);
        Assert.True(device.Connected);
        Assert.All(service.GetCommandSnapshot().Steps, step => Assert.False(step.WriteAcknowledged));
    }

    [Fact]
    public async Task CommandCompletionPublishesStateAndReleasesAdmissionBeforeImmediateNextCommand()
    {
        var (serial, device) = Device();
        device.OnSend = packet => { device.Emit(WriteAck(packet)); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        for (var index = 0; index < 5; index++)
        {
            await service.ExecuteCommandAsync(Command(), default);
            Assert.False(service.IsBusy);
            Assert.False(service.GetCommandSnapshot().IsBusy);
            Assert.Equal(ModbusCommandState.Acknowledged, service.GetCommandSnapshot().State);
        }
        Assert.Equal(5, device.Sent.Count);
    }

    [Fact]
    public async Task QueuedWriteAndCommandShareOneMutationSlotAndStopCancelsUnsentCommand()
    {
        var (serial, device) = Device();
        var sent = Signal();
        byte[]? request = null;
        device.OnSend = packet => { request = packet; sent.TrySetResult(); return Task.CompletedTask; };
        await using var service = new ModbusRtuMonitorService(serial);
        var polling = service.RunGroupsAsync(Groups(), default);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var write = service.WriteGroupAsync("b", 10, [7], default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteCommandAsync(Command(), default));
        var stopping = service.StopAsync();
        device.Emit(ReadReply(request!, [1, 2]));
        await stopping;
        await polling;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Equal(ModbusCommandState.Idle, service.GetCommandSnapshot().State);
        Assert.Single(device.Sent);
    }

    private static ModbusCommandDefinition Command(ModbusCommandVerificationMode mode = ModbusCommandVerificationMode.AcknowledgementOnly) =>
        new() { Id = "command", Name = "Drive command", Steps = [CommandStep(mode)] };

    private static ModbusCommandStep CommandStep(ModbusCommandVerificationMode mode = ModbusCommandVerificationMode.AcknowledgementOnly) => new()
    {
        Name = "Start", UnitId = 9, Function = 3,
        Item = new() { Address = 900, Access = ModbusItemAccess.ReadWrite }, Value = "1", TimeoutMs = 1000,
        Verification = new() { Mode = mode, Function = 3, Address = 100, Mask = 1, ExpectedValue = 1, TimeoutMs = 1000, IntervalMs = 100 }
    };
}
