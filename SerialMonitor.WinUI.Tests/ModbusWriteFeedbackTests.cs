using Microsoft.UI.Xaml;
using SerialMonitor.Core.Modbus;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusWriteFeedbackTests
{
    [Fact]
    public async Task AnEarlierCommandResultNeverBecomesDirectWriteFeedback()
    {
        var service = new FeedbackMonitor();
        var model = Model(service);
        model.ApplySavedCommandSettings(Commands());
        await model.ExecuteSelectedCommandAsync();
        Assert.Contains("All configured result checks passed", model.CommandStatus);
        Assert.Contains("All configured result checks passed", model.Status);
        AssertEmptyFeedback(model);

        model.WriteValuesText = "invalid";
        await model.WriteAsync();

        Assert.Equal(0, service.WriteCount);
        Assert.NotEmpty(model.WriteError);
        Assert.DoesNotContain("All configured", model.WriteError);
        Assert.Equal(Visibility.Visible, model.WriteErrorVisibility);
        Assert.Equal("", model.WriteStatus);
    }

    [Fact]
    public async Task DirectWriteTracksOnlyItsOwnPhasesAndSurvivesPollingAndNewCommands()
    {
        var service = new FeedbackMonitor();
        var model = Model(service);
        var polling = model.RunAsync(true);
        model.WriteValuesText = "7";
        var writing = model.WriteAsync();
        Assert.Equal("Write queued", model.WriteStatus);
        Assert.Equal("", model.WriteError);

        service.SetWriteState(ModbusWriteState.Writing);
        model.Refresh();
        Assert.Equal("Writing", model.WriteStatus);
        service.CompleteWrite(ModbusWriteState.Verified);
        await writing;
        Assert.Equal("Write verified", model.WriteStatus);
        Assert.True(model.IsBusy);
        Assert.DoesNotContain("Polling", model.WriteStatus);

        await model.ExecuteSelectedCommandAsync();
        Assert.Equal("Write verified", model.WriteStatus);
        service.SetWriteState(ModbusWriteState.Failed, "a later polling error");
        model.Refresh();
        Assert.Equal("Write verified", model.WriteStatus);
        Assert.Equal("", model.WriteError);
        await model.StopAsync();
        await polling;
    }

    [Theory]
    [InlineData(ModbusWriteState.Verified, false, false, "Write verified")]
    [InlineData(ModbusWriteState.NotVerified, true, false, "Write not verified")]
    [InlineData(ModbusWriteState.Failed, true, false, "Write failed")]
    [InlineData(ModbusWriteState.None, false, true, "Write stopped")]
    public async Task TerminalFeedbackDistinguishesVerificationFailureAndCancellation(
        ModbusWriteState state, bool failure, bool canceled, string expected)
    {
        var service = new FeedbackMonitor();
        var model = Model(service);
        model.WriteValuesText = "7";
        var writing = model.WriteAsync();
        service.CompleteWrite(state, failure ? new IOException("direct write failure") : null, canceled);
        await writing;
        Assert.StartsWith(expected, model.WriteStatus);
        Assert.Equal(failure ? "direct write failure" : "", model.WriteError);
        Assert.Equal(Visibility.Visible, model.WriteStatusVisibility);
        service.SetWriteState(ModbusWriteState.Verified);
        model.Refresh();
        Assert.StartsWith(expected, model.WriteStatus);
    }

    [Fact]
    public async Task GroupWriteUsesItsGroupSnapshotAndKeepsItsResultWhilePollingContinues()
    {
        var service = new FeedbackMonitor();
        var model = Model(service);
        model.ApplySavedPollingSettings(new()
        {
            SelectedGroupId = "drive", Groups = [new() { Id = "drive", Name = "Drive", Settings = new() }]
        });
        var polling = model.RunAsync(true);
        model.Refresh();
        model.WriteValuesText = "7";
        var writing = model.WriteAsync();
        Assert.Equal("drive", service.LastWriteGroupId);
        Assert.Equal("Write queued", model.WriteStatus);
        service.CompleteWrite(ModbusWriteState.Verified);
        await writing;
        model.Refresh();
        Assert.True(model.IsBusy);
        Assert.Equal("Write verified", model.WriteStatus);
        await model.StopAsync();
        await polling;
        Assert.Equal("Write verified", model.WriteStatus);
    }

    [Fact]
    public async Task AdmissionFailureAndAnUnchangedOldSnapshotCannotReuseEarlierVerification()
    {
        var service = new FeedbackMonitor();
        service.SetWriteState(ModbusWriteState.Verified);
        var model = Model(service);
        model.WriteValuesText = "7";
        service.RejectWrite = true;
        await model.WriteAsync();
        Assert.StartsWith("Write failed", model.WriteStatus);
        Assert.Equal("write rejected", model.WriteError);

        service.RejectWrite = false;
        service.PublishQueued = false;
        var writing = model.WriteAsync();
        Assert.Equal("Write queued", model.WriteStatus);
        Assert.Equal("", model.WriteError);
        service.CompleteWriteWithoutSnapshot();
        await writing;
        Assert.StartsWith("Write not verified", model.WriteStatus);
    }

    [Theory]
    [InlineData("address")]
    [InlineData("value")]
    [InlineData("row")]
    [InlineData("settings")]
    [InlineData("mode")]
    [InlineData("group")]
    public async Task ChangingTheWriteContextClearsBothSuccessfulAndFailedFeedback(string change)
    {
        var service = new FeedbackMonitor();
        var model = Model(service);
        model.WriteValuesText = "7";
        var writing = model.WriteAsync();
        service.CompleteWrite(ModbusWriteState.Failed, new IOException("old target failure"));
        await writing;
        Assert.NotEmpty(model.WriteStatus);
        Assert.NotEmpty(model.WriteError);
        switch (change)
        {
            case "address": model.WriteAddressText = "1"; break;
            case "value": model.WriteValuesText = "8"; break;
            case "row": model.SelectedRow = model.Rows[1]; break;
            case "settings": model.UnitText = "2"; break;
            case "mode": model.WriteModeIndex = 1; break;
            case "group": model.ApplySavedPollingSettings(new() { SelectedGroupId = null }); break;
        }
        AssertEmptyFeedback(model);
    }

    [Fact]
    public async Task SelectingAnotherRowDuringAWriteDoesNotAttachItsResultToTheNewRow()
    {
        var service = new FeedbackMonitor();
        var model = Model(service);
        model.WriteValuesText = "7";
        var writing = model.WriteAsync();
        model.SelectedRow = model.Rows[1];
        service.CompleteWrite(ModbusWriteState.Verified);
        await writing;
        Assert.Equal("1", model.WriteAddressText);
        AssertEmptyFeedback(model);
    }

    private static ModbusRtuViewModel Model(FeedbackMonitor service)
    {
        var model = new ModbusRtuViewModel(service, () => true, () => Task.CompletedTask, () => { });
        model.ApplySavedCommandSettings(Commands());
        return model;
    }

    private static ModbusCommandSettings Commands() => new()
    {
        SelectedCommandId = "run", Commands = [new() { Id = "run", Name = "Other command", Steps = [new()
        {
            Name = "Other target", UnitId = 2, Function = 3, Value = "1",
            Item = new() { Address = 300, Access = ModbusItemAccess.ReadWrite }
        }] }]
    };

    private static void AssertEmptyFeedback(ModbusRtuViewModel model)
    {
        Assert.Equal("", model.WriteStatus);
        Assert.Equal("", model.WriteError);
        Assert.Equal(Visibility.Collapsed, model.WriteStatusVisibility);
        Assert.Equal(Visibility.Collapsed, model.WriteErrorVisibility);
    }

    private sealed class FeedbackMonitor : IModbusRtuGroupMonitorService, IModbusRtuCommandService
    {
        private ModbusMonitorSnapshot _monitor = new(0, ModbusMonitorState.Idle, false, false, null, null, 0, 0, null, null, false);
        private ModbusCommandSnapshot _command = new(0, null, null, ModbusCommandState.Idle, false, 0, [], null);
        private IReadOnlyList<ModbusGroupSnapshot> _groups = [];
        private TaskCompletionSource? _write, _poll;
        public bool RejectWrite, PublishQueued = true;
        public int WriteCount;
        public string? LastWriteGroupId;
        public bool IsBusy => _monitor.IsBusy;
        public ModbusMonitorSnapshot GetSnapshot() => _monitor;
        public ModbusCommandSnapshot GetCommandSnapshot() => _command;
        public ModbusPollingSnapshot GetPollingSnapshot() => new(_monitor.Revision, _monitor.IsBusy, _monitor.IsPolling, _groups, null, null);
        public Task ExecuteCommandAsync(ModbusCommandDefinition command, CancellationToken cancellationToken)
        {
            _command = new(_command.Revision + 1, command.Id, command.Name, ModbusCommandState.Verified, false, 1,
                [new(1, "Other target", 2, 6, 300, 3, 300, ModbusCommandStepState.Verified, true, [1], null)], null);
            return Task.CompletedTask;
        }
        public Task RunAsync(ModbusMonitorSettings settings, bool repeat, CancellationToken cancellationToken)
        {
            _monitor = _monitor with { Revision = _monitor.Revision + 1, IsBusy = repeat, IsPolling = repeat,
                State = repeat ? ModbusMonitorState.Polling : ModbusMonitorState.Stopped,
                Values = new ushort[settings.Quantity], ValuesCurrent = true, WriteState = ModbusWriteState.None };
            _poll = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return repeat ? _poll.Task : Task.CompletedTask;
        }
        public Task RunGroupsAsync(ModbusPollingSettings settings, CancellationToken cancellationToken)
        {
            var running = RunAsync(settings.Groups[0].Settings, true, cancellationToken);
            _groups = settings.Groups.Select(group => new ModbusGroupSnapshot(group.Id, _monitor)).ToArray();
            return running;
        }
        public Task WriteGroupAsync(string groupId, int address, IReadOnlyList<ushort> values, CancellationToken cancellationToken)
        {
            LastWriteGroupId = groupId;
            return WriteAsync(new(), address, values, cancellationToken);
        }
        public Task WriteAsync(ModbusMonitorSettings settings, int address, IReadOnlyList<ushort> values, CancellationToken cancellationToken)
        {
            if (RejectWrite) throw new InvalidOperationException("write rejected");
            WriteCount++;
            _write = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (PublishQueued) SetWriteState(ModbusWriteState.Queued);
            return _write.Task;
        }
        public void SetWriteState(ModbusWriteState state, string? error = null)
        {
            _monitor = _monitor with { Revision = _monitor.Revision + 1, WriteState = state, Error = error };
            _groups = _groups.Select(group => group with { Snapshot = _monitor }).ToArray();
        }
        public void CompleteWrite(ModbusWriteState state, Exception? error = null, bool canceled = false)
        {
            SetWriteState(state, error?.Message);
            if (canceled) _write!.TrySetCanceled();
            else if (error is not null) _write!.TrySetException(error);
            else _write!.TrySetResult();
        }
        public void CompleteWriteWithoutSnapshot() => _write!.TrySetResult();
        public Task StopAsync()
        {
            _monitor = _monitor with { Revision = _monitor.Revision + 1, IsBusy = false, IsPolling = false, State = ModbusMonitorState.Stopped };
            _groups = _groups.Select(group => group with { Snapshot = _monitor }).ToArray();
            _poll?.TrySetResult();
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
