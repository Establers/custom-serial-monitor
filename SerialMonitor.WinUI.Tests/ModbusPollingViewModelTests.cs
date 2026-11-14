using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusPollingViewModelTests
{
    [Fact]
    public async Task SelectingAnotherGroupKeepsPollingAndWritesToTheCapturedSelectedGroup()
    {
        var service = new GroupMonitor();
        var model = Model(service);
        model.ApplySavedPollingSettings(Workspace());
        var run = model.RunAsync(true);
        model.Refresh();
        Assert.Equal("101", model.Rows[0].ValueText);
        Assert.True(model.IsBusy);
        model.SelectedPollingGroup = model.PollingGroups[1];
        Assert.Equal("2", model.UnitText);
        Assert.Equal("100", model.AddressText);
        Assert.Equal("202", Assert.Single(model.Rows).ValueText);
        Assert.False(run.IsCompleted);
        service.BeforeGroupWrite = () =>
        {
            model.SelectedPollingGroup = model.PollingGroups[0];
            Assert.Equal("b", model.SelectedPollingGroup?.Id);
            Assert.False(model.IsGroupSelectionEnabled);
        };
        model.WriteValuesText = "7";
        await model.WriteAsync();
        var write = Assert.Single(service.GroupWrites);
        Assert.Equal("b", write.Id);
        Assert.Equal(100, write.Address);
        Assert.Equal(new ushort[] { 7 }, write.Values);
        Assert.Empty(service.SingleWrites);
        Assert.Contains("ID 1", model.PollingSummary);
        Assert.Contains("ID 2", model.PollingSummary);
        await model.StopAsync();
        await run;
        Assert.False(model.IsBusy);
        Assert.DoesNotContain("Current", model.Rows[0].StatusText);
    }

    [Fact]
    public async Task SameIdentityFromOldWorkspaceCannotPopulateANewTarget()
    {
        var service = new GroupMonitor();
        var model = Model(service);
        model.ApplySavedPollingSettings(Workspace());
        var run = model.RunAsync(true);
        model.Refresh();
        await model.StopAsync();
        await run;
        var changed = Workspace();
        changed.Groups[0].Settings.UnitId = 7;
        model.ApplySavedPollingSettings(changed);
        model.Refresh();
        Assert.Equal("7", model.UnitText);
        Assert.All(model.Rows, row => Assert.Equal("—", row.ValueText));
    }

    [Fact]
    public async Task SingleReadValuesAreRetainedPerGroupWithoutBeingReassignedToAnotherDevice()
    {
        var service = new GroupMonitor();
        var model = Model(service);
        model.ApplySavedPollingSettings(Workspace());
        await model.RunAsync(false);
        Assert.Equal("101", model.Rows[0].ValueText);
        model.SelectedPollingGroup = model.PollingGroups[1];
        Assert.Equal("—", model.Rows[0].ValueText);
        await model.RunAsync(false);
        Assert.Equal("202", model.Rows[0].ValueText);
        model.SelectedPollingGroup = model.PollingGroups[0];
        Assert.Equal("101", model.Rows[0].ValueText);
        Assert.DoesNotContain("Current", model.Rows[0].StatusText);
        Assert.Equal(new[] { 1, 2 }, service.SingleReads.Select(settings => settings.UnitId));
        model.UnitText = "9";
        var persisted = model.CreatePersistablePollingSettingsSnapshot()!;
        Assert.Equal(9, persisted.Groups[0].Settings.UnitId);
        model.SelectedPollingGroup = model.PollingGroups[1];
        model.SelectedPollingGroup = model.PollingGroups[0];
        Assert.Equal("—", model.Rows[0].ValueText);
    }

    [Fact]
    public async Task ExplicitEmptyWorkspaceDoesNotFallBackToSendingTheLegacyTarget()
    {
        var service = new GroupMonitor();
        var model = Model(service);
        Assert.Null(model.CreatePersistablePollingSettingsSnapshot());
        model.ApplySavedPollingSettings(new());
        Assert.NotNull(model.CreatePersistablePollingSettingsSnapshot());
        await model.RunAsync(true);
        await model.RunAsync(false);
        model.WriteValuesText = "1";
        await model.WriteAsync();
        Assert.Empty(service.GroupRuns);
        Assert.Empty(service.SingleReads);
        Assert.Empty(service.SingleWrites);
        Assert.NotEmpty(model.Error);
    }

    [Fact]
    public async Task OrphanSelectionDoesNotChooseAnImplicitWriteTargetButEnabledGroupsCanPoll()
    {
        var service = new GroupMonitor();
        var model = Model(service);
        var settings = Workspace();
        settings.SelectedGroupId = "missing";
        model.ApplySavedPollingSettings(settings);
        Assert.Null(model.SelectedPollingGroup);
        await model.RunAsync(false);
        Assert.Empty(service.SingleReads);
        var run = model.RunAsync(true);
        Assert.Single(service.GroupRuns);
        model.WriteValuesText = "1";
        await model.WriteAsync();
        Assert.Empty(service.GroupWrites);
        model.SelectedPollingGroup = model.PollingGroups[1];
        Assert.Equal("202", model.Rows[0].ValueText);
        await model.StopAsync();
        await run;
    }

    [Fact]
    public void InvalidSelectedDraftBlocksSelectionAndWorkspaceApplyChecksRevision()
    {
        var model = Model(new GroupMonitor());
        model.ApplySavedPollingSettings(Workspace());
        model.AddressText = "";
        model.SelectedPollingGroup = model.PollingGroups[1];
        Assert.Equal("a", model.SelectedPollingGroup?.Id);
        Assert.NotEmpty(model.Error);
        Assert.Equal(0, model.CreatePersistablePollingSettingsSnapshot()!.Groups[0].Settings.Address);
        model.AddressText = "0";
        var revision = model.SettingsRevision;
        model.UnitText = "3";
        Assert.Throws<InvalidOperationException>(() => model.ApplyPollingSettings(Workspace(), revision));
    }

    [Fact]
    public void LegacyMigrationAndPersistenceUseIndependentDefinitionCopies()
    {
        var model = Model(new GroupMonitor());
        model.ApplySettings(new() { UnitId = 7, Address = 40, Quantity = 2 });
        var draft = model.CreatePollingEditorSettings();
        var migrated = Assert.Single(draft.Groups);
        Assert.Equal(7, migrated.Settings.UnitId);
        Assert.Equal(40, migrated.Settings.Address);
        model.ApplyPollingSettings(draft);
        migrated.Settings.UnitId = 99;
        Assert.Equal("7", model.UnitText);
        var persisted = model.CreatePersistablePollingSettingsSnapshot()!;
        persisted.Groups[0].Settings.Address = 999;
        Assert.Equal(40, model.CreatePersistablePollingSettingsSnapshot()!.Groups[0].Settings.Address);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedSingleOperationCannotAssignThePreviousGroupsValuesToANewTarget(bool write)
    {
        var service = new GroupMonitor();
        var model = Model(service);
        model.ApplySavedPollingSettings(Workspace());
        await model.RunAsync(false);
        model.SelectedPollingGroup = model.PollingGroups[1];
        service.RejectSingle = true;
        if (write) { model.WriteValuesText = "7"; await model.WriteAsync(); }
        else await model.RunAsync(false);
        Assert.NotEmpty(model.Error);
        model.SelectedPollingGroup = model.PollingGroups[0];
        model.SelectedPollingGroup = model.PollingGroups[1];
        Assert.Equal("—", Assert.Single(model.Rows).ValueText);
    }

    [Fact]
    public async Task DisabledAndMalformedImportedGroupsCannotBeWrittenThroughTheSingleWritePath()
    {
        var service = new GroupMonitor();
        var model = Model(service);
        var settings = Workspace();
        settings.Groups[0].Enabled = false;
        model.ApplySavedPollingSettings(settings);
        model.WriteValuesText = "1";
        await model.WriteAsync();
        Assert.Empty(service.SingleWrites);
        settings.Groups[0].Enabled = true;
        settings.Groups[0].HasInvalidMetadata = true;
        model.ApplySavedPollingSettings(settings);
        model.WriteValuesText = "1";
        await model.WriteAsync();
        Assert.Empty(service.SingleWrites);
        await model.RunAsync(false);
        Assert.Empty(service.SingleReads);
    }

    [Fact]
    public async Task StartingAfterEditingTheSelectedTargetRebuildsItsDisplayContext()
    {
        var service = new GroupMonitor();
        var model = Model(service);
        model.ApplySavedPollingSettings(Workspace());
        model.UnitText = "3";
        var run = model.RunAsync(true);
        model.Refresh();
        Assert.Equal("303", model.Rows[0].ValueText);
        Assert.Equal(3, Assert.Single(service.GroupRuns).Groups[0].Settings.UnitId);
        await model.StopAsync();
        await run;
    }

    [Fact]
    public async Task DuplicateGroupIdentityCannotAssignSingleOperationResultsToAnotherDevice()
    {
        var service = new GroupMonitor();
        var model = Model(service);
        var settings = Workspace();
        settings.Groups[1].Id = "a";
        model.ApplySavedPollingSettings(settings);
        await model.RunAsync(false);
        model.WriteValuesText = "1";
        await model.WriteAsync();
        Assert.Empty(service.SingleReads);
        Assert.Empty(service.SingleWrites);
        Assert.NotEmpty(model.Error);
    }

    [Fact]
    public async Task AFifthUnselectedGroupErrorIsVisibleWithoutLosingOtherGroupsOrTheirTargets()
    {
        var service = new GroupMonitor();
        var model = Model(service);
        var settings = new ModbusPollingSettings
        {
            SelectedGroupId = "g1",
            Groups = Enumerable.Range(1, 5).Select(id => new ModbusPollingGroup
            {
                Id = $"g{id}", Name = $"Device {id}", Enabled = id != 4,
                Settings = new() { UnitId = id, Quantity = 2 }
            }).ToList()
        };
        model.ApplySavedPollingSettings(settings);
        var run = model.RunAsync(true);
        var notifications = new List<string?>();
        model.PollingGroups[4].PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        service.FailGroup("g5");
        model.Refresh();
        Assert.Contains("Groups 5 · Errors 1 · Current 3 · Waiting/stale 0 · Disabled 1", model.PollingHealthSummary);
        Assert.Contains("Device 5", model.PollingHealthSummary);
        Assert.Equal(model.PollingHealthSummary, model.MonitorSummary);
        Assert.StartsWith("Error · Device 5", model.PollingGroups[4].SelectionLabel);
        Assert.Contains(nameof(ModbusPollingGroupRow.SelectionLabel), notifications);
        Assert.Equal("g1", model.SelectedPollingGroup?.Id);
        Assert.Equal("101", model.Rows[0].ValueText);
        Assert.True(model.IsBusy);
        Assert.False(run.IsCompleted);
        var healthUpdates = 0;
        model.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(model.PollingHealthSummary)) healthUpdates++; };
        model.Refresh();
        model.Refresh();
        Assert.Equal(0, healthUpdates);
        await model.StopAsync();
        await run;
    }

    [Fact]
    public async Task SingleReadIsReportedAsAValidLastResponseAndTargetEditsUpdateTheChoiceLabel()
    {
        var model = Model(new GroupMonitor());
        model.ApplySavedPollingSettings(Workspace());
        await model.RunAsync(false);
        Assert.Equal(model.SelectedPollingGroup!.DisplayName, model.SelectedPollingGroup.SelectionLabel);
        Assert.Contains("Read complete", model.SelectedPollingGroup.StatusText);
        model.UnitText = "9";
        model.CreatePersistablePollingSettingsSnapshot();
        model.Refresh();
        Assert.Contains("ID 9", model.SelectedPollingGroup.SelectionLabel);
        Assert.Equal(model.SelectedPollingGroup.DisplayName, model.SelectedPollingGroup.SelectionLabel);
        Assert.Contains("Current 0", model.PollingHealthSummary);
    }

    [Fact]
    public void EmptyAndInvalidImportedGroupsKeepAnExplicitSetupPathWithoutSending()
    {
        var service = new GroupMonitor();
        var model = Model(service);
        Assert.False(model.NeedsPollingGroup);
        model.ApplySavedPollingSettings(new());
        Assert.True(model.NeedsPollingGroup);
        Assert.Equal(model.GroupEditorLabel, model.EmptySetupLabel);
        Assert.Contains("Groups 0", model.PollingHealthSummary);
        var settings = Workspace();
        settings.Groups[1].HasInvalidMetadata = true;
        model.ApplySavedPollingSettings(settings);
        Assert.Contains("Errors 1", model.PollingHealthSummary);
        Assert.StartsWith("Error", model.PollingGroups[1].SelectionLabel);
        settings = Workspace();
        settings.Groups[0].Settings = null!;
        model.ApplySavedPollingSettings(settings);
        Assert.True(model.NeedsPollingGroup);
        Assert.False(model.IsTargetSettingsEnabled);
        Assert.Contains("Errors 1", model.PollingHealthSummary);
        Assert.Equal(model.GroupEditorLabel, model.EmptySetupLabel);
        Assert.Empty(service.GroupRuns);
        Assert.Empty(service.SingleReads);
    }

    [Fact]
    public void ReapplyingGroupsKeepsTheSavedSelectionWhenTheBoundListResetsItsSelection()
    {
        var service = new GroupMonitor();
        var model = Model(service);
        model.ApplySavedPollingSettings(Workspace());
        model.PollingGroups.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                model.SelectedPollingGroup = null;
        };
        var settings = Workspace();
        settings.SelectedGroupId = "b";
        model.ApplySavedPollingSettings(settings);
        Assert.Equal("b", model.SelectedPollingGroup?.Id);
        Assert.Equal("2", model.UnitText);
        Assert.Equal("b", model.CreatePersistablePollingSettingsSnapshot()?.SelectedGroupId);
        Assert.Empty(service.SingleReads);
        Assert.Empty(service.GroupRuns);
    }

    private static ModbusRtuViewModel Model(GroupMonitor service) => new(service, () => true, () => Task.CompletedTask, () => { });
    private static ModbusPollingSettings Workspace() => new()
    {
        SelectedGroupId = "a", Groups = [
            new() { Id = "a", Name = "Drive", Settings = new() { UnitId = 1, Address = 0, Quantity = 2 } },
            new() { Id = "b", Name = "Output", Settings = new() { UnitId = 2, Address = 100, Quantity = 1 } }
        ]
    };

    private sealed class GroupMonitor : IModbusRtuGroupMonitorService
    {
        private ModbusMonitorSnapshot _single = Empty();
        private ModbusPollingSnapshot _groups = new(0, false, false, [], null, null);
        private TaskCompletionSource _run = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsBusy => _groups.IsBusy || _single.IsBusy;
        public bool RejectSingle { get; set; }
        public Action? BeforeGroupWrite { get; set; }
        public List<ModbusPollingSettings> GroupRuns { get; } = [];
        public List<ModbusMonitorSettings> SingleReads { get; } = [];
        public List<(string Id, int Address, ushort[] Values)> GroupWrites { get; } = [];
        public List<(int Address, ushort[] Values)> SingleWrites { get; } = [];
        public ModbusMonitorSnapshot GetSnapshot() => _groups.IsBusy ? _single with { IsBusy = true, IsPolling = true } : _single;
        public ModbusPollingSnapshot GetPollingSnapshot() => _groups;
        public void FailGroup(string id) => _groups = _groups with
        {
            Revision = _groups.Revision + 1,
            Groups = _groups.Groups.Select(group => group.GroupId != id ? group : group with
            {
                Snapshot = group.Snapshot with { Revision = group.Snapshot.Revision + 1, State = ModbusMonitorState.Error,
                    IsBusy = false, IsPolling = false, ValuesCurrent = false, ErrorCount = 1, Error = "Device rejected the request" }
            }).ToArray()
        };
        public Task RunGroupsAsync(ModbusPollingSettings settings, CancellationToken cancellationToken)
        {
            GroupRuns.Add(settings.Clone());
            _run = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _groups = new(_groups.Revision + 1, true, true, settings.Groups.Select(group => new ModbusGroupSnapshot(group.Id,
                new(1, ModbusMonitorState.Polling, group.Enabled, group.Enabled, Enumerable.Repeat((ushort)(group.Settings.UnitId * 101), group.Settings.Quantity).ToArray(), DateTimeOffset.Now, 1, 0, 2, null, true))).ToArray(), null, null);
            return _run.Task.WaitAsync(cancellationToken);
        }
        public Task RunAsync(ModbusMonitorSettings settings, bool repeat, CancellationToken cancellationToken)
        {
            if (RejectSingle) throw new InvalidOperationException("Connection changed before admission.");
            SingleReads.Add(settings.Clone());
            _single = new(_single.Revision + 1, ModbusMonitorState.Stopped, false, false,
                Enumerable.Repeat((ushort)(settings.UnitId * 101), settings.Quantity).ToArray(), DateTimeOffset.Now, 1, 0, 2, null, true);
            return Task.CompletedTask;
        }
        public Task WriteGroupAsync(string groupId, int address, IReadOnlyList<ushort> values, CancellationToken cancellationToken)
        {
            BeforeGroupWrite?.Invoke();
            GroupWrites.Add((groupId, address, values.ToArray()));
            return Task.CompletedTask;
        }
        public Task WriteAsync(ModbusMonitorSettings settings, int address, IReadOnlyList<ushort> values, CancellationToken cancellationToken)
        {
            if (RejectSingle) throw new InvalidOperationException("Connection changed before admission.");
            SingleWrites.Add((address, values.ToArray()));
            return Task.CompletedTask;
        }
        public Task StopAsync()
        {
            _groups = _groups with { Revision = _groups.Revision + 1, IsBusy = false, IsPolling = false,
                Groups = _groups.Groups.Select(group => group with { Snapshot = group.Snapshot with { Revision = group.Snapshot.Revision + 1,
                    IsBusy = false, IsPolling = false, ValuesCurrent = false, State = ModbusMonitorState.Stopped } }).ToArray() };
            _run.TrySetResult();
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private static ModbusMonitorSnapshot Empty() => new(0, ModbusMonitorState.Idle, false, false, null, null, 0, 0, null, null, false);
    }
}
