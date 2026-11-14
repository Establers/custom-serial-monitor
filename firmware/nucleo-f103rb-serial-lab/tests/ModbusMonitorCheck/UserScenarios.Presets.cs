using System.Text.Json;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;
using static Oracle;

internal sealed partial class LiveCheck
{
    private ushort[] _scenarioPresetRam = [];
    private string ScenarioProfilePath => Path.Combine(evidence.Directory, "user-profile.json");

    private static ModbusCommandSettings ScenarioPresets(
        [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../../.."));
        var path = Path.Combine(root, "firmware/nucleo-f103rb-serial-lab/presets/modbus-commands.json");
        var settings = JsonSerializer.Deserialize<ModbusCommandSettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Missing preset definitions.");
        settings.Validate();
        Require(settings.Commands.Count == 10 && settings.Commands.Sum(command => command.Steps.Count) == 13,
            "Reviewed preset count/steps changed.");
        return settings;
    }

    private async Task SaveLoadPresetScenario()
    {
        var before = _serial.WrittenByteCount;
        var model = ScenarioModel;
        model.ApplyPollingSettings(new() { SelectedGroupId = "preset-ram", Groups =
        [new() { Id = "preset-ram", Name = "Preset RAM", Settings = ScenarioSettings(3, Bank + 0x10, 10) },
         new() { Id = "preset-led", Name = "LED", Settings = ScenarioSettings(1, Bank, 16) }] });
        model.ApplyCommandSettings(ScenarioPresets());
        model.SelectedCommand = model.CommandChoices.Single(choice => choice.Id == "nucleo-f103rb-ram-sequence");
        await ScenarioButton(model.SaveSettingsCommand);
        var profile = await new ProfileService().LoadAsync(ScenarioProfilePath, CommandToken);
        Require(profile.ModbusCommands?.Commands.Count == 10 && profile.ModbusCommands.SelectedCommandId == "nucleo-f103rb-ram-sequence",
            "Actual private profile save/load lost commands or selection.");
        var loaded = new ModbusRtuViewModel(ScenarioMaster,
            () => _serial.IsConnected && _serial.ExclusiveSessionOwner is null, SaveScenarioProfile, () => { });
        loaded.ApplySettings(profile.ModbusSettings);
        loaded.ApplySavedPollingSettings(profile.ModbusPolling);
        loaded.ApplySavedCommandSettings(profile.ModbusCommands);
        _scenarioModel = loaded;
        Require(loaded.CommandChoices.Count == 10 && loaded.SelectedCommand?.Id == "nucleo-f103rb-ram-sequence" &&
            !loaded.IsBusy && _serial.WrittenByteCount == before, "Loading saved definitions transmitted or changed the selected command.");
        await CommandEvidence("profile-reload", new { privateProfilePath = ScenarioProfilePath, commands = loaded.CommandChoices.Count,
            selectedCommand = loaded.SelectedCommand?.Id, transmittedBytes = _serial.WrittenByteCount - before });
    }

    private async Task ExecuteInstalledPresetScenario()
    {
        // Independent reviewed values, not values re-encoded by the app codec.
        var expected = new Dictionary<string, (int Address, byte ReadFunction, ushort[] Values)>
        {
            ["led-on"] = (Bank, 1, [1]), ["led-off"] = (Bank, 1, [0]), ["led-cycle"] = (Bank, 1, [0]),
            ["ram-uint16"] = (0x1010, 3, [123]), ["ram-int16"] = (0x1011, 3, [0xFF85]),
            ["ram-scaled-int16"] = (0x1012, 3, [253]), ["ram-uint32"] = (0x1014, 3, [0x1234, 0x5678]),
            ["ram-float32"] = (0x1016, 3, [0x3FC0, 0]), ["ram-int32"] = (0x1018, 3, [0xF8A4, 0x32EB]),
            ["ram-sequence"] = (0x1010, 3, [15])
        };
        var presets = ScenarioPresets();
        var model = ScenarioModel;
        foreach (var definition in presets.Commands)
        {
            model.SelectedCommand = model.CommandChoices.Single(choice => choice.Id == definition.Id);
            Require(model.CommandTargetPreview.Contains("ID 1", StringComparison.Ordinal) &&
                !model.CommandWirePreview.Contains("ID ", StringComparison.Ordinal), "Selected preset preview target/wire separation failed.");
            await ScenarioButton(model.ExecuteCommand);
            _scenarioWrites += definition.Steps.Count;
            var snapshot = ScenarioMaster.GetCommandSnapshot();
            Require(snapshot.CommandId == definition.Id && snapshot.State == ModbusCommandState.Verified &&
                snapshot.Steps.All(step => step.WriteAcknowledged && step.State == ModbusCommandStepState.Verified),
                "Saved preset did not complete all actual result checks: " + definition.Id);
            Require(model.RecentCommandCaption.Contains(definition.Name, StringComparison.Ordinal), "Recent result lost the actual preset name.");
            var check = expected[definition.Id["nucleo-f103rb-".Length..]];
            Require((await CommandRead(check.ReadFunction, check.Address, check.Values.Length)).SequenceEqual(check.Values),
                "Independent post-command read differs: " + definition.Id);
            if (definition.Id == "nucleo-f103rb-ram-sequence")
                Require(snapshot.Steps.Select(step => step.ObservedValues![0]).SequenceEqual(new ushort[] { 6, 7, 15 }),
                    "Sequential readback values differ from 6,7,15.");
            Require(unchecked(await CommandWriteCounter() - _scenarioInitialWrites) == _scenarioWrites,
                "A preset wrote more or fewer times than its explicit step count.");
            await CommandEvidence("preset-" + definition.Id, new { definition, snapshot, model.RecentCommandCaption,
                model.WriteStatus, model.WriteError, model.Status, expectedReadback = check.Values });
        }
        Require(model.WriteStatus.Length == 0 && model.WriteError.Length == 0,
            "Command results leaked into the direct-write editor after profile reload.");
        // Decode the actual final FC06 ACK captured from the board, while keeping
        // RequestOrResponse classification. This is still not a separate readback.
        var echoed = Frames(CommandRx(evidence.Entries(_case))).First(packet => packet.AsSpan().SequenceEqual(Single(6, 0x1010, 15)));
        var frame = new ModbusRtuFrame(1, 1, DateTimeOffset.UtcNow, ProtocolDirection.Rx,
            echoed, ModbusFrameKind.RequestOrResponse, ModbusCrcStatus.Valid);
        var interpreter = new ModbusValueInterpreterViewModel();
        interpreter.SetFrame(frame);
        Require(interpreter.EditorVisibility == Microsoft.UI.Xaml.Visibility.Visible && interpreter.ValuesText == "+0: 15",
            "Actual FC06 response did not expose its known value.");
        await CommandEvidence("fc06-value", new { frame.Kind, interpreter.Help, interpreter.ValuesText, hex = Convert.ToHexString(echoed) });
    }
}
