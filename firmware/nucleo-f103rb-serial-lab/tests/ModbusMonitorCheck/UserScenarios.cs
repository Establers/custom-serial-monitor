using System.Diagnostics;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;
using static Oracle;

internal sealed partial class LiveCheck
{
    private ModbusRtuMonitorService? _scenarioMaster;
    private ModbusRtuViewModel? _scenarioModel;
    private ushort[] _scenarioRam = [], _scenarioCoils = [];
    private uint _scenarioInitialWrites;
    private int _scenarioWrites, _scenarioSaves;
    private ModbusRtuMonitorService ScenarioMaster => _scenarioMaster ?? throw new InvalidOperationException("Scenario service not initialized.");
    private ModbusRtuViewModel ScenarioModel => _scenarioModel ?? throw new InvalidOperationException("Scenario model not initialized.");

    public async Task RunUserScenarios()
    {
        _serial.CaptureBytesReceived += evidence.Rx;
        _serial.Error += evidence.Error;
        await CommandConnect(650);
        await using var master = Master();
        _scenarioMaster = master;
        var model = new ModbusRtuViewModel(master,
            () => _serial.IsConnected && _serial.ExclusiveSessionOwner is null,
            SaveScenarioProfile, () => { });
        _scenarioModel = model;
        Add("scenario-identity-and-preserved-baseline", async () =>
        {
            await CommandIdentity(); // Exact reviewed BUILD and ID before any write/fault setup.
            await CommandText("RTU FAULT CLEAR", "OK RTU FAULT CLEAR\r\n");
            await CommandText("RTU", "OK RTU\r\n");
            _scenarioRam = await CommandRead(3, Bank, 4);
            _scenarioCoils = await CommandRead(1, Bank, 16);
            _scenarioInitialWrites = await CommandWriteCounter();
            await ScenarioEvidence("baseline");
        });
        Add("scenario-offline-setup-and-profile-no-transmit", async () =>
        {
            var written = _serial.WrittenByteCount;
            model.ApplySettings(ScenarioSettings(3, Bank, 4));
            model.ApplyItemDefinitions([new() { Address = Bank + 2, Name = "Setpoint", ValueType = ModbusValueType.Int16,
                Scale = 0.1, Unit = "C", Access = ModbusItemAccess.ReadWrite }]);
            await ScenarioButton(model.SaveSettingsCommand);
            Require(_scenarioSaves == 1 && _serial.WrittenByteCount == written && !model.IsBusy, "Setup/save unexpectedly transmitted or failed.");
            await ScenarioEvidence("setup");
        });
        Add("scenario-read-once", async () =>
        {
            model.ApplySettings(ScenarioSettings(3, 0, 2));
            await ScenarioButton(model.ReadOnceCommand);
            Require(model.Rows.Count == 2 && model.Rows[0].RawValues!.SequenceEqual(new ushort[] { 250 }) &&
                model.Rows[1].RawValues!.SequenceEqual(new ushort[] { 100 }) && !model.IsBusy, "Read-once rows do not match the board.");
            await ScenarioEvidence("read");
        });
        Add("scenario-raw-multiple-write-and-readback", async () =>
        {
            model.ApplySettings(ScenarioSettings(3, Bank, 4));
            model.WriteAddressText = Bank.ToString(); model.WriteValuesText = "123 456";
            Require(model.WritePreview.Contains("0x007B", StringComparison.Ordinal), "Raw write preview missing.");
            await ScenarioButton(model.WriteCommand);
            _scenarioWrites++;
            Require(master.GetSnapshot().WriteState == ModbusWriteState.Verified &&
                (await CommandRead(3, Bank, 2)).SequenceEqual(new ushort[] { 123, 456 }), "Multiple raw write/readback failed.");
            await ScenarioEvidence("write");
        });
        Add("scenario-engineering-item-write", async () =>
        {
            model.ApplyItemDefinitions([new() { Address = Bank + 2, Name = "Setpoint", ValueType = ModbusValueType.Int16,
                Scale = 0.1, Unit = "C", Access = ModbusItemAccess.ReadWrite }]);
            model.WriteModeIndex = 1; model.WriteAddressText = (Bank + 2).ToString(); model.WriteValuesText = "25.3";
            Require(model.WritePreview.Contains("0x00FD", StringComparison.Ordinal), "Engineering preview did not encode 25.3 as raw253.");
            await ScenarioButton(model.WriteCommand);
            _scenarioWrites++;
            Require(master.GetSnapshot().WriteState == ModbusWriteState.Verified &&
                (await CommandRead(3, Bank + 2, 1))[0] == 253, "Interpreted write/readback failed.");
            await ScenarioEvidence("engineering");
        });
        Add("scenario-direct-coil-led-write-and-readback", async () =>
        {
            model.ApplySettings(ScenarioSettings(1, Bank, 16));
            model.WriteModeIndex = 0; model.WriteAddressText = Bank.ToString();
            foreach (var value in new ushort[] { 1, 0 })
            {
                model.WriteValuesText = value.ToString();
                await ScenarioButton(model.WriteCommand); _scenarioWrites++;
                Require(master.GetSnapshot().WriteState == ModbusWriteState.Verified &&
                    (await CommandRead(2, Bank + 1, 1))[0] == value && (await CommandRead(4, Bank + 7, 1))[0] == value,
                    "Direct coil write and independent LED status disagree.");
            }
            await ScenarioEvidence("coil");
        });
        Add("scenario-read-only-no-write", async () =>
        {
            model.ApplySettings(ScenarioSettings(4, Bank, 6));
            var written = _serial.WrittenByteCount;
            Require(!model.WriteCommand.CanExecute(null), "Input registers offered a write action.");
            model.WriteCommand.Execute(null);
            await Task.Delay(100, CommandToken);
            Require(_serial.WrittenByteCount == written, "Disabled write action sent bytes.");
        });
        Add("scenario-five-groups-error-selection-and-stop", async () =>
        {
            model.ApplyPollingSettings(ScenarioGroups(includeException: true));
            try
            {
                model.StartCommand.Execute(null);
                await ScenarioUntil(() => master.GetPollingSnapshot().Groups.Count == 5 &&
                    master.GetPollingSnapshot().Groups.Take(4).All(group => group.Snapshot.SuccessCount >= 3) &&
                    master.GetPollingSnapshot().Groups[4].Snapshot.State == ModbusMonitorState.Error);
                Require(model.PollingGroups[4].HasError && model.PollingHealthSummary.Contains("Bad address", StringComparison.Ordinal),
                    "An unselected group's real exception is absent from the aggregate error summary.");
                var earlier = model.PollingGroups[0].Snapshot!.Values!;
                var tick = UInt32(earlier, 0);
                model.SelectedPollingGroup = model.PollingGroups[1];
                Require(model.UnitText == "1" && model.AddressText == Bank.ToString() && model.Rows[0].RawValues![0] == 123,
                    "Selecting RAM did not show that group's values/target.");
                await ScenarioUntil(() => UInt32(master.GetPollingSnapshot().Groups[0].Snapshot.Values!, 0) > tick);
                await ScenarioEvidence("polling");
                await StopScenarioPolling();
                Require(master.GetPollingSnapshot().Groups.Take(4).All(group => group.Snapshot.SuccessCount >= 3), "Healthy groups did not continue.");
            }
            finally { await master.StopAsync(); }
        });
        Add("scenario-polling-queued-write-atomic-command-resume", async () =>
        {
            model.ApplyPollingSettings(ScenarioGroups(includeException: false));
            var command = Definition(LedStep(1, 4, Bank + 7, 1), LedStep(0, 2, Bank + 1, 0));
            command.Name = "LED ON then OFF";
            model.ApplyCommandSettings(new() { SelectedCommandId = command.Id, Commands = [command] });
            try
            {
                model.StartCommand.Execute(null);
                await ScenarioUntil(() => master.GetPollingSnapshot().Groups.Count == 4 && master.GetPollingSnapshot().Groups.All(group => group.Snapshot.SuccessCount >= 2));
                model.SelectedPollingGroup = model.PollingGroups[1];
                model.WriteModeIndex = 0; model.WriteAddressText = Bank.ToString(); model.WriteValuesText = "999";
                await ScenarioButton(model.WriteCommand);
                _scenarioWrites++;
                Require(master.GetPollingSnapshot().Groups[1].Snapshot.WriteState == ModbusWriteState.Verified, "Queued group write was not verified.");
                await ScenarioButton(model.ExecuteCommand);
                _scenarioWrites += 2;
                Require(master.GetCommandSnapshot().State == ModbusCommandState.Verified && master.GetSnapshot().IsPolling && model.IsBusy,
                    "Command did not verify and return to polling.");
                var counts = master.GetPollingSnapshot().Groups.Select(group => group.Snapshot.SuccessCount).ToArray();
                await ScenarioUntil(() => master.GetPollingSnapshot().Groups.Select((group, index) => group.Snapshot.SuccessCount > counts[index]).All(value => value));
                await StopScenarioPolling();
                var tx = CommandTx(evidence.Entries(_case));
                var first = Array.FindIndex(tx, packet => packet.AsSpan().SequenceEqual(Single(5, Bank, 1)));
                var expected = new[] { Single(5, Bank, 1), Read(4, Bank + 7, 1), Single(5, Bank, 0), Read(2, Bank + 1, 1) };
                Require(first >= 0 && first + expected.Length <= tx.Length, "Atomic command exchanges missing.");
                for (var index = 0; index < expected.Length; index++)
                    Require(tx[first + index].AsSpan().SequenceEqual(expected[index]), "Polling interleaved inside the command transaction.");
                Require(model.RecentCommandResults.Length > 0 && (await CommandRead(3, Bank, 1))[0] == 999, "Results/queued RAM write lost.");
                await ScenarioEvidence("resumed");
            }
            finally { await master.StopAsync(); }
        });
        Add("scenario-ack-only-and-preserved-recent-result", async () =>
        {
            model.ApplySettings(ScenarioSettings(3, Bank, 4));
            var command = Definition(Step(Bank, "77", ModbusCommandVerificationMode.AcknowledgementOnly));
            command.Name = "ACK only RAM";
            var other = Definition(Step(Bank + 3, "99")); other.Name = "Not executed";
            model.ApplyCommandSettings(new() { SelectedCommandId = command.Id, Commands = [command, other] });
            await ScenarioButton(model.ExecuteCommand);
            _scenarioWrites++;
            Require(master.GetCommandSnapshot().State == ModbusCommandState.Acknowledged && master.GetCommandSnapshot().Steps[0].WriteAcknowledged,
                "ACK-only command incorrectly reported verified completion.");
            var recent = model.RecentCommandSummary;
            model.SelectedCommand = model.CommandChoices[1];
            await ScenarioButton(model.ReadOnceCommand);
            Require(model.RecentCommandSummary == recent && model.RecentCommandResults.Length > 0 && model.Rows[0].RawValues![0] == 77,
                "Browsing another command/read reassigned or erased the real recent result.");
            await ScenarioEvidence("ack");
        });
        Add("scenario-condition-failure-skips-next-write", async () =>
        {
            var before = (await CommandRead(3, Bank + 3, 1))[0];
            var step = LedStep(0, 4, Bank + 7, 1); step.Verification.TimeoutMs = 350; step.Verification.IntervalMs = 350;
            var command = Definition(step, Step(Bank + 3, "51966"));
            model.ApplyCommandSettings(new() { SelectedCommandId = command.Id, Commands = [command] });
            await ScenarioButton(model.ExecuteCommand, allowError: true);
            _scenarioWrites++;
            Require(master.GetCommandSnapshot().State == ModbusCommandState.NotVerified && master.GetCommandSnapshot().Steps[1].State == ModbusCommandStepState.Canceled &&
                _serial.IsConnected && (await CommandRead(3, Bank + 3, 1))[0] == before, "Condition failure continued or misclassified the command.");
            await ScenarioEvidence("not-verified");
        });
        Add("scenario-stop-during-status-check", async () =>
        {
            var before = (await CommandRead(3, Bank + 3, 1))[0];
            var step = LedStep(0, 4, Bank + 7, 1); step.Verification.TimeoutMs = 3000;
            var command = Definition(step, Step(Bank + 3, "48879"));
            model.ApplyCommandSettings(new() { SelectedCommandId = command.Id, Commands = [command] });
            model.ExecuteCommand.Execute(null);
            await ScenarioUntil(() => master.GetCommandSnapshot().Steps.Count > 0 && master.GetCommandSnapshot().Steps[0].ObservedValues is { Length: 1 });
            model.StopCommand.Execute(null);
            await ScenarioUntil(() => !model.IsBusy && model.ExecuteCommand.CanExecute(null));
            _scenarioWrites++;
            Require(master.GetCommandSnapshot().State == ModbusCommandState.Canceled && master.GetCommandSnapshot().Steps[0].WriteAcknowledged &&
                master.GetCommandSnapshot().Steps[1].State == ModbusCommandStepState.Canceled && _serial.ExclusiveSessionOwner is null &&
                (await CommandRead(3, Bank + 3, 1))[0] == before, "Stop lost ACK, failed to release ownership, or sent the next write.");
            await ScenarioEvidence("stopped");
        });
        Add("scenario-restoration-and-final-quiet-state", async () =>
        {
            await CommandRawWrite(16, Bank, _scenarioRam); _scenarioWrites++;
            await CommandRawWrite(15, Bank, _scenarioCoils); _scenarioWrites++;
            Require((await CommandRead(3, Bank, 4)).SequenceEqual(_scenarioRam) && (await CommandRead(1, Bank, 16)).SequenceEqual(_scenarioCoils), "Original RAM/coils not restored.");
            Require((await CommandRead(4, Bank + 7, 1))[0] == _scenarioCoils[0] && (await CommandRead(2, Bank + 1, 1))[0] == _scenarioCoils[0], "LED state readbacks do not match the restored coil.");
            var writes = await CommandWriteCounter();
            Require(unchecked(writes - _scenarioInitialWrites) == _scenarioWrites, "Firmware write counter differs from the exact planned writes (duplicate/missing write).");
            await CommandText("LAB", "OK LAB\r\n");
            var status = await CommandTextValue("RTU STATUS?");
            Require(status.Contains("fault=NONE", StringComparison.Ordinal) && status.Contains("pending=0", StringComparison.Ordinal), "Final board state not quiet.");
            await CommandText("RTU", "OK RTU\r\n");
            await CommandEvidence("restoration", new { ram = _scenarioRam, coils = _scenarioCoils, initialWrites = _scenarioInitialWrites,
                finalWrites = writes, expectedWriteDelta = _scenarioWrites, status, resetIssued = false, privateProfileSaves = _scenarioSaves });
        });
        foreach (var item in _cases) evidence.Results.Add(new(item.Name, "not_run"));
        for (var index = 0; index < _cases.Count; index++)
        {
            _case = _cases[index].Name; evidence.Select(_case);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20)); _commandCaseToken = deadline.Token;
            try
            {
                await _cases[index].Run(); evidence.RequireComplete();
                if (!_case.Contains("identity", StringComparison.Ordinal) && !_case.Contains("restoration", StringComparison.Ordinal))
                    CheckScenarioExchanges(_case);
                evidence.Results[index] = new(_case, "pass"); Console.WriteLine("PASS User Scenario " + _case);
            }
            catch (Exception ex) { evidence.Results[index] = new(_case, "fail", ex.ToString()); throw; }
            finally { _commandCaseToken = default; }
        }
    }

    private void CheckScenarioExchanges(string name)
    {
        var entries = evidence.Entries(name);
        var tx = CommandTx(entries); var rx = Frames(CommandRx(entries));
        Require(tx.Length == rx.Count, "Scenario TX/RX frame count differs: " + name);
        for (var index = 0; index < tx.Length; index++)
        {
            if ((rx[index][1] & 0x80) != 0)
                Require(tx[index].AsSpan().SequenceEqual(Read(3, 0x2000, 1)) && rx[index].AsSpan().SequenceEqual(Adu(1, 0x83, 2)), "Unexpected exception response.");
            else if (tx[index][1] <= 4) _ = Values(tx[index], rx[index]);
            else Require(Crc(tx[index]) == 0 && rx[index].AsSpan().SequenceEqual(Ack(tx[index])), "Scenario write/ACK mismatch.");
        }
    }

    private static ModbusMonitorSettings ScenarioSettings(byte function, int address, int quantity) => new()
    { UnitId = 1, Function = function, Address = address, Quantity = quantity, IntervalMs = 100, TimeoutMs = 1000 };
    private static ModbusPollingSettings ScenarioGroups(bool includeException)
    {
        var counters = ScenarioSettings(4, Bank, 6); counters.ValueType = ModbusValueType.UInt32;
        var settings = new ModbusPollingSettings { SelectedGroupId = "counter", Groups =
        [new() { Id = "counter", Name = "Counters", Settings = counters },
         new() { Id = "ram", Name = "RAM", Settings = ScenarioSettings(3, Bank, 4) },
         new() { Id = "led", Name = "LED", Settings = ScenarioSettings(1, Bank, 16) },
         new() { Id = "pattern", Name = "Patterns", Settings = ScenarioSettings(4, Bank + 8, 6) }] };
        if (includeException) settings.Groups.Add(new() { Id = "bad", Name = "Bad address", Settings = ScenarioSettings(3, 0x2000, 1) });
        return settings;
    }
    private async Task ScenarioUntil(Func<bool> condition, int timeoutMs = 8000) =>
        await CommandUntil(() => { ScenarioModel.Refresh(); return condition(); }, timeoutMs);
    private async Task ScenarioButton(AsyncRelayCommand command, bool allowError = false)
    {
        Require(command.CanExecute(null), "Scenario action disabled before invocation.");
        command.Execute(null);
        await ScenarioUntil(() => command.CanExecute(null));
        Require(allowError || ScenarioModel.Error.Length == 0, "ViewModel action failed: " + ScenarioModel.Error);
    }
    private async Task StopScenarioPolling()
    {
        Require(ScenarioModel.StopCommand.CanExecute(null), "Stop unavailable during actual polling.");
        ScenarioModel.StopCommand.Execute(null);
        await ScenarioUntil(() => !ScenarioModel.IsBusy && ScenarioModel.StartCommand.CanExecute(null));
        Require(_serial.ExclusiveSessionOwner is null && _serial.IsConnected, "Stop did not settle/release the connection.");
        var bytes = _serial.WrittenByteCount;
        await Task.Delay(250, CommandToken);
        Require(_serial.WrittenByteCount == bytes, "Polling/queued TX continued after Stop.");
    }
    private async Task SaveScenarioProfile()
    {
        _scenarioSaves++;
        await CommandEvidence("private-profile-" + _scenarioSaves, new { monitor = ScenarioModel.CreatePersistableSettingsSnapshot(),
            groups = ScenarioModel.CreatePersistablePollingSettingsSnapshot(), commands = ScenarioModel.CreatePersistableCommandSettingsSnapshot() });
    }
    private Task ScenarioEvidence(string suffix) => CommandEvidence(suffix, new
    {
        ScenarioModel.Status, ScenarioModel.Error, ScenarioModel.RequestSummary, ScenarioModel.IsBusy,
        ScenarioModel.PollingHealthSummary, ScenarioModel.RecentCommandSummary, ScenarioModel.RecentCommandResults,
        selectedGroup = ScenarioModel.SelectedPollingGroup?.Id,
        rows = ScenarioModel.Rows.Select(row => new { row.Address, row.RawValues, row.RawText, row.ValueText, row.StatusText }).ToArray(),
        monitor = ScenarioMaster.GetSnapshot(), groups = ScenarioMaster.GetPollingSnapshot(), command = ScenarioMaster.GetCommandSnapshot()
    });
}
