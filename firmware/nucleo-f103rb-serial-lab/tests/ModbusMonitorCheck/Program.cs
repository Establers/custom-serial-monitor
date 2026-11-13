using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using static Oracle;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Evidence? evidence = null;
        BuildManifest? manifest = null;
        Exception? failure = null;
        var startedAtUtc = DateTimeOffset.UtcNow;
        var clockAnchorTimestamp = Stopwatch.GetTimestamp();
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
        try
        {
            if (args is ["offline", var manifestPath, var sequencePath])
            {
                manifest = BuildManifest.Load(manifestPath);
                await Offline(sequencePath, stop.Token);
                Console.WriteLine($"PASS Offline: independent fixtures + GUI v3 import + manifest {manifest.Version}/{manifest.BuildId}; hardwareVerified=false");
                return 0;
            }
            if (args is not ["live", _, _, _, _]) throw new ArgumentException("offline MANIFEST GUI_V3_FILE | live PORT MANIFEST NEW_EVIDENCE_DIR GUI_V3_FILE");
            Require(Regex.IsMatch(args[1], @"^COM[1-9][0-9]*$", RegexOptions.IgnoreCase), "Live requires an explicit Windows COM port.");
            var directory = Path.GetFullPath(args[3]);
            Require(!Directory.Exists(directory) && !File.Exists(directory), "Evidence directory must be new; existing evidence is never overwritten.");
            Directory.CreateDirectory(directory);
            evidence = new Evidence(directory);
            manifest = BuildManifest.Load(args[2]);
            await using var live = new LiveCheck(args[1], manifest, evidence, args[4], stop.Token);
            await live.Run();
            return 0;
        }
        catch (Exception ex) { failure = ex; Console.Error.WriteLine(ex); return 1; }
        finally
        {
            if (evidence is not null)
            {
                var passed = failure is null && evidence.Results.Count > 0 && evidence.Results.All(result => result.Status == "pass");
                await evidence.SaveAsync(new
                {
                    passed, hardwareVerified = passed, error = failure?.ToString(), manifest,
                    port = args[1], baud = 115200, serialFormat = "8N1 / no handshake", byteTimeoutMs = 40,
                    startedAtUtc, completedAtUtc = DateTimeOffset.UtcNow, clockAnchorTimestamp,
                    completedCases = evidence.Results.Count(result => result.Status == "pass"),
                    failedCases = evidence.Results.Count(result => result.Status == "fail"),
                    serialServiceSha256 = Hash(typeof(SerialService).Assembly.Location),
                    validatorSha256 = Hash(typeof(Program).Assembly.Location),
                    coreAssemblySha256 = Hash(typeof(SequenceRunSession).Assembly.Location),
                    guiSequencePath = Path.GetFullPath(args[4]), guiSequenceSha256 = File.Exists(args[4]) ? Hash(args[4]) : null,
                    failureCleanup = "Disconnect only; firmware state is reset/left quiet only by the final-clean-state PASS case. After any failure inspect evidence and explicitly recover the known fixture; no uncertain write is replayed.",
                    scope = "Direct UART through actual SerialService and ModbusRtuMonitorService; exact GUI file replayed twice through actual CommandSequenceRunner and linked production SequenceRunObserver plus actual core SequenceRunSession/RX matcher, with independent full-wire oracle. GUI controls/rendering, physical button actuation and electrical RS-485 timing are not exercised. Headless validation never retries TX."
                }, passed);
                Console.WriteLine($"Evidence: {evidence.Directory}; passed={passed}; completed={evidence.Results.Count(result => result.Status == "pass")}");
            }
        }
    }

    private static async Task Offline(string sequencePath, CancellationToken token)
    {
        Require(Adu(1, 3, 0, 0, 0, 2).AsSpan().SequenceEqual(Convert.FromHexString("010300000002C40B")), "Independent CRC known vector.");
        Require(Reply(3, 250, 100).AsSpan().SequenceEqual(Convert.FromHexString("01030400FA0064DBE9")), "Independent reply known vector.");
        foreach (var function in new byte[] { 1, 2, 3, 4 })
        {
            var count = function <= 2 ? 2000 : 125;
            var request = Read(function, 0x1000, count);
            Require(request.AsSpan().SequenceEqual(ModbusRtuRequestBuilder.Read(1, function, 0x1000, count)), "Builder differs from independent read oracle.");
            var values = Enumerable.Range(0, count).Select(index => (ushort)(function <= 2 ? index % 2 : index)).ToArray();
            var response = Reply(function, values);
            Require(Values(request, response).AsSpan().SequenceEqual(values), "Independent maximum-size fixture.");
            var decoded = ModbusResponseDecoder.DecodeRead(new ModbusReadRequest(1, function, 0x1000, count), response);
            Require(decoded.Status == ModbusResponseStatus.Success && decoded.Values.AsSpan().SequenceEqual(values), "Application maximum read decoder differs from oracle.");
        }
        foreach (var function in new byte[] { 5, 6 })
            Require(Single(function, 0x1000, 1).AsSpan().SequenceEqual(ModbusRtuRequestBuilder.WriteSingle(1, function, 0x1000, 1)), "Single-write builder differs from oracle.");
        foreach (var function in new byte[] { 15, 16 })
        {
            var count = function == 15 ? 1968 : 123;
            var values = Enumerable.Range(0, count).Select(index => (ushort)(function == 15 ? index % 2 : index + 1)).ToArray();
            var request = Multiple(function, 0x1000, values);
            Require(request.AsSpan().SequenceEqual(ModbusRtuRequestBuilder.WriteMultiple(1, function, 0x1000, values)), "Maximum write builder differs from oracle.");
            Require(ModbusResponseDecoder.DecodeWrite(request, Ack(request)).Status == ModbusResponseStatus.Success, "Maximum write acknowledgement decoder.");
        }
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(sequencePath, token)))
            Require(document.RootElement.GetProperty("FormatVersion").GetInt32() == 3, "Import file must be GUI format v3.");
        var imported = await new CommandSequenceFileService().LoadAsync([Path.GetFullPath(sequencePath)], token);
        Require(imported.Count == 1 && imported[0].Steps.Count == 52 && imported[0].RepeatCount == 1 &&
            imported[0].Steps.Count(step => step.Kind == CommandSequenceStepKind.Tx) == 26 &&
            imported[0].Steps.Count(step => step.Kind == CommandSequenceStepKind.RxCheck) == 26, "Extended GUI sequence import contract: 52 steps, 26 TX, 26 RX Check.");
        foreach (var step in imported[0].Steps.Where(step => step.Kind == CommandSequenceStepKind.Tx && step.TxMode == TxSendMode.Hex))
        {
            var bytes = Convert.FromHexString(string.Concat(step.CommandText.Where(character => !char.IsWhiteSpace(character))));
            Require(Crc(bytes) == 0, "GUI TX has invalid independent CRC: " + step.Name);
        }
        Console.WriteLine($"PASS Offline protocol boundaries and {imported[0].Steps.Count} GUI v3 steps (import only; not executed)");
    }
}

internal sealed partial class LiveCheck(string port, BuildManifest manifest, Evidence evidence, string sequencePath, CancellationToken token) : IAsyncDisposable
{
    private readonly SerialService _serial = new();
    private readonly List<Task> _drains = [];
    private readonly List<(string Name, Func<Task> Run)> _cases = [];
    private string _case = "preflight";
    private const int Bank = 0x1000;

    public async Task Run()
    {
        await LoadGuiSequence();
        _serial.CaptureBytesReceived += evidence.Rx;
        _serial.Error += evidence.Error;
        await Connect(650);
        Add("build-identity", async () =>
        {
            await Text("BUILD?", $"BUILD version={manifest.Version} protocol=1 id={manifest.BuildId}\r\n");
            await Text("ID?", "ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1\r\n");
        });
        Add("legacy-lab", async () =>
        {
            await Text("LAB", "OK LAB\r\n");
            await Text("PING", "PONG\r\n");
            await Text("SELFTEST", $"SELFTEST PASS crc=PASS fixtures=PASS limits=PASS build={manifest.BuildId}\r\n");
            await Reset();
        });
        Add("legacy-read-holding", () => ReadKnown(3, 0, [250, 100]));
        Add("legacy-read-input", () => ReadKnown(4, 0, [250, 100]));
        Add("legacy-mstp", async () =>
        {
            var response = await Raw(Convert.FromHexString("55FF0301020003C84142432F9F"));
            Require(response.AsSpan().SequenceEqual(Convert.FromHexString("55FF04020100036C4142432F9F")), "Existing BACnet MS/TP reply changed.");
        });
        Add("known-pattern-formats", Patterns);
        Add("single-register-write", async () => { await WriteChecked(3, Bank, [1234]); await ReadKnown(3, Bank, [1234]); });
        Add("multiple-register-write", async () => { await WriteChecked(3, Bank, [111, 222, 333]); await ReadKnown(3, Bank, [111, 222, 333]); });
        Add("register-boundaries", async () =>
        {
            var values = Enumerable.Range(0, 123).Select(index => (ushort)(0x2000 + index)).ToArray();
            await WriteChecked(3, Bank, values, 125);
            var read = await EngineRead(3, Bank, 125);
            Require(read.Values!.Take(123).SequenceEqual(values) && read.Values!.Length == 125, "123-write / 125-read boundary values.");
        });
        Add("single-coil-led", async () =>
        {
            await WriteChecked(1, Bank, [1]); await ReadKnown(1, Bank, [1]);
            await ReadKnown(4, Bank + 7, [1]);
            await WriteChecked(1, Bank, [0]); await ReadKnown(4, Bank + 7, [0]);
        });
        Add("coil-boundaries", async () =>
        {
            var values = Enumerable.Range(0, 1968).Select(index => (ushort)(index % 2)).ToArray();
            await WriteChecked(1, Bank, values, 2000);
            var read = await EngineRead(1, Bank, 2000);
            Require(read.Values!.Take(values.Length).SequenceEqual(values) && read.Values!.Length == 2000, "1968-write / 2000-read boundary values.");
        });
        Add("input-discrete-boundaries", async () =>
        {
            var input = await EngineRead(4, Bank, 125);
            Require(input.Values!.Length == 125, "Input register maximum read.");
            var discrete = await EngineRead(2, Bank, 2000);
            Require(discrete.Values!.Length == 2000 && discrete.Values[0] <= 1 && discrete.Values[1] <= 1 && discrete.Values.Skip(2).All(value => value == 0), "Discrete inputs include only button and LED bits.");
        });
        Add("counters-read-only", Counters);
        Add("polling-ownership-105-responses", Polling);
        foreach (var fault in new[] { "DROP", "CRC", "TRUNCATE", "WRONGID", "SPLIT 80" })
        {
            var selected = fault;
            Add("fault-" + selected.Replace(' ', '-').ToLowerInvariant(), () => Fault(selected));
        }
        Add("fault-delay-100", async () =>
        {
            await Arm("DELAY 100");
            var result = await EngineRead(3, 0, 2, 1000);
            Require(result.Values!.SequenceEqual(new ushort[] { 250, 100 }) && result.ResponseMilliseconds >= 80 && result.ResponseMilliseconds < 1000, "Delayed response was not observed within timeout.");
        });
        Add("write-drop-commits-once", WriteDrop);
        Add("gui-sequence-replay-round-1", () => ReplayGuiSequence(1));
        Add("gui-sequence-replay-round-2", () => ReplayGuiSequence(2));
        Add("final-clean-state", async () =>
        {
            await Reset(); await ReadKnown(3, 0, [250, 100]);
            await Text("LAB", "OK LAB\r\n");
            var status = await TextValue("STATUS?");
            Require(status.Contains("rx_drops=0 uart_errors=0 tx_rejected=0", StringComparison.Ordinal), "Firmware transport counters: " + status);
            await Text("RTU", "OK RTU\r\n");
        });
        foreach (var item in _cases) evidence.Results.Add(new(item.Name, "not_run"));
        for (var index = 0; index < _cases.Count; index++)
        {
            _case = _cases[index].Name; evidence.Select(_case);
            try
            {
                await _cases[index].Run(); evidence.RequireComplete();
                evidence.Results[index] = new(_case, "pass");
                Console.WriteLine("PASS Live " + _case);
            }
            catch (Exception ex) { evidence.Results[index] = new(_case, "fail", ex.ToString()); throw; }
        }
    }

    private void Add(string name, Func<Task> run) => _cases.Add((name, run));
    private static ModbusMonitorSettings Settings(byte function, int address, int count, int timeout = 1000) => new()
    { UnitId = 1, Function = function, Address = address, Quantity = count, IntervalMs = 100, TimeoutMs = timeout };
    private ModbusRtuMonitorService Master() => new(_serial, evidence.Tx);
    private async Task<ModbusMonitorSnapshot> EngineRead(byte function, int address, int count, int timeout = 1000)
    {
        var before = evidence.Entries(_case).Length;
        await using var master = Master();
        await master.RunAsync(Settings(function, address, count, timeout), false, token).WaitAsync(TimeSpan.FromSeconds(8), token);
        var snapshot = master.GetSnapshot();
        var entries = evidence.Entries(_case).Skip(before).ToArray();
        var tx = entries.Where(entry => entry.Direction == "TX").Select(entry => entry.Bytes).ToArray();
        var raw = entries.Where(entry => entry.Direction == "RX").SelectMany(entry => entry.Bytes).ToArray();
        Require(tx.Length == 1 && tx[0].AsSpan().SequenceEqual(Read(function, address, count)), "Engine request differs from independent oracle.");
        var independent = Values(tx[0], raw);
        Require(snapshot.ValuesCurrent && snapshot.Values is not null && snapshot.Values.AsSpan().SequenceEqual(independent), "Engine values differ from raw response oracle.");
        return snapshot;
    }
    private async Task ReadKnown(byte function, int address, ushort[] values)
    {
        var snapshot = await EngineRead(function, address, values.Length);
        Require(snapshot.Values!.SequenceEqual(values), "Known bank value mismatch.");
    }
    private async Task WriteChecked(byte readFunction, int address, ushort[] values, int? readCount = null)
    {
        var before = evidence.Entries(_case).Length;
        await using var master = Master();
        await master.WriteAsync(Settings(readFunction, address, readCount ?? values.Length), address, values, token).WaitAsync(TimeSpan.FromSeconds(8), token);
        Require(master.GetSnapshot().WriteState == ModbusWriteState.Verified && master.GetSnapshot().ValuesCurrent, "Engine write was not read-back verified.");
        var entries = evidence.Entries(_case).Skip(before).ToArray();
        var tx = entries.Where(entry => entry.Direction == "TX").Select(entry => entry.Bytes).ToArray();
        var frames = Frames(entries.Where(entry => entry.Direction == "RX").SelectMany(entry => entry.Bytes).ToArray());
        var function = readFunction == 1 ? values.Length == 1 ? (byte)5 : (byte)15 : values.Length == 1 ? (byte)6 : (byte)16;
        var expected = values.Length == 1 ? Single(function, address, values[0]) : Multiple(function, address, values);
        Require(tx.Length == 2 && tx[0].AsSpan().SequenceEqual(expected) &&
            tx[1].AsSpan().SequenceEqual(Read(readFunction, address, readCount ?? values.Length)) &&
            frames.Count == 2 && frames[0].AsSpan().SequenceEqual(Ack(expected)), "Write request/acknowledgement/readback target differs from independent oracle.");
        var read = Values(tx[1], frames[1]);
        Require(read.Take(values.Length).SequenceEqual(values), "Independent readback differs from written values.");
    }
    private async Task Patterns()
    {
        var start = evidence.Entries(_case).Length;
        await ReadKnown(3, Bank + 4, [0xFF85, 250, 0x1234, 0x5678, 0x41C8, 0]);
        var bytes = evidence.Entries(_case).Skip(start).Where(entry => entry.Direction == "RX").SelectMany(entry => entry.Bytes).ToArray();
        var frame = new ModbusRtuFrame(1, _serial.ReceiveSessionGeneration, DateTimeOffset.Now, ProtocolDirection.Rx, bytes, ModbusFrameKind.Response, ModbusCrcStatus.Valid);
        Require(ModbusRegisterInterpreter.Interpret(frame, ModbusValueType.Int16).Values[0].Value == -123, "Signed16 interpretation.");
        Require(Math.Abs(ModbusRegisterInterpreter.Interpret(frame, ModbusValueType.Int16, scale: 0.1).Values[0].Value + 12.3) < 1e-10, "Scaled signed16 interpretation.");
        Require(ModbusRegisterInterpreter.Interpret(frame, ModbusValueType.UInt32, 2).Values[0].Value == 0x12345678, "UInt32 interpretation.");
        Require(ModbusRegisterInterpreter.Interpret(frame, ModbusValueType.Float32, 4).Values[0].Value == 25, "Float32 interpretation.");
        Require(ModbusRegisterInterpreter.Interpret(frame, ModbusValueType.UInt16, swapBytes: true).Values[0].Value == 0x85FF, "Byte swap interpretation.");
        Require(ModbusRegisterInterpreter.Interpret(frame, ModbusValueType.UInt32, 2, swapWords: true).Values[0].Value == 0x56781234, "Word swap interpretation.");
    }
    private async Task Counters()
    {
        await Reset();
        var first = (await EngineRead(4, Bank, 14)).Values!;
        Require(UInt32(first, 2) == 0 && UInt32(first, 4) == 1 && first[6] <= 1 && first[7] == 0, "Initial counters/button/LED.");
        await WriteChecked(3, Bank, [123]);
        await Task.Delay(30, token);
        var next = (await EngineRead(4, Bank, 14)).Values!;
        Require(UInt32(next, 0) > UInt32(first, 0) && UInt32(next, 2) == 1 && UInt32(next, 4) == 3, "Uptime/write/read counters including current read.");
        await using var master = Master();
        var written = _serial.WrittenByteCount;
        var rejected = false;
        try { await master.WriteAsync(Settings(4, Bank, 2), Bank, [1], token); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected && _serial.WrittenByteCount == written && !master.IsBusy, "Read-only input was accepted or transmitted a write.");
    }
    private async Task Polling()
    {
        await Reset();
        var begin = evidence.Entries(_case).Length;
        var writtenAtStart = _serial.WrittenByteCount;
        await using var master = Master();
        var settings = Settings(3, Bank, 4);
        var run = master.RunAsync(settings, true, token);
        try
        {
            await Until(() => master.GetSnapshot().SuccessCount >= 3);
            var rejected = false;
            try { await _serial.SendBytesAsync(Read(3, 0, 2), "competing normal send", token); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "Normal TX was not rejected during polling.");
            var sequence = new CommandSequence { Name = "Exclusive-owner regression", Steps = [new() { CommandText = "read" }] };
            rejected = false;
            try
            {
                await new CommandSequenceRunner().RunAsync(sequence, _ => Task.FromResult(_serial.IsConnected),
                    async (_, cancellation) => { await _serial.SendBytesAsync(Read(3, 0, 2), "sequence TX", cancellation); return true; },
                    () => false, _ => { }, _ => { }, token);
            }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "Existing sequence writer was not rejected during polling.");
            await master.WriteAsync(settings, Bank + 1, [4321], token).WaitAsync(TimeSpan.FromSeconds(8), token);
            Require(master.GetSnapshot().WriteState == ModbusWriteState.Verified, "Queued polling write was not verified.");
            await Until(() => master.GetSnapshot().SuccessCount >= 105, 30000);
            await master.StopAsync().WaitAsync(TimeSpan.FromSeconds(8), token);
            await run;
            Require(_serial.ExclusiveSessionOwner is null && !master.IsBusy && _serial.IsConnected, "Stop did not settle/release healthy polling ownership.");
            var entries = evidence.Entries(_case).Skip(begin).ToArray();
            var tx = entries.Where(entry => entry.Direction == "TX").Select(entry => entry.Bytes).ToArray();
            var frames = Frames(entries.Where(entry => entry.Direction == "RX").SelectMany(entry => entry.Bytes).ToArray());
            Require(tx.Length == frames.Count && tx.Count(request => request[1] == 3) >= 100 && tx.Count(request => request[1] == 6) == 1, "Polling wire counts / single queued write.");
            Require(_serial.WrittenByteCount - writtenAtStart == tx.Sum(request => request.Length), "Unexpected competing TX bytes during polling ownership.");
            for (var index = 0; index < tx.Length; index++)
                if (tx[index][1] == 3)
                {
                    Require(tx[index].AsSpan().SequenceEqual(Read(3, Bank, 4)), "Polling read target/function changed.");
                    _ = Values(tx[index], frames[index]);
                }
                else Require(tx[index].AsSpan().SequenceEqual(Single(6, Bank + 1, 4321)) &&
                    frames[index].AsSpan().SequenceEqual(Ack(tx[index])), "Polling queued-write target/ACK.");
            Require(master.GetSnapshot().Values![1] == 4321, "Polling table did not retain new value.");
            var raw = await Raw(Read(3, 0, 2));
            Require(raw.AsSpan().SequenceEqual(Reply(3, 250, 100)), "Normal read after polling stop.");
            Console.WriteLine($"  polling responses={tx.Count(request => request[1] == 3)}; queuedWrites=1; exclusive conflicts rejected=2");
        }
        finally { await master.StopAsync(); await RecordRun(run); }
    }
    private async Task Fault(string fault)
    {
        await Arm(fault);
        var begin = evidence.Entries(_case).Length;
        await using var master = Master();
        var failed = false;
        try { await master.RunAsync(Settings(3, 0, 2, 250), false, token).WaitAsync(TimeSpan.FromSeconds(8), token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { failed = true; }
        Require(failed && !master.IsBusy && !_serial.IsConnected && _serial.ExclusiveSessionOwner is null, "Fault did not stop and disconnect before releasing ownership: " + fault);
        Require(!master.GetSnapshot().ValuesCurrent && master.GetSnapshot().ErrorCount == 1, "Fault exposed current values or wrong error count.");
        var entries = evidence.Entries(_case).Skip(begin).ToArray();
        var tx = entries.Where(entry => entry.Direction == "TX").ToArray();
        var rx = entries.Where(entry => entry.Direction == "RX").SelectMany(entry => entry.Bytes).ToArray();
        Require(tx.Length == 1 && tx[0].Bytes.AsSpan().SequenceEqual(Read(3, 0, 2)), "Fault retried or sent unexpected request.");
        var expected = Reply(3, 250, 100);
        if (fault == "DROP") Require(rx.Length == 0, "DROP emitted response bytes.");
        else if (fault == "CRC") Require(rx.Length == expected.Length && Crc(rx) != 0 && rx.AsSpan(0, 7).SequenceEqual(expected.AsSpan(0, 7)), "CRC fault raw evidence.");
        else if (fault == "WRONGID") Require(rx.Length == expected.Length && rx[0] != 1 && Crc(rx) == 0, "WRONGID fault raw evidence.");
        else Require(rx.Length > 0 && rx.Length < expected.Length && expected.AsSpan(0, rx.Length).SequenceEqual(rx), "Truncate/split fault prefix evidence.");
        await Recover();
    }
    private async Task WriteDrop()
    {
        await Reset(); await Arm("DROP");
        var begin = evidence.Entries(_case).Length;
        await using var master = Master();
        var failed = false;
        try { await master.WriteAsync(Settings(3, Bank, 2, 250), Bank, [0x55AA], token).WaitAsync(TimeSpan.FromSeconds(8), token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { failed = true; }
        Require(failed && !_serial.IsConnected && _serial.ExclusiveSessionOwner is null, "Dropped ACK write did not fail closed.");
        var entries = evidence.Entries(_case).Skip(begin).ToArray();
        Require(entries.Count(entry => entry.Direction == "TX") == 1 && entries.All(entry => entry.Direction != "RX"), "Dropped ACK write was retried or returned a response.");
        await Connect(400);
        await Text("LAB", "OK LAB\r\n");
        await Text("RTU FAULT CLEAR", "OK RTU FAULT CLEAR\r\n");
        var status = await TextValue("RTU STATUS?");
        Require(Regex.IsMatch(status, @"\bwrites=1\b") && status.Contains("pending=0", StringComparison.Ordinal), "Dropped ACK must commit exactly once: " + status);
        await Text("RTU", "OK RTU\r\n");
        await ReadKnown(3, Bank, [0x55AA]);
        await Text("LAB", "OK LAB\r\n");
        status = await TextValue("RTU STATUS?");
        Require(Regex.IsMatch(status, @"\bwrites=1\b"), "Reopen/read caused duplicate write commit: " + status);
        await Text("RTU", "OK RTU\r\n");
    }
    private async Task Arm(string fault)
    {
        await Text("LAB", "OK LAB\r\n");
        await Text("RTU FAULT " + fault, "OK RTU FAULT " + fault + "\r\n");
        await Text("RTU", "OK RTU\r\n");
    }
    private async Task Reset()
    {
        await Text("LAB", "OK LAB\r\n");
        await Text("RTU RESET", "OK RTU RESET\r\n");
        await Text("RTU", "OK RTU\r\n");
    }
    private async Task Recover()
    {
        // Known one-shot faults are never retried. Reopen, observe/drain any
        // scheduled tail, then explicitly cancel/reset firmware state in LAB.
        await Connect(400); await Reset();
    }
    private async Task Connect(int drainMs)
    {
        await _serial.ConnectAsync(new() { PortName = port, BaudRate = 115200, DataBits = 8, Parity = SerialParityMode.None,
            StopBits = SerialStopBitsMode.One, Handshake = SerialHandshakeMode.None }, new() { UseNativeIdleTimeout = true, IdleTimeoutMs = 40 }, token);
        var reader = _serial.ReceivedBytes;
        _drains.Add(Task.Run(async () => { try { await foreach (var _ in reader.ReadAllAsync(token)) { } } catch (OperationCanceledException) when (token.IsCancellationRequested) { } }));
        await Task.Delay(drainMs, token);
    }
    private async Task<byte[]> Raw(byte[] bytes, int waitMs = 180)
    {
        var before = evidence.Entries(_case).Length;
        evidence.Tx(bytes, _serial.ReceiveSessionGeneration);
        await _serial.SendBytesAsync(bytes, Convert.ToHexString(bytes), token);
        await Task.Delay(waitMs, token);
        evidence.RequireComplete();
        return evidence.Entries(_case).Skip(before).Where(entry => entry.Direction == "RX").SelectMany(entry => entry.Bytes).ToArray();
    }
    private async Task<string> TextValue(string command) => Encoding.ASCII.GetString(await Raw(Encoding.ASCII.GetBytes(command + "\r\n")));
    private async Task Text(string command, string expected)
    {
        var received = await TextValue(command);
        Require(received == expected, command + " response mismatch: " + Convert.ToHexString(Encoding.ASCII.GetBytes(received)));
    }
    private async Task Until(Func<bool> condition, int timeoutMs = 8000)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            Require(Stopwatch.GetElapsedTime(started).TotalMilliseconds < timeoutMs, "Timed out waiting for monitor state; no TX retry.");
            await Task.Delay(5, token);
        }
    }
    private static async Task RecordRun(Task run) { try { await run; } catch (Exception) { /* already reported by the owning operation */ } }
    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        try { await _serial.DisconnectAsync(CancellationToken.None); }
        catch (Exception ex) { failures.Add(ex); }
        try { await Task.WhenAll(_drains).WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (Exception ex) { failures.Add(ex); }
        finally
        {
            _serial.CaptureBytesReceived -= evidence.Rx; _serial.Error -= evidence.Error;
            try { await _serial.DisposeAsync(); }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (failures.Count > 0) throw new AggregateException("Serial validation cleanup failed; inspect the recorded primary case error too.", failures);
    }
}
