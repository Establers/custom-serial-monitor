using System.Globalization;
using System.Text.Json;
using SerialMonitor.Core.Modbus;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

// Cross the authoring, monitor and actual profile persistence boundaries. The
// recording service checks explicit execution admission, without opening a port
// or pretending that an offline test has verified a physical device response.
public sealed class ModbusUserJourneyTests
{
    [Fact]
    public async Task AuthorSequenceWithNewNavigationApplySaveReloadThenExplicitRunKeepsOnlyRealDrafts()
    {
        using var journey = new Journey();
        var revision = journey.Model.CommandSettingsRevision;
        var editor = new ModbusCommandEditorViewModel(journey.Model.CreateCommandEditorSettings());
        editor.NewCommand();
        editor.NewStep();
        Assert.True(editor.TryApply(out var empty), editor.Error);
        Assert.Empty(empty.Commands);
        Assert.Null(empty.SelectedCommandId);

        editor.CommandNameText = "RAM startup";
        foreach (var value in new[] { "6", "7", "15" })
        {
            Fill(editor, 1, 0x1010, value);
            editor.NewStep(); // Valid draft included; the next untouched draft is empty.
        }
        editor.NewCommand();
        var sequenceId = Assert.Single(editor.Items).Definition!.Id;
        editor.CommandNameText = "Another product";
        Fill(editor, 7, 520, "99");
        editor.NewCommand();
        editor.NewStep();
        editor.SelectedCommand = editor.Items[0];
        Assert.True(editor.TryApply(out var applied), editor.Error);
        Assert.Equal(2, applied.Commands.Count);
        Assert.Equal(sequenceId, applied.SelectedCommandId);
        Assert.Equal(new[] { "6", "7", "15" }, applied.Commands[0].Steps.Select(step => step.Value));

        journey.Model.ApplyCommandSettings(applied, revision);
        Assert.Equal(sequenceId, journey.Model.SelectedCommand!.Id);
        Assert.Empty(journey.Service.Commands);
        Assert.False(journey.Model.ExecuteCommand.CanExecute(null));
        // Neither dialog objects nor returned Apply data may retain the live model.
        applied.Commands[0].Steps[0].Value = "54321";
        editor.ValueText = "999";
        Assert.Equal("6", journey.Model.CreatePersistableCommandSettingsSnapshot()!.Commands[0].Steps[0].Value);

        await journey.SaveAndReloadAsync();
        Assert.Equal(sequenceId, journey.Model.SelectedCommand!.Id);
        Assert.Equal(2, journey.Model.CommandChoices.Count);
        Assert.Empty(journey.Service.Commands);
        Assert.False(journey.Model.ExecuteCommand.CanExecute(null));
        journey.Connected = true;
        Assert.True(journey.Model.ExecuteCommand.CanExecute(null));
        await journey.Model.ExecuteSelectedCommandAsync();
        var executed = Assert.Single(journey.Service.Commands);
        Assert.Equal(sequenceId, executed.Id);
        Assert.Equal(new[] { "6", "7", "15" }, executed.Steps.Select(step => step.Value));
        Assert.All(executed.Steps, step =>
        {
            Assert.Equal(1, step.UnitId);
            Assert.Equal(0x1010, step.Item.Address);
        });
    }

    [Fact]
    public async Task CancelAfterAutomaticInclusionAndInvalidDraftCannotLeakIntoProfileOrNextRun()
    {
        using var journey = new Journey();
        journey.Model.ApplyCommandSettings(Seed());
        await journey.Model.SaveSettingsAsync();
        Assert.Empty(journey.Model.Error);
        var saved = await File.ReadAllTextAsync(journey.Path);
        var revision = journey.Model.CommandSettingsRevision;
        var original = Serialize(journey.Model.CreatePersistableCommandSettingsSnapshot());
        var editor = new ModbusCommandEditorViewModel(journey.Model.CreateCommandEditorSettings());
        editor.ValueText = "15";
        editor.NewStep();
        editor.SelectedCommand = editor.Items[1];
        Assert.Equal("15", editor.Items[0].Definition!.Steps[0].Value);
        var selected = editor.SelectedCommand;
        editor.UnitText = "0";
        editor.SelectedCommand = editor.Items[0];
        editor.NewCommand();
        Assert.False(editor.TryApply(out _));
        Assert.Same(selected, editor.SelectedCommand);
        Assert.Equal("0", editor.UnitText);
        // Cancel follows the real caller's non-Primary path: do not apply the
        // editor result, even though navigation updated its private command list.
        Assert.Equal(revision, journey.Model.CommandSettingsRevision);
        Assert.Equal(original, Serialize(journey.Model.CreatePersistableCommandSettingsSnapshot()));
        Assert.Equal(saved, await File.ReadAllTextAsync(journey.Path));
        var reopened = new ModbusCommandEditorViewModel(journey.Model.CreateCommandEditorSettings());
        Assert.Equal("6", reopened.ValueText);
        Assert.Equal("1", reopened.UnitText);

        await journey.SaveAndReloadAsync();
        Assert.Equal(original, Serialize(journey.Model.CreatePersistableCommandSettingsSnapshot()));
        Assert.Empty(journey.Service.Commands);
        journey.Connected = true;
        await journey.Model.ExecuteSelectedCommandAsync();
        var executed = Assert.Single(journey.Service.Commands);
        Assert.Equal("start", executed.Id);
        Assert.Equal("6", Assert.Single(executed.Steps).Value);
        Assert.Equal(1, executed.Steps[0].UnitId);
    }

    [Fact]
    public async Task RepairInvalidDraftThenSelectAnotherCommandSaveReloadAndRunKeepsThatIdentity()
    {
        using var journey = new Journey();
        journey.Model.ApplyCommandSettings(Seed());
        var editor = new ModbusCommandEditorViewModel(journey.Model.CreateCommandEditorSettings());
        var revision = journey.Model.CommandSettingsRevision;
        var first = editor.SelectedCommand;
        editor.ValueText = "not a number";
        editor.SelectedCommand = editor.Items[1];
        Assert.Same(first, editor.SelectedCommand);
        Assert.Equal("not a number", editor.ValueText);
        Assert.False(editor.TryApply(out _));
        Assert.Equal("start", journey.Model.SelectedCommand!.Id);
        Assert.Empty(journey.Service.Commands);

        editor.ValueText = "15";
        editor.SelectedCommand = editor.Items[1];
        Assert.Equal("stop", editor.SelectedCommand!.Definition!.Id);
        Assert.Equal("99", editor.ValueText);
        Assert.True(editor.TryApply(out var applied), editor.Error);
        journey.Model.ApplyCommandSettings(applied, revision);
        await journey.SaveAndReloadAsync();
        Assert.Equal("stop", journey.Model.SelectedCommand!.Id);
        Assert.Equal("15", journey.Model.CreatePersistableCommandSettingsSnapshot()!.Commands[0].Steps[0].Value);
        Assert.Contains("ID 7", journey.Model.CommandTargetPreview);
        Assert.Empty(journey.Service.Commands);

        journey.Connected = true;
        await journey.Model.ExecuteSelectedCommandAsync();
        var executed = Assert.Single(journey.Service.Commands);
        Assert.Equal("stop", executed.Id);
        var step = Assert.Single(executed.Steps);
        Assert.Equal(7, step.UnitId);
        Assert.Equal(520, step.Item.Address);
        Assert.Equal("99", step.Value);
    }

    [Fact]
    public async Task DuplicateCommandAndStepRemainPrivateAcrossApplyProfileClonesAndSelectedRun()
    {
        using var journey = new Journey();
        var seed = Seed();
        seed.Commands.RemoveAt(1);
        seed.Commands[0].Steps[0].Item.ValueNames = [new() { Value = 6, Name = "Start" }];
        seed.Commands[0].Steps[0].Value = "Start";
        journey.Model.ApplyCommandSettings(seed);
        var editor = new ModbusCommandEditorViewModel(journey.Model.CreateCommandEditorSettings());
        Assert.True(editor.DuplicateCommand(), editor.Error);
        var copyId = editor.SelectedCommand!.Definition!.Id;
        Assert.NotEqual("start", copyId);
        editor.CommandNameText = "Copied recipe";
        Assert.True(editor.DuplicateStep(), editor.Error);
        editor.ValueNamesText = "6=Start\n15=Copied";
        editor.ValueText = "Copied";
        editor.AddressText = "4113";
        editor.VerificationIndex = 1;
        editor.StatusFunctionIndex = 3;
        editor.StatusAddressText = "4103";
        editor.MaskText = "1";
        editor.ExpectedText = "1";
        editor.SelectedStep = editor.Steps[0];
        Assert.True(editor.TryApply(out var applied), editor.Error);
        journey.Model.ApplyCommandSettings(applied);
        applied.Commands[1].Steps[1].Item.ValueNames[1].Name = "Detached apply object";
        editor.Items[0].Definition!.Steps[0].Item.ValueNames[0].Name = "Detached editor";
        seed.Commands[0].Steps[0].Item.ValueNames[0].Name = "Detached source";

        await journey.SaveAndReloadAsync();
        var restored = journey.Model.CreatePersistableCommandSettingsSnapshot()!;
        Assert.Equal(copyId, journey.Model.SelectedCommand!.Id);
        Assert.Equal(new[] { "start", copyId }, restored.Commands.Select(command => command.Id));
        Assert.Equal("Start", Assert.Single(restored.Commands[0].Steps[0].Item.ValueNames).Name);
        Assert.Equal(ModbusCommandVerificationMode.ReadBack, restored.Commands[0].Steps[0].Verification.Mode);
        Assert.Equal(new[] { "Start", "Copied" }, restored.Commands[1].Steps.Select(step => step.Value));
        restored.Commands[1].Steps[1].Verification.Address = 999;
        restored.Commands[1].Steps[1].Item.ValueNames.Clear();
        Assert.Empty(journey.Service.Commands);

        journey.Connected = true;
        await journey.Model.ExecuteSelectedCommandAsync();
        var executed = Assert.Single(journey.Service.Commands);
        Assert.Equal(copyId, executed.Id);
        Assert.Equal(2, executed.Steps.Count);
        Assert.Equal(new ushort[] { 15 }, executed.Steps[1].EncodeValues());
        Assert.Equal(4113, executed.Steps[1].Item.Address);
        Assert.Equal(4103, executed.Steps[1].Verification.Address);
        Assert.Equal(ModbusCommandVerificationMode.Status, executed.Steps[1].Verification.Mode);
        Assert.Equal("Copied", executed.Steps[1].Item.ValueNames[1].Name);
    }

    private static void Fill(ModbusCommandEditorViewModel editor, int unit, int address, string value)
    {
        editor.UnitText = unit.ToString(CultureInfo.InvariantCulture);
        editor.AddressText = address.ToString(CultureInfo.InvariantCulture);
        editor.ValueText = value;
    }

    private static string Serialize(ModbusCommandSettings? settings) => JsonSerializer.Serialize(settings);

    private static ModbusCommandSettings Seed() => new()
    {
        SelectedCommandId = "start",
        Commands =
        [
            new() { Id = "start", Name = "Start", Steps = [Step(1, 0x1010, "6")] },
            new() { Id = "stop", Name = "Another device", Steps = [Step(7, 520, "99")] }
        ]
    };

    private static ModbusCommandStep Step(int unit, int address, string value) => new()
    {
        Name = "Write", UnitId = unit, Function = 3, Value = value,
        Item = new() { Address = address, Access = ModbusItemAccess.ReadWrite }
    };

    private sealed class Journey : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SerialMonitor.Tests", Guid.NewGuid().ToString("N"));
        private readonly ProfileService _profiles = new();
        private AppProfile _profile;
        public string Path => System.IO.Path.Combine(_directory, "journey.json");
        public RecordingMonitor Service { get; } = new();
        public ModbusRtuViewModel Model { get; private set; }
        public bool Connected { get; set; }
        public int Saves { get; private set; }

        public Journey()
        {
            Directory.CreateDirectory(_directory);
            _profile = _profiles.CreateDefaultProfile();
            _profile.ModbusPolling = new();
            Model = NewModel();
            Model.ApplySettings(_profile.ModbusSettings);
            Model.ApplySavedPollingSettings(_profile.ModbusPolling);
        }

        private ModbusRtuViewModel NewModel() => new(Service, () => Connected, SaveAsync, () => { });

        private async Task SaveAsync()
        {
            // Same three configuration snapshots used by MainViewModel's profile
            // creation; no runtime state or execution is persisted.
            _profile.ModbusSettings = Model.CreatePersistableSettingsSnapshot();
            _profile.ModbusPolling = Model.CreatePersistablePollingSettingsSnapshot();
            _profile.ModbusCommands = Model.CreatePersistableCommandSettingsSnapshot();
            await _profiles.SaveAsync(Path, _profile, CancellationToken.None);
            Assert.Null(_profiles.LastError);
            Saves++;
        }

        public async Task SaveAndReloadAsync()
        {
            Assert.False(Connected); // The real app requires disconnect before profile reload.
            var before = Saves;
            await Model.SaveSettingsAsync();
            Assert.Empty(Model.Error);
            Assert.Equal(before + 1, Saves);
            _profile = await new ProfileService().LoadAsync(Path, CancellationToken.None);
            Model = NewModel();
            Model.ApplySettings(_profile.ModbusSettings);
            Model.ApplySavedPollingSettings(_profile.ModbusPolling);
            Model.ApplySavedCommandSettings(_profile.ModbusCommands);
            Assert.Equal(0, Service.NonCommandCalls);
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private sealed class RecordingMonitor : IModbusRtuMonitorService, IModbusRtuCommandService
    {
        public List<ModbusCommandDefinition> Commands { get; } = [];
        public int NonCommandCalls { get; private set; }
        public bool IsBusy => false;
        public ModbusMonitorSnapshot GetSnapshot() => new(0, ModbusMonitorState.Idle, false, false, null, null, 0, 0, null, null, false);
        public ModbusCommandSnapshot GetCommandSnapshot() => new(0, null, null, ModbusCommandState.Idle, false, 0, [], null);
        public Task ExecuteCommandAsync(ModbusCommandDefinition command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.Validate();
            Commands.Add(command.Clone());
            return Task.CompletedTask;
        }
        public Task RunAsync(ModbusMonitorSettings settings, bool repeat, CancellationToken cancellationToken)
        {
            NonCommandCalls++;
            throw new InvalidOperationException("Authoring and loading must not start a read or polling.");
        }
        public Task WriteAsync(ModbusMonitorSettings settings, int address, IReadOnlyList<ushort> values, CancellationToken cancellationToken)
        {
            NonCommandCalls++;
            throw new InvalidOperationException("Authoring and loading must not issue a direct write.");
        }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
