using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

if (args.Length != 5) throw new ArgumentException("SessionRaceCheck <absolute-cli> <instance> <new-evidence-directory> probe|wire|broken <rounds>");
await using var suite = new Suite(args[0], args[1], args[2], args[3], int.Parse(args[4]));
try { await suite.RunAsync(); return 0; }
catch (IOException) { return 1; }

sealed class Suite(string cli, string instance, string directory, string phase, int rounds) : IAsyncDisposable
{
    private readonly ConcurrentQueue<object> evidence = new();
    private readonly ConcurrentQueue<string> stderr = new();
    private readonly List<string> ownedNames = [], ownedRuns = [], checks = [];
    private readonly List<McpClient> clients = [];
    private readonly string tag = "F103RB session " + DateTime.Now.ToString("HHmmss") + " " + Guid.NewGuid().ToString("N")[..6];
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private int number, cliCalls, mcpCalls, transmitted, rejected;
    private string? initialMode, failure;
    private static bool Ok(JsonElement value) => value.GetProperty("success").GetBoolean();
    private static JsonElement Data(JsonElement value) { Need(Ok(value), value.ToString()); return value.GetProperty("data"); }
    private static void Need(bool condition, string message) { if (!condition) throw new IOException(message); }
    private void Save(string name, object value) => File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(value, json));
    private void Pass(string value) { checks.Add(value); Console.WriteLine("PASS " + value); }

    public async Task RunAsync()
    {
        Need(Path.IsPathFullyQualified(cli) && File.Exists(cli), "An absolute existing CLI is required.");
        Need(rounds is >= 1 and <= 100 && phase is "probe" or "wire" or "broken", "Invalid bounded test options.");
        Need(!Directory.Exists(directory) || !Directory.EnumerateFileSystemEntries(directory).Any(), "Use a new evidence directory.");
        Directory.CreateDirectory(directory);
        Save("executable.json", new { cli, instance, phase, rounds,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(cli))),
            assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.ChangeExtension(cli, ".dll")))) });
        try
        {
            for (var index = 0; index < 2; index++)
            {
                var transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = "SessionRaceCheck" + index, Command = cli, Arguments = ["mcp", "--instance", instance],
                    StandardErrorLines = line => stderr.Enqueue(line)
                });
                using var start = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                clients.Add(await McpClient.CreateAsync(transport, cancellationToken: start.Token));
            }
            var state = Data(await Cli("status"));
            if (phase == "probe")
            {
                for (var round = 0; round < rounds; round++)
                {
                    var tasks = Enumerable.Range(0, 12).Select(index => Cli(index % 3 == 0 ? "describe" : "status")).ToList();
                    tasks.AddRange(clients.SelectMany((_, index) => Enumerable.Range(0, 3).Select(_ => Mcp(index, "serial_get_status", new { }))));
                    foreach (var reply in await Task.WhenAll(tasks)) Need(Ok(reply), reply.ToString());
                    Pass($"probe {round + 1}: 12 CLI and 6 MCP complete replies");
                }
            }
            else
            {
                Need(state.GetProperty("connected").GetBoolean() && state.GetProperty("port").GetString() == "COM3" &&
                    state.GetProperty("baudRate").GetInt32() == 115200 && state.GetProperty("dataBits").GetInt32() == 8 &&
                    state.GetProperty("parity").GetString() == "None" && state.GetProperty("stopBits").GetString() == "One" &&
                    state.GetProperty("handshake").GetString() == "None" && !state.GetProperty("busy").GetBoolean(), "Require idle COM3 / 115200 / 8N1 / None.");
                initialMode = state.GetProperty("mode").GetString();
                SerialLabWire.AssertBytes(await Read(Data(await Cli("request", "--port", "COM3", "--text", "ID?", "--ending", "crlf", "--duration-ms", "400")), "identity"),
                    Encoding.ASCII.GetBytes("ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1\r\n"));
                for (var round = 0; round < rounds; round++)
                    if (phase == "broken") await BrokenOutputRound(round); else await WireRound(round);
                var health = Encoding.UTF8.GetString(await Read(Data(await Cli("request", "--port", "COM3", "--text", "STATUS?", "--ending", "crlf", "--duration-ms", "400")), "device-health"));
                Need(health.Contains("parse_errors=0 rx_drops=0 uart_errors=0 tx_rejected=0"), health);
                Pass("device parse/UART/drop/rejection counters remain zero");
            }
        }
        catch (Exception ex) { failure = ex.ToString(); Console.Error.WriteLine(failure); }
        finally
        {
            foreach (var id in ownedRuns)
            {
                try { var run = Data(await Cli("sequence", "status", "--run", id)); if (run.GetProperty("state").GetString() == "running") { Data(await Cli("sequence", "cancel", "--run", id)); await WaitRun(id, false); } } catch { }
            }
            foreach (var name in ownedNames) { try { Data(await Cli("sequence", "delete", "--name", name)); } catch (Exception ex) { evidence.Enqueue(new { cleanup = name, error = ex.Message }); } }
            if (initialMode is not null) { try { Data(await Cli("mode", "--set", initialMode)); } catch { } }
            if (!stderr.IsEmpty) failure ??= "MCP stderr: " + string.Join("\n", stderr);
            Save("report.json", new { instance, cli, phase, rounds, cliCalls, mcpCalls, transmitted, rejected, failure, checks, responses = evidence.ToArray(), mcpStderr = stderr.ToArray() });
            Console.WriteLine("Evidence: " + directory);
        }
        if (failure is not null) throw new IOException(failure);
    }

    private async Task<JsonElement> Cli(params string[] arguments)
    {
        var id = Interlocked.Increment(ref number); Interlocked.Increment(ref cliCalls);
        var start = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments[0] == "describe" ? arguments : arguments.Concat(["--instance", instance])) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_DbgEnableMiniDump"] = "1";
        start.Environment["DOTNET_DbgMiniDumpName"] = Path.Combine(directory, "crash-%p.dmp");
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = Task.Factory.StartNew(process.StandardOutput.ReadToEnd, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var error = Task.Factory.StartNew(process.StandardError.ReadToEnd, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var timer = Stopwatch.StartNew(); string? text = null, errors = null; int? exit = null;
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            exit = process.ExitCode; text = await output; errors = await error;
            File.WriteAllText(Path.Combine(directory, $"cli-{id:D5}.stdout"), text);
            Need(text.Length != 0, $"Empty stdout: CLI {id}, exit {exit}, stderr {errors}");
            Need(errors.Length == 0, "CLI stderr: " + errors);
            var reply = JsonSerializer.Deserialize<JsonElement>(text);
            Need(exit is 0 or 1, $"Unexpected CLI exit {exit}");
            return reply;
        }
        finally
        {
            evidence.Enqueue(new { id, kind = "cli", processId = process.Id, arguments, elapsedMs = timer.Elapsed.TotalMilliseconds, exit, stdout = text, stderr = errors });
            if (!process.HasExited) process.Kill(true);
        }
    }

    private async Task<JsonElement> Mcp(int index, string tool, object arguments)
    {
        var id = Interlocked.Increment(ref number); Interlocked.Increment(ref mcpCalls);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var fields = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments))!;
        fields["instanceId"] = instance;
        var timer = Stopwatch.StartNew();
        var result = await clients[index].CallToolAsync(tool, fields, cancellationToken: timeout.Token);
        var reply = JsonSerializer.Deserialize<JsonElement>(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
        evidence.Enqueue(new { id, kind = "mcp", index, tool, arguments, elapsedMs = timer.Elapsed.TotalMilliseconds, reply });
        return reply;
    }

    private async Task WireRound(int round)
    {
        var mode = round % 2 == 0 ? "hex" : "terminal";
        Data(await Cli("mode", "--set", mode));
        var payload = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var packet = SerialLabWire.Encode(2, (uint)(20000 + round), payload);
        var binaryReply = SerialLabWire.Encode(0x82, (uint)(20000 + round), payload);
        var ping = "race-" + tag.Replace(' ', '-') + "-" + round;
        var stopName = tag + " stop " + round; var mainName = tag + " mixed " + round;
        object Step(string command, string txMode, int captureMs) => new { command, txMode, lineEnding = txMode == "hex" ? "none" : "crlf", captureMs, delayAfterMs = 0 };
        await Define(stopName, 1, [Step("PING " + ping, "text", 60000), Step("PING forbidden-late-" + ping, "text", 300)]);
        await Define(mainName, 4, [Step(SerialLabWire.Hex(packet), "hex", 200), Step("PING " + ping, "text", 200)]);
        var stopped = Data(await Mcp(0, "serial_sequence_start", new { name = stopName, port = "COM3" })).GetProperty("runId").GetString()!; ownedRuns.Add(stopped);
        await WaitRun(stopped, true); await Task.Delay(80);
        var contenders = new[] {
            Cli("send", "--port", "COM3", "--text", "PING forbidden-" + ping),
            Cli("disconnect"), Cli("mode", "--set", mode == "hex" ? "terminal" : "hex"),
            Mcp(0, "serial_send", new { port = "COM3", command = SerialLabWire.Hex(packet), txMode = "hex" }),
            Mcp(1, "serial_sequence_start", new { name = mainName, port = "COM3" }),
            Mcp(1, "serial_capture_rx", new { port = "COM3", durationMs = 100 }),
            Cli("cancel"), Mcp(0, "serial_disconnect", new { })
        };
        foreach (var reply in await Task.WhenAll(contenders))
        { Need(!Ok(reply) && reply.GetProperty("error").GetProperty("code").GetString() == "busy", "Contender entered active sequence: " + reply); rejected++; }
        Data(await Mcp(1, "serial_sequence_cancel", new { runId = stopped }));
        var restarts = await Task.WhenAll(Cli("sequence", "run", "--name", mainName, "--port", "COM3"),
            Mcp(0, "serial_sequence_start", new { name = mainName, port = "COM3" }));
        var acceptedRestarts = restarts.Where(Ok).ToArray();
        Need(acceptedRestarts.Length <= 1, "Both racing clients started the same sequence.");
        foreach (var reply in restarts.Where(reply => !Ok(reply)))
            Need(reply.GetProperty("error").GetProperty("code").GetString() == "busy" && reply.GetProperty("data").ValueKind == JsonValueKind.Null, "Uncertain restart, never replay: " + reply);
        var canceled = await WaitRun(stopped, false);
        Need(canceled.GetProperty("state").GetString() == "canceled" && canceled.GetProperty("steps").GetArrayLength() == 1, "A step escaped cancellation: " + canceled);
        SerialLabWire.AssertBytes(await Read(canceled, "cancel-" + round), Encoding.UTF8.GetBytes("PONG " + ping + "\r\n")); transmitted++;
        var restart = acceptedRestarts.Length == 1 ? acceptedRestarts[0] : await Mcp(0, "serial_sequence_start", new { name = mainName, port = "COM3" });
        var id = Data(restart).GetProperty("runId").GetString()!; ownedRuns.Add(id);
        var statuses = Enumerable.Range(0, 6).Select(index => index % 3 == 0 ? Cli("status") : Mcp(index % 2, "serial_get_status", new { })).ToArray();
        foreach (var reply in await Task.WhenAll(statuses)) Need(Ok(reply), reply.ToString());
        var completed = await WaitRun(id, false);
        Need(completed.GetProperty("state").GetString() == "completed" && completed.GetProperty("completedSteps").GetInt32() == 8, completed.ToString());
        var expected = Enumerable.Range(0, 4).SelectMany(_ => binaryReply.Concat(Encoding.UTF8.GetBytes("PONG " + ping + "\r\n"))).ToArray();
        SerialLabWire.AssertBytes(await Read(completed, "mixed-" + round), expected); transmitted += 8;
        var generation = Data(await Cli("status")).GetProperty("sessionGeneration").GetInt64();
        // Two independent clients race to close and then reopen the same port.
        foreach (var reply in await Task.WhenAll(Cli("disconnect"), Mcp(1, "serial_disconnect", new { }))) SuccessOrBusy(reply);
        foreach (var reply in await Task.WhenAll(Cli("connect", "--port", "COM3", "--baud", "115200"), Mcp(0, "serial_connect", new { port = "COM3", baudRate = 115200 }))) SuccessOrBusy(reply);
        var state = Data(await Cli("status"));
        Need(state.GetProperty("connected").GetBoolean() && state.GetProperty("sessionGeneration").GetInt64() > generation, "Reconnect failed: " + state);
        Pass($"wire {round + 1}: 8 exact mixed steps, cancel/restart, 8 CLI/MCP contenders, concurrent disconnect/reconnect");
    }

    private static void SuccessOrBusy(JsonElement reply) => Need(Ok(reply) || reply.GetProperty("error").GetProperty("code").GetString() == "busy", reply.ToString());

    private async Task BrokenOutputRound(int round)
    {
        var captureRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SerialMonitor", "automation", "captures", instance);
        var command = "PING broken-" + tag.Replace(' ', '-') + "-" + round;
        var start = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "request", "--instance", instance, "--port", "COM3", "--text", command, "--ending", "crlf", "--duration-ms", "500" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var errorTask = Task.Factory.StartNew(process.StandardError.ReadToEnd, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        process.StandardInput.Close();
        process.StandardOutput.Dispose(); // Deliberate caller-side output loss, never a TX replay.
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var error = await errorTask;
            var matching = Directory.EnumerateFiles(captureRoot, "*.json").Select(path => JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path)))
                .Where(value => value.TryGetProperty("command", out var text) && text.GetString() == command).ToArray();
            Need(matching.Length == 1, "Expected exactly one retained capture after losing stdout.");
            var capture = matching[0];
            var bytes = File.ReadAllBytes(capture.GetProperty("rawFile").GetString()!);
            File.WriteAllBytes(Path.Combine(directory, $"broken-{round}.bin"), bytes);
            Save($"broken-{round}.json", new { command, exitCode = process.ExitCode, stderr = error, capture });
            Need(capture.GetProperty("sendStatus").GetString() == "sent" && capture.GetProperty("captureStatus").GetString() == "completed" && !capture.GetProperty("rxGapDetected").GetBoolean(), capture.ToString());
            SerialLabWire.AssertBytes(bytes, Encoding.ASCII.GetBytes(command.Replace("PING ", "PONG ", StringComparison.Ordinal) + "\r\n")); transmitted++;
            Need(process.ExitCode == 1 && error.Contains("output", StringComparison.OrdinalIgnoreCase), $"Lost reply was falsely successful: exit {process.ExitCode}, stderr '{error}'");
            Pass($"broken stdout {round + 1}: exit 1 + stderr, original TX/capture retained once, GUI remains alive");
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }
    private async Task Define(string name, int repeatCount, object[] steps)
    { Data(await Mcp(0, "serial_sequence_save", new { sequence = new { name, repeatCount, steps } })); ownedNames.Add(name); }
    private async Task<JsonElement> WaitRun(string id, bool firstSent)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var run = Data(await Cli("sequence", "status", "--run", id));
            if (firstSent && run.GetProperty("steps").EnumerateArray().Any(step => step.GetProperty("sendStatus").GetString() == "sent")) return run;
            if (run.GetProperty("state").GetString() != "running") return run;
            await Task.Delay(50, timeout.Token);
        }
    }
    private async Task<byte[]> Read(JsonElement data, string label)
    {
        var capture = data.GetProperty("capture");
        Need(!capture.GetProperty("rxGapDetected").GetBoolean() && capture.GetProperty("droppedBytes").GetInt64() == 0 && capture.GetProperty("serialErrors").GetInt64() == 0, capture.ToString());
        var bytes = new List<byte>(); long offset = 0;
        while (true)
        {
            var page = Data(await Cli("read", "--capture", capture.GetProperty("captureId").GetString()!, "--format", "hex", "--offset", offset.ToString(), "--limit", "256")).GetProperty("page");
            bytes.AddRange(Convert.FromHexString(page.GetProperty("text").GetString()!)); offset = page.GetProperty("nextOffset").GetInt64();
            if (!page.GetProperty("hasMore").GetBoolean()) break;
        }
        Need(bytes.Count == capture.GetProperty("storedBytes").GetInt64(), "Incomplete raw pages");
        File.WriteAllBytes(Path.Combine(directory, label + ".bin"), bytes.ToArray());
        return bytes.ToArray();
    }
    public async ValueTask DisposeAsync() { foreach (var client in clients) await client.DisposeAsync(); }
}
