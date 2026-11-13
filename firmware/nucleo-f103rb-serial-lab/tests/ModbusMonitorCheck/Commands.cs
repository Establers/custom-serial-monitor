using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using static Oracle;

internal sealed partial class LiveCheck
{
    private ushort[] _originalCommandRam = [], _originalCommandCoils = [], _expectedCommandCoils = [];
    private uint _initialCommandWriteCounter;
    private CancellationToken _commandCaseToken;
    private CancellationToken CommandToken => _commandCaseToken == default ? token : _commandCaseToken;
    private static readonly ushort[] CommandSentinel = [0x1111, 0x2222, 0x3333, 0x4444];
    private static readonly JsonSerializerOptions CommandJson = new()
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public async Task RunCommands()
    {
        _serial.CaptureBytesReceived += evidence.Rx;
        _serial.Error += evidence.Error;
        await CommandConnect(650);
        Add("command-build-identity", async () =>
        {
            await CommandText("LAB", "OK LAB\r\n");
            await CommandIdentity();
            await CommandText("RTU FAULT CLEAR", "OK RTU FAULT CLEAR\r\n");
            await CommandText("RTU", "OK RTU\r\n");
        });
        Add("command-baseline-no-reset", async () =>
        {
            _originalCommandRam = await CommandRead(3, Bank, 4);
            _originalCommandCoils = await CommandRead(1, Bank, 16);
            _expectedCommandCoils = _originalCommandCoils.ToArray();
            _initialCommandWriteCounter = await CommandWriteCounter();
            var led = await CommandRead(4, Bank + 7, 1);
            Require(led[0] == _originalCommandCoils[0], "Baseline LED command state disagrees with coil0.");
            await CommandEvidence("baseline", new { manifest, ram = _originalCommandRam, coils = _originalCommandCoils, writeCounter = _initialCommandWriteCounter, resetIssued = false });
        });
        Add("command-acknowledgement-only", async () =>
        {
            var before = await PrepareCommandBank();
            var command = Definition(Step(Bank, "77", ModbusCommandVerificationMode.AcknowledgementOnly));
            var result = await ActualCommand(command, ModbusCommandState.Acknowledged);
            CheckCommandExchanges(result, [Single(6, Bank, 77)]);
            Require(result.Snapshot.Steps[0].WriteAcknowledged && result.Snapshot.Steps[0].State == ModbusCommandStepState.Acknowledged,
                "ACK-only was mislabeled as physical verification.");
            await CheckCommandEffects(before, 1, [77, CommandSentinel[1], CommandSentinel[2], CommandSentinel[3]]);
        });
        Add("command-float32-readback-and-restore", async () =>
        {
            var before = await PrepareCommandBank();
            var command = Definition(Step(Bank, "1.5", ModbusCommandVerificationMode.ReadBack));
            command.Steps[0].Item.ValueType = ModbusValueType.Float32;
            var result = await ActualCommand(command, ModbusCommandState.Verified);
            CheckCommandExchanges(result, [Multiple(16, Bank, [0x3FC0, 0]), Read(3, Bank, 2)], [[0x3FC0, 0]]);
            await CheckCommandEffects(before, 1, [0x3FC0, 0, CommandSentinel[2], CommandSentinel[3]]);
            // Only after observing the typed write, restore this known pair explicitly.
            await CommandRawWrite(16, Bank, CommandSentinel[..2]);
            await CheckCommandEffects(before, 2, CommandSentinel);
        });
        Add("command-sequence-6-7-15", async () =>
        {
            var before = await PrepareCommandBank();
            var command = Definition(Step(Bank, "6"), Step(Bank, "7"), Step(Bank, "15"));
            var result = await ActualCommand(command, ModbusCommandState.Verified);
            CheckCommandExchanges(result,
                [Single(6, Bank, 6), Read(3, Bank, 1), Single(6, Bank, 7), Read(3, Bank, 1), Single(6, Bank, 15), Read(3, Bank, 1)],
                [[6], [7], [15]]);
            await CheckCommandEffects(before, 3, [15, CommandSentinel[1], CommandSentinel[2], CommandSentinel[3]]);
        });
        Add("command-led-on-input-register-status", async () =>
        {
            var before = await PrepareCommandBank();
            var command = Definition(LedStep(1, 4, Bank + 7, 1));
            var result = await ActualCommand(command, ModbusCommandState.Verified);
            CheckCommandExchanges(result, [Single(5, Bank, 1), Read(4, Bank + 7, 1)], [[1]]);
            _expectedCommandCoils[0] = 1;
            await CheckCommandEffects(before, 1, CommandSentinel);
        });
        Add("command-led-off-discrete-status", async () =>
        {
            var before = await PrepareCommandBank();
            var command = Definition(LedStep(0, 2, Bank + 1, 0));
            var result = await ActualCommand(command, ModbusCommandState.Verified);
            CheckCommandExchanges(result, [Single(5, Bank, 0), Read(2, Bank + 1, 1)], [[0]]);
            _expectedCommandCoils[0] = 0;
            await CheckCommandEffects(before, 1, CommandSentinel);
        });
        Add("command-condition-never-met-no-next-step", CommandConditionFailure);
        Add("command-known-exception-no-next-step", async () =>
        {
            var before = await PrepareCommandBank();
            var command = Definition(Step(0x2000, "6", ModbusCommandVerificationMode.AcknowledgementOnly), Step(Bank + 3, "15"));
            var result = await ActualCommand(command, ModbusCommandState.Failed, expectFailure: true);
            var tx = CommandTx(result.Entries);
            var rx = CommandRx(result.Entries);
            Require(tx.Length == 1 && tx[0].AsSpan().SequenceEqual(Single(6, 0x2000, 6)) && rx.AsSpan().SequenceEqual(Adu(1, 0x86, 2)),
                "Known illegal-address response/wire was not exact exception02.");
            Require(_serial.IsConnected && !result.Snapshot.Steps[0].WriteAcknowledged && result.Snapshot.Steps[1].State == ModbusCommandStepState.Canceled,
                "Known exception falsely acknowledged or ran next step.");
            await CheckCommandEffects(before, 0, CommandSentinel);
        });
        Add("command-stop-during-delayed-ack", CommandStopDuringAck);
        Add("command-stop-during-status-loop", CommandStopDuringStatus);
        Add("command-drop-write-commits-once", () => CommandFault("DROP"));
        Add("command-crc-write-commits-once", () => CommandFault("CRC"));
        Add("command-delayed-write-timeout-late-ack", () => CommandFault("DELAY 1200"));
        Add("command-active-polling-atomic-fair-resume", CommandWhilePolling);
        Add("final-baseline-restored", async () =>
        {
            var before = await CommandWriteCounter();
            await CommandRawWrite(16, Bank, _originalCommandRam);
            await CommandRawWrite(15, Bank, _originalCommandCoils);
            Require((await CommandRead(3, Bank, 4)).SequenceEqual(_originalCommandRam), "Original RAM was not restored.");
            Require((await CommandRead(1, Bank, 16)).SequenceEqual(_originalCommandCoils), "Original coils/LED command state were not restored.");
            Require((await CommandRead(4, Bank + 7, 1))[0] == _originalCommandCoils[0] &&
                (await CommandRead(2, Bank + 1, 1))[0] == _originalCommandCoils[0], "Restored LED command-state indicators disagree.");
            var after = await CommandWriteCounter();
            Require(unchecked(after - before) == 2, "Restoration issued unexpected extra writes.");
            await CommandText("LAB", "OK LAB\r\n");
            await CommandText("RTU FAULT CLEAR", "OK RTU FAULT CLEAR\r\n");
            await CommandIdentity();
            var status = await CommandTextValue("RTU STATUS?");
            Require(status.Contains("pending=0", StringComparison.Ordinal) && status.Contains("fault=NONE", StringComparison.Ordinal), "Final fault state not quiet: " + status);
            await CommandText("RTU", "OK RTU\r\n");
            await CommandEvidence("restoration", new { ram = _originalCommandRam, coils = _originalCommandCoils,
                initialWriteCounter = _initialCommandWriteCounter, finalWriteCounter = after,
                totalWriteDelta = unchecked(after - _initialCommandWriteCounter), firmwareCounterRestored = false, resetIssued = false });
        });
        foreach (var item in _cases) evidence.Results.Add(new(item.Name, "not_run"));
        for (var index = 0; index < _cases.Count; index++)
        {
            _case = _cases[index].Name;
            evidence.Select(_case);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            _commandCaseToken = deadline.Token;
            try
            {
                await _cases[index].Run();
                evidence.RequireComplete();
                evidence.Results[index] = new(_case, "pass");
                Console.WriteLine("PASS Commands Live " + _case);
            }
            catch (Exception ex) { evidence.Results[index] = new(_case, "fail", ex.ToString()); throw; }
            finally { _commandCaseToken = default; }
        }
    }

    private async Task CommandIdentity()
    {
        Require(manifest.Version == "1.7.1" && string.Equals(manifest.BuildId, "22F524185C55784C", StringComparison.OrdinalIgnoreCase),
            "Command mutation tests require the explicitly reviewed firmware1.7.1/build22F524185C55784C manifest.");
        await CommandText("BUILD?", $"BUILD version={manifest.Version} protocol=1 id={manifest.BuildId}\r\n");
        await CommandText("ID?", "ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1\r\n");
    }

    private async Task<ushort[]> CommandRead(byte function, int address, int count)
    {
        var request = Read(function, address, count);
        return Values(request, await CommandRaw(request));
    }
    private async Task<uint> CommandWriteCounter() => UInt32(await CommandRead(4, Bank + 2, 2), 0);
    private async Task CommandRawWrite(byte function, int address, ushort[] values)
    {
        var request = function is 5 or 6 ? Single(function, address, values[0]) : Multiple(function, address, values);
        Require((await CommandRaw(request)).AsSpan().SequenceEqual(Ack(request)), "Explicit known raw write acknowledgement mismatch; no retry.");
    }
    private async Task<uint> PrepareCommandBank()
    {
        await CommandRawWrite(16, Bank, CommandSentinel);
        Require((await CommandRead(3, Bank, 4)).SequenceEqual(CommandSentinel), "Command sentinel setup not observed.");
        Require((await CommandRead(1, Bank, 16)).SequenceEqual(_expectedCommandCoils), "Unexpected coil/LED mutation before command.");
        return await CommandWriteCounter();
    }
    private async Task CheckCommandEffects(uint before, uint writes, ushort[] ram)
    {
        Require((await CommandRead(3, Bank, 4)).SequenceEqual(ram), "Independent RAM/sentinel check failed.");
        Require((await CommandRead(1, Bank, 16)).SequenceEqual(_expectedCommandCoils), "Independent coil/LED preservation check failed.");
        var after = await CommandWriteCounter();
        Require(unchecked(after - before) == writes, $"Independent firmware write count delta {unchecked(after - before)} != {writes}; no automatic write retry is allowed.");
        await CommandEvidence("effects-" + writes, new { beforeWriteCounter = before, afterWriteCounter = after, delta = unchecked(after - before), expectedWrites = writes, ram, coils = _expectedCommandCoils });
    }

    private async Task<CommandCapture> ActualCommand(ModbusCommandDefinition definition, ModbusCommandState expected,
        bool expectFailure = false, bool expectCanceled = false, Action<byte[]>? transmitted = null,
        Func<ModbusRtuMonitorService, Task, Task>? control = null)
    {
        var begin = evidence.Entries(_case).Length;
        var generation = _serial.ReceiveSessionGeneration;
        var master = new ModbusRtuMonitorService(_serial, (bytes, session) => { evidence.Tx(bytes, session); transmitted?.Invoke(bytes); });
        Task? running = null;
        Exception? error = null;
        try
        {
            running = master.ExecuteCommandAsync(definition, CommandToken);
            if (control is not null) await control(master, running);
            await running.WaitAsync(TimeSpan.FromSeconds(8), CommandToken);
        }
        catch (OperationCanceledException ex) when (expectCanceled && !CommandToken.IsCancellationRequested) { error = ex; }
        catch (Exception ex) when (expectFailure && ex is not OperationCanceledException and not TimeoutException) { error = ex; }
        catch (Exception ex) { error = ex; throw; }
        finally
        {
            // Observe task-completion/ownership contract before Dispose can hide a premature completion.
            var settled = master.GetCommandSnapshot();
            var wasTaskSettled = running?.IsCompleted == true;
            var ownerAtTaskSettlement = _serial.ExclusiveSessionOwner;
            var busyAtTaskSettlement = master.IsBusy;
            try
            {
                if (wasTaskSettled && (error is null || expectFailure || expectCanceled))
                    Require(settled.State == expected && !settled.IsBusy && !busyAtTaskSettlement && ownerAtTaskSettlement is null,
                        $"Command Task settled before expected {expected} state/ownership release; Dispose cleanup cannot count as engine success.");
            }
            finally
            {
                await master.DisposeAsync();
                if (running is not null) await RecordRun(running);
                await CommandEvidence("command", new { definition, snapshot = settled, expectedState = expected, beginEntry = begin,
                    endEntry = evidence.Entries(_case).Length, generation, wasTaskSettled, ownerAtTaskSettlement, busyAtTaskSettlement, error = error?.ToString() });
            }
        }
        var result = new CommandCapture(master.GetCommandSnapshot(), evidence.Entries(_case).Skip(begin).ToArray());
        Require(result.Snapshot.State == expected && !result.Snapshot.IsBusy && !master.IsBusy && _serial.ExclusiveSessionOwner is null,
            $"Command state/ownership expected {expected}, got {result.Snapshot.State}.");
        Require((expectFailure || expectCanceled) == (error is not null), "Unexpected command Task completion classification.");
        return result;
    }

    private static ModbusCommandDefinition Definition(params ModbusCommandStep[] steps) =>
        new() { Id = Guid.NewGuid().ToString("N"), Name = "UART command validation", Steps = steps.ToList() };
    private static ModbusCommandStep Step(int address, string value, ModbusCommandVerificationMode mode = ModbusCommandVerificationMode.ReadBack) =>
        new() { Name = "Write " + address, UnitId = 1, Function = 3, Item = new() { Address = address, Access = ModbusItemAccess.ReadWrite }, Value = value,
            TimeoutMs = 1000, Verification = new() { Mode = mode, Function = 3, Address = address, Mask = ushort.MaxValue, ExpectedValue = 0, TimeoutMs = 1000, IntervalMs = 100 } };
    private static ModbusCommandStep LedStep(ushort value, byte statusFunction, int statusAddress, ushort expected)
    {
        var step = Step(Bank, value.ToString(System.Globalization.CultureInfo.InvariantCulture), ModbusCommandVerificationMode.Status);
        step.Function = 1;
        step.Verification.Function = statusFunction;
        step.Verification.Address = statusAddress;
        step.Verification.Mask = 1;
        step.Verification.ExpectedValue = expected;
        return step;
    }
    private async Task CommandEvidence(string suffix, object value)
    {
        await using var stream = new FileStream(Path.Combine(evidence.Directory, _case + "." + suffix + ".json"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read, 8192, true);
        await JsonSerializer.SerializeAsync(stream, value, CommandJson, CancellationToken.None);
    }

    private async Task<byte[]> CommandRaw(byte[] bytes, int waitMs = 180)
    {
        var before = evidence.Entries(_case).Length;
        evidence.Tx(bytes, _serial.ReceiveSessionGeneration);
        await _serial.SendBytesAsync(bytes, Convert.ToHexString(bytes), CommandToken);
        await Task.Delay(waitMs, CommandToken);
        evidence.RequireComplete();
        return CommandRx(evidence.Entries(_case).Skip(before).ToArray());
    }
    private async Task<string> CommandTextValue(string command) =>
        Encoding.ASCII.GetString(await CommandRaw(Encoding.ASCII.GetBytes(command + "\r\n")));
    private async Task CommandText(string command, string expected) =>
        Require(await CommandTextValue(command) == expected, "Command LAB/identity response mismatch: " + command);
    private async Task CommandArm(string fault)
    {
        await CommandText("LAB", "OK LAB\r\n");
        await CommandText("RTU FAULT " + fault, "OK RTU FAULT " + fault + "\r\n");
        await CommandText("RTU", "OK RTU\r\n");
    }
    private async Task CommandConnect(int drainMs)
    {
        await _serial.ConnectAsync(new() { PortName = port, BaudRate = 115200, DataBits = 8, Parity = SerialParityMode.None,
            StopBits = SerialStopBitsMode.One, Handshake = SerialHandshakeMode.None },
            new() { UseNativeIdleTimeout = true, IdleTimeoutMs = 40 }, CommandToken);
        var reader = _serial.ReceivedBytes;
        var lifetime = token;
        _drains.Add(Task.Run(async () =>
        {
            try { await foreach (var _ in reader.ReadAllAsync(lifetime)) { } }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }));
        await Task.Delay(drainMs, CommandToken);
    }
    private async Task CommandUntil(Func<bool> condition, int timeoutMs = 8000)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            CommandToken.ThrowIfCancellationRequested();
            Require(Stopwatch.GetElapsedTime(started).TotalMilliseconds < timeoutMs, "Command validator state wait expired; no TX retry.");
            await Task.Delay(5, CommandToken);
        }
    }
    private static byte[][] CommandTx(WireEntry[] entries) => entries.Where(entry => entry.Direction == "TX").Select(entry => entry.Bytes).ToArray();
    private static byte[] CommandRx(WireEntry[] entries) => entries.Where(entry => entry.Direction == "RX").SelectMany(entry => entry.Bytes).ToArray();
    private static void CheckCommandExchanges(CommandCapture capture, byte[][] expected, ushort[][]? expectedReads = null)
    {
        var tx = CommandTx(capture.Entries);
        var frames = Frames(CommandRx(capture.Entries));
        Require(tx.Length == expected.Length && frames.Count == tx.Length, "Command TX/RX counts differ from independent plan.");
        var readIndex = 0;
        for (var index = 0; index < tx.Length; index++)
        {
            Require(tx[index].AsSpan().SequenceEqual(expected[index]), "Command exact TX target/value/order mismatch at " + index);
            if (tx[index][1] <= 4)
            {
                var values = Values(tx[index], frames[index]);
                Require(expectedReads is not null && values.SequenceEqual(expectedReads[readIndex++]), "Command independent status/readback value mismatch.");
            }
            else Require(frames[index].AsSpan().SequenceEqual(Ack(expected[index])), "Command independent write ACK mismatch.");
        }
        Require(expectedReads is null || readIndex == expectedReads.Length, "Unused expected read payloads.");
    }
    private sealed record CommandCapture(ModbusCommandSnapshot Snapshot, WireEntry[] Entries);

    private async Task CommandConditionFailure()
    {
        var before = await PrepareCommandBank();
        var step = LedStep(0, 4, Bank + 7, 1);
        step.Verification.TimeoutMs = 350;
        step.Verification.IntervalMs = 350; // One full valid sample, then wait budget; no tiny final reply timeout.
        var command = Definition(step, Step(Bank + 3, "15"));
        var result = await ActualCommand(command, ModbusCommandState.NotVerified, expectFailure: true);
        CheckStatusLoopWire(result, Single(5, Bank, 0), Read(4, Bank + 7, 1), 0);
        Require(_serial.IsConnected && result.Snapshot.Steps[0].WriteAcknowledged && result.Snapshot.Steps[1].State == ModbusCommandStepState.Canceled,
            "Condition failure disconnected, forgot acknowledgement, or ran next step.");
        _expectedCommandCoils[0] = 0;
        await CheckCommandEffects(before, 1, CommandSentinel);
    }
    private static void CheckStatusLoopWire(CommandCapture capture, byte[] expectedWrite, byte[] expectedRead, ushort expectedValue)
    {
        var tx = CommandTx(capture.Entries);
        var frames = Frames(CommandRx(capture.Entries));
        Require(tx.Length >= 2 && frames.Count == tx.Length && tx[0].AsSpan().SequenceEqual(expectedWrite) &&
            frames[0].AsSpan().SequenceEqual(Ack(expectedWrite)), "Status-loop initial write/ACK missing or changed.");
        for (var index = 1; index < tx.Length; index++)
            Require(tx[index].AsSpan().SequenceEqual(expectedRead) && Values(tx[index], frames[index]).SequenceEqual(new[] { expectedValue }),
                "Status-loop sent another write/target or observed unexpected raw status.");
    }

    private async Task CommandStopDuringAck()
    {
        var before = await PrepareCommandBank();
        await CommandArm("DELAY 300"); // No addressed read may consume the armed delay before the command.
        var commandBegin = evidence.Entries(_case).Length;
        var writtenBeforeCommand = _serial.WrittenByteCount;
        var expectedRequest = Single(6, Bank, 6);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = Definition(Step(Bank, "6", ModbusCommandVerificationMode.AcknowledgementOnly), Step(Bank + 3, "15"));
        var result = await ActualCommand(command, ModbusCommandState.Canceled, expectCanceled: true,
            transmitted: _ => sent.TrySetResult(), control: async (master, running) =>
            {
                await sent.Task.WaitAsync(TimeSpan.FromSeconds(2), CommandToken);
                Require(CommandRx(evidence.Entries(_case).Skip(commandBegin).ToArray()).Length == 0,
                    "Delayed ACK arrived before the control task could request Stop; case cannot prove drain behavior.");
                var written = _serial.WrittenByteCount;
                var rejected = false;
                try { await _serial.SendBytesAsync(Read(3, 0, 2), "rejected command-owner probe", CommandToken); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected && _serial.WrittenByteCount == written, "Competing TX was not rejected without bytes.");
                var stopping = master.StopAsync(); // Control continuation; never called inside TX/RX callback.
                await Task.Delay(20, CommandToken);
                Require(!stopping.IsCompleted && !running.IsCompleted && _serial.ExclusiveSessionOwner is not null, "Stop released ownership before delayed ACK drained.");
                await stopping.WaitAsync(TimeSpan.FromSeconds(2), CommandToken);
                Require(running.IsCompleted && !master.IsBusy && _serial.ExclusiveSessionOwner is null &&
                    CommandRx(evidence.Entries(_case).Skip(commandBegin).ToArray()).AsSpan().SequenceEqual(Ack(expectedRequest)),
                    "Stop returned before the command task, full ACK and owner release settled.");
                await Task.Delay(80, CommandToken);
                Require(_serial.WrittenByteCount - writtenBeforeCommand == expectedRequest.Length &&
                    CommandTx(evidence.Entries(_case).Skip(commandBegin).ToArray()).Length == 1,
                    "A rejected competing request or next command step was transmitted after Stop.");
                await CommandEvidence("control", new { competingTxRejected = rejected, rejectedTxBytes = 0, ownerRetainedWhileStopping = true });
            });
        CheckCommandExchanges(result, [Single(6, Bank, 6)]);
        Require(result.Snapshot.Steps[0].WriteAcknowledged && result.Snapshot.Steps[1].State == ModbusCommandStepState.Canceled, "Stop lost ACK or sent next step.");
        await CheckCommandEffects(before, 1, [6, CommandSentinel[1], CommandSentinel[2], CommandSentinel[3]]);
    }

    private async Task CommandStopDuringStatus()
    {
        var before = await PrepareCommandBank();
        var step = LedStep(0, 4, Bank + 7, 1);
        step.Verification.TimeoutMs = 3000;
        var command = Definition(step, Step(Bank + 3, "15"));
        var result = await ActualCommand(command, ModbusCommandState.Canceled, expectCanceled: true,
            control: async (master, _) =>
            {
                await CommandUntil(() => master.GetCommandSnapshot().Steps[0].ObservedValues is { Length: 1 }, 1500);
                await master.StopAsync().WaitAsync(TimeSpan.FromSeconds(2), CommandToken);
            });
        CheckStatusLoopWire(result, Single(5, Bank, 0), Read(4, Bank + 7, 1), 0);
        Require(result.Snapshot.Steps[0].WriteAcknowledged && result.Snapshot.Steps[1].State == ModbusCommandStepState.Canceled && _serial.IsConnected,
            "Status stop acknowledgement/remaining-step/connection mismatch.");
        _expectedCommandCoils[0] = 0;
        await CheckCommandEffects(before, 1, CommandSentinel);
    }

    private async Task CommandFault(string fault)
    {
        var before = await PrepareCommandBank();
        await CommandArm(fault);
        var expected = Single(6, Bank, 0x55AA);
        var command = Definition(Step(Bank, "0x55AA", ModbusCommandVerificationMode.AcknowledgementOnly), Step(Bank + 3, "15"));
        command.Steps[0].TimeoutMs = 150;
        var generation = _serial.ReceiveSessionGeneration;
        var result = await ActualCommand(command, ModbusCommandState.Failed, expectFailure: true);
        Require(!_serial.IsConnected && _serial.ExclusiveSessionOwner is null && !result.Snapshot.Steps[0].WriteAcknowledged &&
            result.Snapshot.Steps[1].State == ModbusCommandStepState.Canceled, "Uncertain write did not disconnect/fail without ACK/next step.");
        var tx = CommandTx(result.Entries);
        var rx = CommandRx(result.Entries);
        Require(tx.Length == 1 && tx[0].AsSpan().SequenceEqual(expected), "Fault write was retried or retargeted.");
        if (fault == "CRC")
        {
            var corrupt = Ack(expected);
            corrupt[^1] ^= 1;
            Require(rx.AsSpan().SequenceEqual(corrupt) && Crc(rx) != 0, "CRC-fault ACK differs from exact independent corruption.");
        }
        else Require(rx.Length == 0, "DROP/delayed-timeout unexpectedly received response before retirement.");
        var reconnectBegin = evidence.Entries(_case).Length;
        await CommandConnect(fault.StartsWith("DELAY", StringComparison.Ordinal) ? 1450 : 400);
        Require(_serial.ReceiveSessionGeneration > generation, "Fault recovery did not replace the serial session.");
        var quiet = evidence.Entries(_case).Skip(reconnectBegin).ToArray();
        Require(CommandTx(quiet).Length == 0, "Automatic TX occurred during reconnect/late-response observation.");
        var late = CommandRx(quiet);
        Require(fault.StartsWith("DELAY", StringComparison.Ordinal) ? late.AsSpan().SequenceEqual(Ack(expected)) : late.Length == 0,
            "Late ACK was not completely observed before LAB, or unexpected stale RX appeared.");
        await CommandEvidence("recovery", new { fault, oldGeneration = generation, newGeneration = _serial.ReceiveSessionGeneration,
            lateAckHex = Convert.ToHexString(late), noAutomaticTx = true, labSentAfterLateAckObservation = true });
        await CommandText("LAB", "OK LAB\r\n");
        await CommandText("RTU FAULT CLEAR", "OK RTU FAULT CLEAR\r\n");
        await CommandIdentity();
        await CommandText("RTU", "OK RTU\r\n");
        await CheckCommandEffects(before, 1, [0x55AA, CommandSentinel[1], CommandSentinel[2], CommandSentinel[3]]);
    }

    private async Task CommandWhilePolling()
    {
        var before = await PrepareCommandBank();
        var start = evidence.Entries(_case).Length;
        await using var master = Master();
        var settings = new ModbusPollingSettings { SelectedGroupId = "ram", Groups =
        [new() { Id = "ram", Name = "RAM", Settings = Settings(3, Bank, 4) },
            new() { Id = "status", Name = "LED state", Settings = Settings(4, Bank + 7, 1) }] };
        var polling = master.RunGroupsAsync(settings, CommandToken);
        var command = Definition(Step(Bank, "6"), Step(Bank, "7"), Step(Bank, "15"));
        try
        {
            await CommandUntil(() => master.GetPollingSnapshot().Groups.All(group => group.Snapshot.SuccessCount >= 2));
            await master.ExecuteCommandAsync(command, CommandToken).WaitAsync(TimeSpan.FromSeconds(5), CommandToken);
            var counts = master.GetPollingSnapshot().Groups.Select(group => group.Snapshot.SuccessCount).ToArray();
            await CommandUntil(() => master.GetPollingSnapshot().Groups.Select((group, index) => group.Snapshot.SuccessCount > counts[index]).All(value => value));
            await master.StopAsync();
            Require(polling.IsCompleted && !master.IsBusy && _serial.ExclusiveSessionOwner is null,
                "Polling Stop returned before its operation settled and released ownership; Dispose cannot mask this failure.");
            await polling;
            var entries = evidence.Entries(_case).Skip(start).ToArray();
            var tx = CommandTx(entries);
            var frames = Frames(CommandRx(entries));
            Require(tx.Length == frames.Count, "Polling/command TX/RX mismatch.");
            var first = Array.FindIndex(tx, request => request[1] == 6);
            Require(first >= 0 && tx.Count(request => request[1] == 6) == 3, "Polling command write count differs from3.");
            var expected = new[] { Single(6, Bank, 6), Read(3, Bank, 1), Single(6, Bank, 7), Read(3, Bank, 1), Single(6, Bank, 15), Read(3, Bank, 1) };
            Require(first + expected.Length <= tx.Length, "Atomic command evidence is truncated.");
            for (var index = 0; index < expected.Length; index++)
                Require(tx[first + index].AsSpan().SequenceEqual(expected[index]), "A poll/write interleaved inside the atomic command sequence.");
            var ram = CommandSentinel.ToArray();
            var writeIndex = 0;
            var values = new ushort[] { 6, 7, 15 };
            for (var index = 0; index < tx.Length; index++)
            {
                var request = tx[index];
                if (request[1] == 6)
                {
                    Require(request.AsSpan().SequenceEqual(Single(6, Bank, values[writeIndex])) &&
                        frames[index].AsSpan().SequenceEqual(Ack(request)), "Polling command write/ACK target or value changed.");
                    ram[0] = values[writeIndex++];
                }
                else if (request.AsSpan().SequenceEqual(Read(3, Bank, 4)))
                    Require(Values(request, frames[index]).SequenceEqual(ram), "Polling RAM values disagree with independently tracked command state.");
                else if (request.AsSpan().SequenceEqual(Read(3, Bank, 1)))
                    Require(index > first && index < first + 6 && Values(request, frames[index]).SequenceEqual(new[] { ram[0] }), "Command readback target/value differs from expected sequence.");
                else
                    Require(request.AsSpan().SequenceEqual(Read(4, Bank + 7, 1)) &&
                        Values(request, frames[index]).SequenceEqual(new[] { _expectedCommandCoils[0] }), "Unexpected polling request or LED status.");
            }
            Require(tx.Skip(first + 6).Any(request => request.AsSpan().SequenceEqual(Read(3, Bank, 4))) &&
                tx.Skip(first + 6).Any(request => request.AsSpan().SequenceEqual(Read(4, Bank + 7, 1))), "Both due groups did not resume fairly after command.");
            Require(master.GetCommandSnapshot().State == ModbusCommandState.Verified, "Polling command not verified.");
            await CommandEvidence("command", new { definition = command, snapshot = master.GetCommandSnapshot(), polling = master.GetPollingSnapshot(), beginEntry = start,
                endEntry = evidence.Entries(_case).Length, atomicStartTxIndex = first });
        }
        finally { await master.StopAsync(); await RecordRun(polling); }
        await CheckCommandEffects(before, 3, [15, CommandSentinel[1], CommandSentinel[2], CommandSentinel[3]]);
    }
}
