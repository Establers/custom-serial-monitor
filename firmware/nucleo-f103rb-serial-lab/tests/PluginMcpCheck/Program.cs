using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

if (args.Length != 3 || args[2] != "--allow-mode-switch")
    throw new ArgumentException("This legacy suite changes GUI mode and clears visible history. Explicitly pass <plugin-root> <evidence-directory> --allow-mode-switch, or use FixtureContractCheck mcp to preserve the screen.");
var pluginRoot = Path.GetFullPath(args[0]);
var evidenceDirectory = Path.GetFullPath(args[1]);
Directory.CreateDirectory(evidenceDirectory);
using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(pluginRoot, "mcp.json")));
var server = manifest.RootElement.GetProperty("mcpServers").GetProperty("serial-monitor");
if (server.GetProperty("type").GetString() != "stdio") throw new Exception("Expected plugin stdio manifest.");
var command = server.GetProperty("command").GetString()!;
var serverArguments = server.GetProperty("args").EnumerateArray()
    .Select(value => value.GetString()!.Replace("${PLUGIN_ROOT}", pluginRoot, StringComparison.Ordinal)).ToArray();
var serializerOptions = new JsonSerializerOptions { WriteIndented = true };
var calls = 0;
var passed = new List<string>();
var captures = new List<object>();
string? instance = null;
string? failure = null;
string? initialMode = null;
string? activeRun = null;
int initialTimeout = 40;

File.WriteAllText(Path.Combine(evidenceDirectory, "transport.json"), JsonSerializer.Serialize(new {
    pluginRoot, manifest = Path.Combine(pluginRoot, "mcp.json"), command, arguments = serverArguments,
    client = "Official ModelContextProtocol.Core 2.3.0 SDK; no custom JSON-RPC; no direct serialctl/device transport"
}, serializerOptions));
var transport = new StdioClientTransport(new StdioClientTransportOptions {
    Name = "SerialMonitorPluginHardwareCheck", Command = command, Arguments = serverArguments,
    WorkingDirectory = pluginRoot,
    StandardErrorLines = line => File.AppendAllText(Path.Combine(evidenceDirectory, "server-stderr.txt"), line + Environment.NewLine)
});
using var startupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await using var client = await McpClient.CreateAsync(transport, cancellationToken: startupDeadline.Token);

async Task<JsonElement> Call(string name, Dictionary<string, object?> arguments, bool allowFailure = false)
{
    int call = ++calls;
    File.WriteAllText(Path.Combine(evidenceDirectory, $"{call:D3}-{name}-request.json"),
        JsonSerializer.Serialize(arguments, serializerOptions));
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    var result = await client.CallToolAsync(name, arguments, cancellationToken: deadline.Token);
    File.WriteAllText(Path.Combine(evidenceDirectory, $"{call:D3}-{name}-result.json"),
        JsonSerializer.Serialize(result, serializerOptions));
    string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
    using var document = JsonDocument.Parse(text);
    var envelope = document.RootElement.Clone();
    if (!allowFailure && (result.IsError == true || !envelope.GetProperty("success").GetBoolean()))
        throw new Exception($"MCP tool {name} failed; TX was not retried. {text}");
    return envelope;
}
Dictionary<string, object?> Target() => new() { ["instanceId"] = instance! };
void Pass(string name) { passed.Add(name); Console.WriteLine("PASS " + name); }
void Require(bool condition, string reason) { if (!condition) throw new Exception(reason); }

async Task<byte[]> ReadCapture(JsonElement data, bool mustPage = false)
{
    var capture = data.GetProperty("capture");
    Require(capture.GetProperty("captureStatus").GetString() == "completed", "Capture incomplete");
    Require(!capture.GetProperty("rxGapDetected").GetBoolean() && capture.GetProperty("droppedBytes").GetInt64() == 0 &&
        capture.GetProperty("serialErrors").GetInt64() == 0, "Capture has transport gap/error");
    string id = capture.GetProperty("captureId").GetString()!;
    var page = data.GetProperty("page");
    Require(page.GetProperty("format").GetString() == "hex", "Expected raw HEX paging");
    if (mustPage) Require(page.GetProperty("hasMore").GetBoolean(), "Paging fixture did not exercise additional pages");
    using var bytes = new MemoryStream();
    int pages = 0;
    while (true)
    {
        var hex = page.GetProperty("text").GetString()!;
        // Current MCP returns compact HEX; tolerate whitespace without altering bytes.
        bytes.Write(Convert.FromHexString(string.Concat(hex.Where(c => !char.IsWhiteSpace(c)))));
        ++pages;
        if (!page.GetProperty("hasMore").GetBoolean()) break;
        var next = Target(); next["captureId"] = id; next["offset"] = page.GetProperty("nextOffset").GetInt64();
        next["limit"] = 4096; next["format"] = "hex";
        var read = await Call("serial_read_capture", next);
        page = read.GetProperty("data").GetProperty("page");
    }
    byte[] result = bytes.ToArray();
    Require(result.LongLength == capture.GetProperty("storedBytes").GetInt64(), "Paged bytes differ from stored RX length");
    File.WriteAllBytes(Path.Combine(evidenceDirectory, id + ".bin"), result);
    File.WriteAllText(Path.Combine(evidenceDirectory, id + "-capture.json"), capture.GetRawText());
    captures.Add(new { captureId = id, bytes = result.Length, pages });
    return result;
}
async Task<byte[]> Request(string value, string txMode = "text", int duration = 600, bool paged = false, string ending = "crlf")
{
    var arguments = Target(); arguments["port"] = "COM3"; arguments["command"] = value;
    arguments["txMode"] = txMode; arguments["lineEnding"] = ending;
    arguments["durationMs"] = duration; arguments["previewBytes"] = 64; arguments["format"] = "hex";
    var response = await Call("serial_send_and_capture", arguments);
    return await ReadCapture(response.GetProperty("data"), paged);
}
async Task SetMode(string mode)
{
    var arguments = Target(); arguments["mode"] = mode;
    await Call("serial_set_mode", arguments);
    var state = (await Call("serial_get_status", Target())).GetProperty("data");
    Require(state.GetProperty("mode").GetString() == mode, "GUI mode readback mismatch");
}
void TextEquals(byte[] actual, string expected) => SerialLabWire.AssertBytes(actual, Encoding.UTF8.GetBytes(expected));

try
{
    var tools = await client.ListToolsAsync(cancellationToken: startupDeadline.Token);
    File.WriteAllText(Path.Combine(evidenceDirectory, "tools.json"), JsonSerializer.Serialize(tools.Select(t => new { t.Name, t.Description }), serializerOptions));
    var toolNames = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
    var capabilities = (await Call("serial_get_capabilities", new())).GetProperty("data");
    File.WriteAllText(Path.Combine(evidenceDirectory, "capabilities.json"), capabilities.GetRawText());
    foreach (string required in new[] { "serial_get_status", "serial_list_instances", "serial_set_mode", "serial_send_and_capture", "serial_read_capture", "serial_sequence_start" })
        Require(toolNames.Contains(required), "Missing plugin MCP tool: " + required);
    Pass($"plugin manifest stdio initialize, tools/list ({tools.Count}), capabilities CLI {capabilities.GetProperty("cliVersion").GetString()}");

    var instances = (await Call("serial_list_instances", new())).GetProperty("data").GetProperty("instances");
    var candidates = new List<(string Id, JsonElement State)>();
    foreach (var entry in instances.EnumerateArray())
    {
        string id = entry.GetProperty("instanceId").GetString()!;
        var response = await Call("serial_get_status", new() { ["instanceId"] = id }, allowFailure: true);
        if (!response.GetProperty("success").GetBoolean()) continue;
        var state = response.GetProperty("data");
        if (state.GetProperty("connected").GetBoolean() && state.GetProperty("port").GetString() == "COM3") candidates.Add((id, state));
    }
    Require(candidates.Count == 1, $"Expected unique connected COM3 GUI, found {candidates.Count}");
    instance = candidates[0].Id;
    var original = candidates[0].State;
    Require(!original.GetProperty("busy").GetBoolean(), "Target GUI is busy with unrelated work");
    Require(original.GetProperty("baudRate").GetInt32() == 115200 && original.GetProperty("dataBits").GetInt32() == 8 &&
        original.GetProperty("parity").GetString() == "None" && original.GetProperty("stopBits").GetString() == "One" &&
        original.GetProperty("handshake").GetString() == "None", "Target must use 115200/8N1/no handshake");
    initialMode = original.GetProperty("mode").GetString()!;
    initialTimeout = original.GetProperty("byteTimeoutMs").GetInt32();
    Console.WriteLine("Target " + instance);
    Require(Encoding.UTF8.GetString(await Request("ID?")).Contains("ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1"), "Wrong device firmware");
    Pass("MCP discovers unique COM3 target and confirms firmware identity");

    await SetMode("terminal");
    foreach (var ending in new[] { "cr", "lf", "crlf" })
        TextEquals(await Request("PING mcp-" + ending, ending: ending), "PONG mcp-" + ending + "\r\n");
    Pass("Terminal mode CR/LF/CRLF text TX/RX");
    TextEquals(await Request("ECHO   MCP 한글 🚀  "), "ECHO   MCP 한글 🚀  \r\n");
    Pass("Terminal UTF-8 and exact spaces through MCP arguments");
    TextEquals(await Request("SPLIT 20 MCP-SPLIT-OK", duration: 1500), "MCP-SPLIT-OK\r\n");
    Pass("Terminal split response reconstructed from complete raw capture");
    var payload = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
    var binaryRequest = SerialLabWire.Encode(2, 0x12345678, payload);
    var binaryResponse = SerialLabWire.Encode(0x82, 0x12345678, payload);
    SerialLabWire.AssertBytes(await Request(SerialLabWire.Hex(binaryRequest), "hex", paged: true, ending: "none"), binaryResponse);
    Pass("Terminal GUI with explicit HEX TX: all 256 byte values and CRC, full MCP paging");
    var burst = await Request("BURST 100 32", duration: 1500, paged: true);
    SerialLabWire.AssertTextRecords(burst, "BURST", 100, 0, 0); SerialLabWire.AssertBurstPayload(burst, 32);
    Pass("Terminal 100 numbered records/payloads, follow every MCP RX page");

    await SetMode("hex");
    SerialLabWire.AssertBytes(await Request(SerialLabWire.Hex(binaryRequest), "hex", paged: true, ending: "none"), binaryResponse);
    Pass("HEX GUI with explicit HEX TX: byte-exact binary echo/CRC, no appended line ending");
    TextEquals(await Request("PING text-in-hex"), "PONG text-in-hex\r\n");
    Pass("HEX GUI with explicit Terminal text TX remains independent");
    foreach (int timeout in new[] { 1, 40, 120 })
    {
        var configure = Target(); configure["port"] = "COM3"; configure["byteTimeoutMs"] = timeout;
        await Call("serial_configure", configure);
        SerialLabWire.AssertGeneratedFrames(await Request("HEX 20", duration: 700, paged: true), 20, 0);
    }
    Pass("MCP configure 1/40/120ms byte timeout: raw CRC/order integrity in HEX mode");

    var invalid = Target(); invalid["port"] = "COM3"; invalid["command"] = "GG";
    invalid["txMode"] = "hex"; invalid["durationMs"] = 300;
    var rejected = await Call("serial_send_and_capture", invalid, allowFailure: true);
    Require(!rejected.GetProperty("success").GetBoolean(), "Malformed HEX was not rejected");
    Require(rejected.GetProperty("error").GetProperty("code").GetString() == "invalid_argument", "Unexpected malformed HEX error");
    TextEquals(await Request("PING after-invalid-hex"), "PONG after-invalid-hex\r\n");
    Pass("invalid HEX rejected by MCP, then same session remains usable");

    foreach (var (name, guiMode) in new[] { ("F103RB LAB - CLI smoke", "terminal"), ("F103RB LAB - Binary CRC echo", "hex") })
    {
        await SetMode(guiMode);
        var lookup = Target(); lookup["name"] = name;
        var definition = await Call("serial_sequence_get", lookup);
        File.WriteAllText(Path.Combine(evidenceDirectory, guiMode + "-sequence-definition.json"), definition.GetRawText());
        var start = Target(); start["name"] = name; start["port"] = "COM3";
        var run = (await Call("serial_sequence_start", start)).GetProperty("data");
        string runId = run.GetProperty("runId").GetString()!;
        activeRun = runId;
        var watch = Target(); watch["runId"] = runId;
        for (int i = 0; i < 20; ++i)
        {
            await Task.Delay(500);
            run = (await Call("serial_sequence_status", watch)).GetProperty("data");
            if (run.GetProperty("state").GetString() != "running") break;
        }
        Require(run.GetProperty("state").GetString() == "completed", "MCP sequence did not complete: " + name);
        activeRun = null;
        foreach (var step in run.GetProperty("steps").EnumerateArray())
            Require(step.GetProperty("sendStatus").GetString() == "sent" && step.GetProperty("matchStatus").GetString() == "matched", "MCP sequence step did not match");
        var capture = run.GetProperty("capture");
        var readArgs = Target(); readArgs["captureId"] = capture.GetProperty("captureId").GetString();
        readArgs["offset"] = 0; readArgs["limit"] = 64; readArgs["format"] = "hex";
        var first = (await Call("serial_read_capture", readArgs)).GetProperty("data");
        byte[] rx = await ReadCapture(first);
        if (guiMode == "terminal")
        {
            var text = Encoding.UTF8.GetString(rx);
            Require(text.Contains("PONG automation") && text.Contains("CLI-SPLIT-OK"), "Smoke sequence RX incomplete");
            SerialLabWire.AssertTextRecords(rx, "BURST", 100, 0, 0);
            SerialLabWire.AssertBurstPayload(rx, 16);
        }
        else SerialLabWire.AssertBytes(rx, SerialLabWire.Encode(0x82, 42, new byte[] { 0,1,2,10,13,127,128,165,90,255 }));
        Pass("MCP sequence start/status/full capture: " + name);
    }
}
catch (Exception error) { failure = error.ToString(); Console.Error.WriteLine(failure); }
finally
{
    if (instance is not null && initialMode is not null)
    {
        try
        {
            if (activeRun is not null)
            {
                var cancel = Target(); cancel["runId"] = activeRun;
                await Call("serial_sequence_cancel", cancel);
                await Call("serial_sequence_status", cancel);
            }
            var configure = Target(); configure["port"] = "COM3"; configure["byteTimeoutMs"] = initialTimeout;
            await Call("serial_configure", configure); await SetMode(initialMode);
            var final = await Call("serial_get_status", Target());
            File.WriteAllText(Path.Combine(evidenceDirectory, "final-status.json"), final.GetRawText());
        }
        catch (Exception cleanup) { failure ??= cleanup.ToString(); Console.Error.WriteLine("Restore failed: " + cleanup.Message); }
    }
    File.WriteAllText(Path.Combine(evidenceDirectory, "report.json"), JsonSerializer.Serialize(new {
        path = "mcp.json -> actual plugin launcher -> discovered CLI MCP -> GUI -> COM3",
        hostIntegratedToolExposure = false, pluginInstalledInCodex = false,
        instanceId = instance, passed, failure, calls, captures, restoredMode = initialMode, restoredByteTimeoutMs = initialTimeout
    }, serializerOptions));
    Console.WriteLine("Evidence: " + evidenceDirectory);
}
if (failure is not null) Environment.ExitCode = 1;
else Console.WriteLine($"PASS {passed.Count} plugin MCP hardware checks ({calls} tool calls)");
