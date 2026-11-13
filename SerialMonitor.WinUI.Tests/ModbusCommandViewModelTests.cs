using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusCommandViewModelTests
{
    [Fact]
    public async Task LoadingAndEditingNeverRunAndOnlyExplicitSelectionChoosesATarget()
    {
        var service = new CommandMonitor();
        var model = Model(service);
        Assert.Null(model.CreatePersistableCommandSettingsSnapshot());
        var settings = Settings();
        settings.SelectedCommandId = "missing";
        model.ApplySavedCommandSettings(settings);
        Assert.Null(model.SelectedCommand);
        await model.ExecuteSelectedCommandAsync();
        Assert.Empty(service.Commands);
        model.SelectedCommand = model.CommandChoices[1];
        Assert.Contains("ID 7", model.CommandPreview);
        Assert.Contains("HEX:", model.CommandPreview);
        var draft = model.CreateCommandEditorSettings();
        draft.Commands[1].Steps[0].UnitId = 99;
        Assert.Equal(7, model.CreatePersistableCommandSettingsSnapshot()!.Commands[1].Steps[0].UnitId);
        await model.ExecuteSelectedCommandAsync();
        Assert.Equal(7, Assert.Single(service.Commands).Steps[0].UnitId);
        Assert.Contains("ACK", model.CommandStatus);
        Assert.DoesNotContain("All configured result checks passed", model.CommandStatus);
        Assert.Contains("ID 7", model.CommandResults);
        Assert.Contains("300", model.CommandResults);
    }

    [Fact]
    public async Task AnAdmittedCommandCapturesItsTargetAndExcludesEditingAndOtherWrites()
    {
        var service = new CommandMonitor { Hold = true };
        var model = Model(service);
        model.ApplySavedCommandSettings(Settings());
        var running = model.ExecuteSelectedCommandAsync();
        Assert.True(model.IsBusy);
        Assert.False(model.IsSettingsEnabled);
        Assert.False(model.IsCommandSelectionEnabled);
        Assert.Contains("ID 2", model.RequestSummary);
        Assert.Contains("FC 06", model.RequestSummary);
        Assert.False(model.WriteCommand.CanExecute(null));
        model.SelectedCommand = model.CommandChoices[1];
        Assert.Equal("first", model.SelectedCommand?.Id);
        Assert.Throws<InvalidOperationException>(() => model.ApplySavedCommandSettings(Settings()));
        Assert.Throws<InvalidOperationException>(() => model.ApplySettings(new()));
        await model.StopAsync();
        await running;
        Assert.False(model.IsBusy);
        Assert.Equal(2, Assert.Single(service.Commands).Steps[0].UnitId);
        Assert.Contains("stopped", model.CommandStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReloadingSameIdentityCannotDisplayPreviousResultsAndRevisionBlocksStaleEditor()
    {
        var model = Model(new CommandMonitor());
        model.ApplySavedCommandSettings(Settings());
        await model.ExecuteSelectedCommandAsync();
        Assert.NotEmpty(model.CommandResults);
        var revision = model.CommandSettingsRevision;
        var changed = Settings();
        changed.Commands[0].Steps[0].UnitId = 8;
        model.ApplySavedCommandSettings(changed);
        model.Refresh();
        Assert.Empty(model.CommandResults);
        Assert.Contains("ID 8", model.CommandPreview);
        Assert.Throws<InvalidOperationException>(() => model.ApplyCommandSettings(Settings(), revision));
    }

    [Fact]
    public async Task ARejectedAdmissionCannotReuseThePreviousCommandsSuccessfulSnapshot()
    {
        var service = new CommandMonitor();
        var model = Model(service);
        model.ApplySavedCommandSettings(Settings());
        await model.ExecuteSelectedCommandAsync();
        Assert.NotEmpty(model.CommandResults);
        service.Reject = true;
        await model.ExecuteSelectedCommandAsync();
        model.Refresh();
        Assert.Empty(model.CommandResults);
        Assert.NotEmpty(model.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandsCannotBypassReadOnlyOrPartialMultiRegisterMonitorDefinitions(bool partial)
    {
        var service = new CommandMonitor();
        var model = Model(service);
        model.ApplySettings(new()
        {
            UnitId = 2, Address = 300, Quantity = 4,
            Items = [new() { Address = 300, Access = partial ? ModbusItemAccess.ReadWrite : ModbusItemAccess.ReadOnly,
                ValueType = partial ? ModbusValueType.UInt32 : ModbusValueType.UInt16 }]
        });
        model.ApplySavedCommandSettings(Settings());
        await model.ExecuteSelectedCommandAsync();
        Assert.Empty(service.Commands);
        Assert.NotEmpty(model.Error);
    }

    [Fact]
    public async Task InvalidLaterStepAndDuplicateIdentifiersBlockWholeCommandWorkspace()
    {
        var service = new CommandMonitor();
        var model = Model(service);
        var settings = Settings();
        settings.Commands[0].Steps.Add(new() { UnitId = 0, Value = "1" });
        model.ApplySavedCommandSettings(settings);
        await model.ExecuteSelectedCommandAsync();
        Assert.Empty(service.Commands);
        settings = Settings();
        settings.Commands[1].Id = settings.Commands[0].Id;
        model.ApplySavedCommandSettings(settings);
        await model.ExecuteSelectedCommandAsync();
        Assert.Empty(service.Commands);
    }

    [Fact]
    public async Task IndependentCommandsWorkWithExplicitlyEmptyPollingWorkspace()
    {
        var service = new CommandMonitor();
        var model = Model(service);
        model.ApplySavedPollingSettings(new());
        model.ApplySavedCommandSettings(Settings());
        await model.ExecuteSelectedCommandAsync();
        Assert.Single(service.Commands);
        Assert.Empty(model.Rows);
    }

    private static ModbusRtuViewModel Model(CommandMonitor service) => new(service, () => true, () => Task.CompletedTask, () => { });
    private static ModbusCommandSettings Settings() => new()
    {
        SelectedCommandId = "first",
        Commands = [Definition("first", 2), Definition("second", 7)]
    };
    private static ModbusCommandDefinition Definition(string id, int unit) => new()
    {
        Id = id, Name = id, Steps = [new()
        {
            Name = "Start", UnitId = unit, Function = 3, Value = "1",
            Item = new() { Address = 300, Access = ModbusItemAccess.ReadWrite },
            Verification = new() { Mode = ModbusCommandVerificationMode.AcknowledgementOnly }
        }]
    };

    private sealed class CommandMonitor : IModbusRtuMonitorService, IModbusRtuCommandService
    {
        public bool Hold, Reject;
        private bool _busy;
        private TaskCompletionSource? _completion;
        private ModbusCommandSnapshot _command = new(0, null, null, ModbusCommandState.Idle, false, 0, [], null);
        public List<ModbusCommandDefinition> Commands { get; } = [];
        public bool IsBusy => _busy;
        public ModbusMonitorSnapshot GetSnapshot() => new(0, ModbusMonitorState.Idle, _busy, false, null, null, 0, 0, null, null, false);
        public ModbusCommandSnapshot GetCommandSnapshot() => _command;
        public Task ExecuteCommandAsync(ModbusCommandDefinition command, CancellationToken token)
        {
            if (Reject) throw new InvalidOperationException("Rejected before admission");
            Commands.Add(command.Clone());
            _busy = Hold;
            _command = new(_command.Revision + 1, command.Id, command.Name,
                Hold ? ModbusCommandState.Running : ModbusCommandState.Acknowledged, Hold, 1,
                command.Steps.Select((step, index) => new ModbusCommandStepSnapshot(index + 1, step.Name, step.UnitId,
                    6, step.Item.Address, null, null, ModbusCommandStepState.Acknowledged, true, null, null)).ToArray(), null);
            if (!Hold) return Task.CompletedTask;
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _completion.Task;
        }
        public Task RunAsync(ModbusMonitorSettings settings, bool repeat, CancellationToken token) => Task.CompletedTask;
        public Task WriteAsync(ModbusMonitorSettings settings, int address, IReadOnlyList<ushort> values, CancellationToken token) => Task.CompletedTask;
        public Task StopAsync()
        {
            _busy = false;
            _command = _command with { Revision = _command.Revision + 1, IsBusy = false, State = ModbusCommandState.Canceled };
            _completion?.TrySetResult();
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
