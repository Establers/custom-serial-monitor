using Microsoft.UI.Xaml;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusCommandHeaderTests
{
    [Theory]
    [InlineData(ModbusCommandState.Acknowledged, false)]
    [InlineData(ModbusCommandState.Verified, false)]
    [InlineData(ModbusCommandState.NotVerified, false)]
    [InlineData(ModbusCommandState.Failed, false)]
    [InlineData(ModbusCommandState.Canceled, false)]
    [InlineData(ModbusCommandState.Acknowledged, true)]
    [InlineData(ModbusCommandState.Verified, true)]
    [InlineData(ModbusCommandState.NotVerified, true)]
    [InlineData(ModbusCommandState.Failed, true)]
    [InlineData(ModbusCommandState.Canceled, true)]
    public async Task StandaloneCompletionKeepsItsTerminalStatusAndActualTarget(ModbusCommandState terminal, bool readFirst)
    {
        var service = new HeaderMonitor();
        var model = Model(service);
        if (readFirst) await model.RunAsync(false);
        var run = model.ExecuteSelectedCommandAsync();
        model.Refresh();
        Assert.Contains("running", model.Status, StringComparison.OrdinalIgnoreCase);
        service.CompleteCommand(terminal);
        await run;
        for (var index = 0; index < 3; index++) model.Refresh();
        Assert.Equal(model.CommandStatus, model.Status);
        Assert.DoesNotContain("running", model.Status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("queued", model.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("first · 1/1 · ID 2 · FC 06 · 300", model.RequestSummary);
        Assert.False(model.IsBusy);
        Assert.Equal(Visibility.Collapsed, model.BusyVisibility);
        Assert.False(model.StopCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumedPollingRestoresItsOwnStatusAndTargetEvenWithoutANewRead(bool grouped)
    {
        var service = new HeaderMonitor();
        var model = Model(service);
        if (grouped) model.ApplySavedPollingSettings(new()
        {
            SelectedGroupId = "a", Groups = [
                new() { Id = "a", Name = "Input", Settings = new() { UnitId = 1, Quantity = 2 } },
                new() { Id = "b", Name = "Other", Settings = new() { UnitId = 7, Address = 40, Quantity = 2 } }
            ]
        });
        var poll = model.RunAsync(true);
        model.Refresh();
        var monitorTarget = model.RequestSummary;
        if (grouped)
        {
            model.SelectedPollingGroup = model.PollingGroups[1];
            model.Refresh();
            monitorTarget = model.RequestSummary;
            model.SelectedPollingGroup = model.PollingGroups[0];
            model.Refresh();
        }
        var revision = service.GetSnapshot().Revision;
        var run = model.ExecuteSelectedCommandAsync();
        model.Refresh();
        Assert.Contains("ID 2", model.RequestSummary);
        if (grouped)
        {
            model.SelectedPollingGroup = model.PollingGroups[1];
            model.Refresh();
            Assert.Contains("first · 1/1 · ID 2 · FC 06 · 300", model.RequestSummary);
            Assert.Contains("running", model.Status, StringComparison.OrdinalIgnoreCase);
        }
        service.CompleteCommand(ModbusCommandState.Verified);
        await run;
        Assert.Equal(revision, service.GetSnapshot().Revision);
        Assert.Equal(monitorTarget, model.RequestSummary);
        Assert.Contains("Polling", model.Status);
        Assert.DoesNotContain("Command", model.Status);
        Assert.True(model.IsBusy);
        Assert.True(model.StopCommand.CanExecute(null));
        Assert.NotEmpty(model.CommandResults);
        await model.StopAsync();
        await poll;
        model.Refresh();
        Assert.Equal(monitorTarget, model.RequestSummary);
        Assert.Contains("Stopped", model.Status);
        Assert.DoesNotContain("Command", model.Status);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task StepTargetUpdatesTheCombinedHeaderWhenTheRunningStateDoesNotChange()
    {
        var service = new HeaderMonitor();
        var model = Model(service);
        var settings = Settings();
        settings.Commands[0].Steps.Add(Step(7, 777));
        model.ApplySavedCommandSettings(settings);
        await using var analyzer = new ProtocolAnalyzer();
        var protocol = new ProtocolViewModel(analyzer) { Modbus = model, SelectedViewIndex = 2 };
        var run = model.ExecuteSelectedCommandAsync();
        model.Refresh();
        var status = model.Status;
        var changed = new List<string?>();
        protocol.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        service.AdvanceStep(2);
        model.Refresh();
        Assert.Equal(status, model.Status);
        Assert.Contains("2/2 · ID 7 · FC 06 · 777", protocol.WorkspaceHeader);
        Assert.Contains(nameof(ProtocolViewModel.WorkspaceHeader), changed);
        service.CompleteCommand(ModbusCommandState.Acknowledged);
        await run;
        Assert.Contains("2/2 · ID 7 · FC 06 · 777", protocol.WorkspaceHeader);
        Assert.Contains(model.CommandStatus, protocol.WorkspaceHeader);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("save")]
    public async Task TheNextMonitorActionReplacesTheCompletedCommandHeader(string action)
    {
        var service = new HeaderMonitor();
        var model = Model(service);
        var run = model.ExecuteSelectedCommandAsync();
        service.CompleteCommand(ModbusCommandState.Acknowledged);
        await run;
        switch (action)
        {
            case "read": await model.RunAsync(false); break;
            case "write": model.WriteValuesText = "12"; await model.WriteAsync(); break;
            default: await model.SaveSettingsAsync(); break;
        }
        model.Refresh();
        Assert.Contains("ID 1 · FC 03 · 0 + 10", model.RequestSummary);
        Assert.NotEqual(model.CommandStatus, model.Status);
        Assert.NotEmpty(model.CommandResults);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task SelectingAnotherCommandOrEditingTheMonitorCannotReuseThePreviousResultHeader()
    {
        var service = new HeaderMonitor();
        var model = Model(service);
        var run = model.ExecuteSelectedCommandAsync();
        service.CompleteCommand(ModbusCommandState.Verified);
        await run;
        model.SelectedCommand = model.CommandChoices[1];
        Assert.Empty(model.CommandResults);
        Assert.Contains("Ready", model.Status);
        Assert.Contains("ID 1", model.RequestSummary);
        run = model.ExecuteSelectedCommandAsync();
        service.CompleteCommand(ModbusCommandState.Verified);
        await run;
        model.AddressText = "90";
        Assert.Contains("Ready", model.Status);
        Assert.Contains("ID 1 · FC 03 · 90", model.RequestSummary);
    }

    [Fact]
    public async Task StoppingShowsTheDrainStateThenTheTerminalResult()
    {
        var service = new HeaderMonitor { HoldStop = true };
        var model = Model(service);
        var run = model.ExecuteSelectedCommandAsync();
        model.Refresh();
        var stop = model.StopAsync();
        try
        {
            Assert.Contains("Stopping", model.Status);
            Assert.Contains("ID 2", model.RequestSummary);
            Assert.True(model.IsBusy);
            Assert.False(model.StopCommand.CanExecute(null));
        }
        finally { service.CompleteStop(); await Task.WhenAll(stop, run); }
        Assert.Equal(model.CommandStatus, model.Status);
        Assert.Contains("stopped", model.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(model.IsBusy);
        Assert.Equal(Visibility.Collapsed, model.BusyVisibility);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedAdmissionReportsFailureWithoutReusingAnOldSuccessfulSnapshot(bool completedBefore)
    {
        var service = new HeaderMonitor();
        var model = Model(service);
        if (completedBefore)
        {
            var run = model.ExecuteSelectedCommandAsync();
            service.CompleteCommand(ModbusCommandState.Verified);
            await run;
        }
        service.Reject = true;
        await model.ExecuteSelectedCommandAsync();
        model.Refresh();
        Assert.Empty(model.CommandResults);
        Assert.Contains("failed", model.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Rejected before admission", model.Status);
        Assert.Contains("ID 2 · FC 06 · 300", model.RequestSummary);
        Assert.False(model.IsBusy);
    }

    [Theory]
    [InlineData(ModbusCommandState.Acknowledged)]
    [InlineData(ModbusCommandState.Verified)]
    [InlineData(ModbusCommandState.NotVerified)]
    [InlineData(ModbusCommandState.Canceled)]
    [InlineData(ModbusCommandState.Failed)]
    public async Task RecentResultKeepsTheActualExecutedTargetWhenBrowsingAnotherCommand(ModbusCommandState state)
    {
        var service = new HeaderMonitor();
        var model = Model(service);
        var run = model.ExecuteSelectedCommandAsync();
        service.CompleteCommand(state);
        await run;
        var summary = model.RecentCommandSummary;
        var details = model.RecentCommandResults;
        var caption = model.RecentCommandCaption;
        Assert.Contains("first", caption);
        Assert.DoesNotContain("ID ", caption);
        Assert.DoesNotContain("FC ", caption);
        Assert.Contains("ID 2", details);
        Assert.Contains("300", details);
        if (state == ModbusCommandState.Acknowledged) Assert.Contains("completion unchecked", caption);
        if (state == ModbusCommandState.Canceled) Assert.NotEmpty(model.RecentCommandAlert);
        Assert.Contains("Recent command: first · 1/1 · ID 2 · FC 06 · 300", summary);
        Assert.Contains(model.CommandStatus, summary);
        Assert.NotEmpty(details);
        Assert.NotEqual(model.CommandsLabel, model.CommandsMenuLabel);
        model.SelectedCommand = model.CommandChoices[1];
        Assert.Empty(model.CommandResults);
        Assert.Equal(summary, model.RecentCommandSummary);
        Assert.Equal(details, model.RecentCommandResults);
        Assert.Equal(caption, model.RecentCommandCaption);
        Assert.Equal(Visibility.Visible, model.RecentCommandVisibility);
        Assert.NotEqual(model.CommandsLabel, model.CommandsMenuLabel);
        Assert.Contains("ID 7", model.CommandTargetPreview);
        Assert.DoesNotContain("HEX:", model.CommandTargetPreview);
        Assert.Contains("HEX:", model.CommandPreview);
        Assert.DoesNotContain("ID ", model.CommandWirePreview);
        Assert.DoesNotContain("Read back", model.CommandWirePreview);
        Assert.StartsWith("1. ", model.CommandWirePreview);
        Assert.False(model.IsBusy);
        model.ApplySavedCommandSettings(Settings());
        Assert.Equal(Visibility.Collapsed, model.RecentCommandVisibility);
        Assert.Empty(model.RecentCommandResults);
        Assert.Empty(model.RecentCommandCaption);
        Assert.Empty(model.RecentCommandAlert);
        Assert.Equal(model.CommandsLabel, model.CommandsMenuLabel);
    }

    [Fact]
    public async Task PollingAndLaterReadsDoNotEraseTheSeparateRecentCommandResult()
    {
        var service = new HeaderMonitor();
        var model = Model(service);
        var poll = model.RunAsync(true);
        var run = model.ExecuteSelectedCommandAsync();
        service.CompleteCommand(ModbusCommandState.Acknowledged);
        await run;
        Assert.Contains("Polling", model.Status);
        Assert.Contains("completion is not checked", model.RecentCommandSummary);
        var summary = model.RecentCommandSummary;
        await model.StopAsync();
        await poll;
        await model.RunAsync(false);
        Assert.Equal(summary, model.RecentCommandSummary);
        Assert.Contains("Read complete", model.Status);
    }

    [Fact]
    public void ReapplyingCommandsKeepsTheSavedSelectionWhenTheBoundListResetsItsSelection()
    {
        var model = Model(new HeaderMonitor());
        model.CommandChoices.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                model.SelectedCommand = null;
        };
        var settings = Settings();
        settings.SelectedCommandId = "second";
        model.ApplySavedCommandSettings(settings);
        Assert.Equal("second", model.SelectedCommand?.Id);
        Assert.Equal("second", model.CreatePersistableCommandSettingsSnapshot()?.SelectedCommandId);
        Assert.Contains("ID 7", model.CommandTargetPreview);
        Assert.Empty(model.RecentCommandResults);
        Assert.False(model.IsBusy);
    }

    private static ModbusRtuViewModel Model(HeaderMonitor service)
    {
        var model = new ModbusRtuViewModel(service, () => true, () => Task.CompletedTask, () => { });
        model.ApplySavedCommandSettings(Settings());
        return model;
    }
    private static ModbusCommandSettings Settings() => new()
    {
        SelectedCommandId = "first", Commands = [
            new() { Id = "first", Name = "first", Steps = [Step(2, 300)] },
            new() { Id = "second", Name = "second", Steps = [Step(7, 700)] }
        ]
    };
    private static ModbusCommandStep Step(int unit, int address) => new()
    {
        Name = "Start", UnitId = unit, Function = 3, Value = "1", Item = new() { Address = address, Access = ModbusItemAccess.ReadWrite },
        Verification = new() { Mode = ModbusCommandVerificationMode.AcknowledgementOnly }
    };

    private sealed class HeaderMonitor : IModbusRtuGroupMonitorService, IModbusRtuCommandService
    {
        private ModbusMonitorSnapshot _monitor = new(0, ModbusMonitorState.Idle, false, false, null, null, 0, 0, null, null, false);
        private ModbusCommandSnapshot _command = new(0, null, null, ModbusCommandState.Idle, false, 0, [], null);
        private IReadOnlyList<ModbusGroupSnapshot> _groups = [];
        private TaskCompletionSource? _commandDone, _pollDone, _stopDone;
        public bool Reject, HoldStop;
        public bool IsBusy => _monitor.IsBusy || _command.IsBusy;
        public ModbusMonitorSnapshot GetSnapshot() => _monitor;
        public ModbusCommandSnapshot GetCommandSnapshot() => _command;
        public ModbusPollingSnapshot GetPollingSnapshot() => new(_monitor.Revision, _monitor.IsBusy, _monitor.IsPolling, _groups, null, null);
        public Task ExecuteCommandAsync(ModbusCommandDefinition command, CancellationToken token)
        {
            if (Reject) throw new InvalidOperationException("Rejected before admission");
            _command = new(_command.Revision + 1, command.Id, command.Name, ModbusCommandState.Running, true, 1,
                command.Steps.Select((step, index) => new ModbusCommandStepSnapshot(index + 1, step.Name, step.UnitId,
                    6, step.Item.Address, null, null, ModbusCommandStepState.Writing, false, null, null)).ToArray(), null);
            _commandDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _commandDone.Task;
        }
        public void AdvanceStep(int step) => _command = _command with { Revision = _command.Revision + 1, CurrentStep = step };
        public void CompleteCommand(ModbusCommandState terminal)
        {
            _command = _command with { Revision = _command.Revision + 1, State = terminal, IsBusy = false };
            _commandDone?.TrySetResult();
        }
        public Task RunAsync(ModbusMonitorSettings settings, bool repeat, CancellationToken token)
        {
            _monitor = new(_monitor.Revision + 1, repeat ? ModbusMonitorState.Polling : ModbusMonitorState.Stopped, repeat, repeat,
                Enumerable.Repeat((ushort)1, settings.Quantity).ToArray(), DateTimeOffset.Now, 1, 0, 1, null, true);
            _pollDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return repeat ? _pollDone.Task : Task.CompletedTask;
        }
        public Task RunGroupsAsync(ModbusPollingSettings settings, CancellationToken token)
        {
            var run = RunAsync(settings.Groups[0].Settings, true, token);
            _groups = settings.Groups.Select(group => new ModbusGroupSnapshot(group.Id, _monitor)).ToArray();
            return run;
        }
        public Task WriteAsync(ModbusMonitorSettings settings, int address, IReadOnlyList<ushort> values, CancellationToken token)
        {
            _monitor = new(_monitor.Revision + 1, ModbusMonitorState.Stopped, false, false,
                Enumerable.Repeat((ushort)1, settings.Quantity).ToArray(), DateTimeOffset.Now, 1, 0, 1, null, true, ModbusWriteState.Verified);
            return Task.CompletedTask;
        }
        public Task WriteGroupAsync(string groupId, int address, IReadOnlyList<ushort> values, CancellationToken token) => Task.CompletedTask;
        public Task StopAsync()
        {
            if (!HoldStop) { CompleteStop(); return Task.CompletedTask; }
            _stopDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _stopDone.Task;
        }
        public void CompleteStop()
        {
            if (_command.IsBusy) CompleteCommand(ModbusCommandState.Canceled);
            _monitor = _monitor with { Revision = _monitor.Revision + 1, IsBusy = false, IsPolling = false, State = ModbusMonitorState.Stopped, ValuesCurrent = false };
            _groups = _groups.Select(group => group with { Snapshot = _monitor }).ToArray();
            _pollDone?.TrySetResult();
            _stopDone?.TrySetResult();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
