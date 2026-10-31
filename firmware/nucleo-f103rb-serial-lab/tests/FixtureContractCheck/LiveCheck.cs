using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class LiveCheck
{
    public static async Task<int> Run(string pluginRoot,string manifestPath,string directory,bool inspectOnly=false)
    {
        var identity = FixtureContract.Manifest(manifestPath);
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory)) throw new Exception("Use a new evidence directory; existing evidence is never overwritten.");
        Directory.CreateDirectory(directory);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler interrupt = (_,eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += interrupt;
        var json = new JsonSerializerOptions {WriteIndented=true};
        int calls = 0, checks = 0;
        string? instance = null, originalMode = null, failure = null;
        var captures = new List<object>();
        try
        {
            pluginRoot = Path.GetFullPath(pluginRoot);
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(pluginRoot,"mcp.json")));
            var server = manifest.RootElement.GetProperty("mcpServers").GetProperty("serial-monitor");
            if (server.GetProperty("type").GetString() != "stdio") throw new Exception("Plugin must provide stdio transport.");
            string command = server.GetProperty("command").GetString()!;
            string[] arguments = server.GetProperty("args").EnumerateArray().Select(v => v.GetString()!.Replace("${PLUGIN_ROOT}",pluginRoot)).ToArray();
            File.WriteAllText(Path.Combine(directory,"transport.json"),JsonSerializer.Serialize(new {
                pluginRoot,command,arguments,cliOverride=Environment.GetEnvironmentVariable("SERIAL_MONITOR_CLI")
            },json));
            var transport = new StdioClientTransport(new StdioClientTransportOptions {
                Name="SerialLabFixtureContract", Command=command, Arguments=arguments, WorkingDirectory=pluginRoot,
                StandardErrorLines=line => Console.Error.WriteLine("PLUGIN: " + line)
            });
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            startup.CancelAfter(TimeSpan.FromSeconds(30));
            await using var client = await McpClient.CreateAsync(transport,cancellationToken:startup.Token);

            async Task<JsonElement> Call(string name,Dictionary<string,object?> values,bool allowUnavailable=false)
            {
                int index = ++calls;
                File.WriteAllText(Path.Combine(directory,$"{index:D3}-{name}-request.json"),JsonSerializer.Serialize(values,json));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                var result = await client.CallToolAsync(name,values,cancellationToken:timeout.Token);
                File.WriteAllText(Path.Combine(directory,$"{index:D3}-{name}-result.json"),JsonSerializer.Serialize(result,json));
                using var response = JsonDocument.Parse(string.Join("\n",result.Content.OfType<TextContentBlock>().Select(b=>b.Text)));
                var envelope = response.RootElement.Clone();
                if (result.IsError == true || !envelope.GetProperty("success").GetBoolean())
                {
                    if (allowUnavailable) return default;
                    throw new Exception($"{name} failed; no TX replay: {envelope}");
                }
                return envelope.GetProperty("data");
            }
            Dictionary<string,object?> Target() => new() { ["instanceId"]=instance! };
            var tools = await client.ListToolsAsync(cancellationToken:startup.Token);
            var required = new[] {"serial_get_capabilities","serial_list_instances","serial_get_status","serial_send","serial_send_and_capture","serial_read_capture"};
            if (required.Any(name => !tools.Any(tool => tool.Name == name))) throw new Exception("Plugin lacks required tools.");
            await Call("serial_get_capabilities",new());
            var instances = (await Call("serial_list_instances",new())).GetProperty("instances");
            var candidates = new List<JsonElement>();
            foreach (var entry in instances.EnumerateArray())
            {
                var state = await Call("serial_get_status",new() { ["instanceId"]=entry.GetProperty("instanceId").GetString() },allowUnavailable:true);
                if (state.ValueKind != JsonValueKind.Object) continue;
                if (state.GetProperty("connected").GetBoolean() && state.GetProperty("port").GetString() == "COM3") candidates.Add(entry);
            }
            if (candidates.Count != 1) throw new Exception("Require exactly one existing connected COM3 GUI.");
            instance = candidates[0].GetProperty("instanceId").GetString();
            var before = await Call("serial_get_status",Target());
            if (before.GetProperty("busy").GetBoolean() || before.GetProperty("baudRate").GetInt32() != 115200 ||
                before.GetProperty("dataBits").GetInt32() != 8 || before.GetProperty("parity").GetString() != "None" ||
                before.GetProperty("stopBits").GetString() != "One" || before.GetProperty("handshake").GetString() != "None")
                throw new Exception("Require idle 115200/8N1/no handshake connection.");
            originalMode = before.GetProperty("mode").GetString();
            File.WriteAllText(Path.Combine(directory,"initial-status.json"),before.GetRawText());
            if (inspectOnly)
            {
                ++checks;
                Console.WriteLine("READY " + instance + " " + before.GetRawText());
                return 0;
            }

            async Task<byte[]> Request(string text,string txMode="text",bool encodedHex=false)
            {
                var values = Target(); values["port"]="COM3"; values["txMode"]=txMode;
                values["command"]=txMode=="hex" && !encodedHex ? SerialLabWire.Hex(Encoding.ASCII.GetBytes(text+"\r\n")) : text;
                values["lineEnding"]=txMode=="hex" ? "none" : "crlf";
                values["durationMs"]=750; values["previewBytes"]=64; values["format"]="hex";
                var response = await Call("serial_send_and_capture",values);
                var capture = response.GetProperty("capture");
                string id = capture.GetProperty("captureId").GetString()!;
                if (capture.GetProperty("captureStatus").GetString()!="completed" || capture.GetProperty("sendStatus").GetString()!="sent" ||
                    capture.GetProperty("rxGapDetected").GetBoolean() || capture.GetProperty("droppedBytes").GetInt64()!=0 ||
                    capture.GetProperty("serialErrors").GetInt64()!=0) throw new Exception("Incomplete UART evidence: " + id);
                long total = capture.GetProperty("storedBytes").GetInt64();
                if (total < 0 || total > 8192) throw new Exception("Fixture RX exceeds bounded 8KiB budget.");
                var page = response.GetProperty("page");
                using var bytes = new MemoryStream();
                while (true)
                {
                    if (page.GetProperty("offset").GetInt64()!=bytes.Length) throw new Exception("Unexpected RX page offset.");
                    long previous = bytes.Length;
                    string compact = string.Concat(page.GetProperty("text").GetString()!.Where(c=>!char.IsWhiteSpace(c)));
                    if (compact.Length > (8192 - previous) * 2) throw new Exception("RX page exceeds remaining memory budget.");
                    bytes.Write(Convert.FromHexString(compact));
                    if (bytes.Length > 8192) throw new Exception("Fixture RX exceeds memory budget.");
                    if (!page.GetProperty("hasMore").GetBoolean()) break;
                    long next = page.GetProperty("nextOffset").GetInt64();
                    if (next <= previous || next != bytes.Length) throw new Exception("RX page made no valid progress.");
                    var read = Target(); read["captureId"]=id; read["offset"]=next; read["limit"]=4096; read["format"]="hex";
                    page = (await Call("serial_read_capture",read)).GetProperty("page");
                }
                if (bytes.Length != total) throw new Exception("Unread capture tail.");
                byte[] result = bytes.ToArray();
                File.WriteAllBytes(Path.Combine(directory,id+".bin"),result);
                captures.Add(new {captureId=id,bytes=result.Length,txMode});
                return result;
            }
            // A mismatched/old board stops here, before running fixtures or changing settings.
            FixtureContract.AssertBuild(await Request("BUILD?"),identity.Build,identity.Version); ++checks;
            FixtureContract.AssertCaps(await Request("CAPS?"),identity.Build,identity.Version); ++checks;
            FixtureContract.AssertSelftest(await Request("SELFTEST"),identity.Build); ++checks;
            foreach (int length in BinaryContract.Lengths)
            {
                var received = await Request(SerialLabWire.Hex(BinaryContract.Request(length)),"hex",encodedHex:true);
                BinaryContract.AssertReply(received,length); ++checks;
                Console.WriteLine($"PASS binary payload={length}, frame={length + 11}, full CRC/length/endian echo");
            }
            byte[] maximum = BinaryContract.Request(1024);
            var partial = Target(); partial["port"]="COM3"; partial["txMode"]="hex";
            partial["command"]=SerialLabWire.Hex(maximum[..8]); partial["lineEnding"]="none";
            partial["previewBytes"]=64; partial["format"]="hex";
            var sent = await Call("serial_send",partial);
            if (sent.GetProperty("capture").GetProperty("sendStatus").GetString()!="sent") throw new Exception("Partial header TX uncertain; no replay.");
            BinaryContract.AssertReply(await Request(SerialLabWire.Hex(maximum[8..]),"hex",encodedHex:true),1024); ++checks;
            Console.WriteLine("PASS 1024-byte payload with two MCP sends splitting the LEN field");
            byte[] secondPayload = BinaryContract.Payload(1024).Reverse().ToArray();
            byte[] second = SerialLabWire.Encode(2,BinaryContract.Sequence(1024)+1,secondPayload);
            byte[] joined = await Request(SerialLabWire.Hex([..maximum,..second]),"hex",encodedHex:true);
            SerialLabWire.AssertBytes(joined,[..SerialLabWire.Encode(0x82,BinaryContract.Sequence(1024),BinaryContract.Payload(1024)),
                ..SerialLabWire.Encode(0x82,BinaryContract.Sequence(1024)+1,secondPayload)]); ++checks;
            byte[] badCrc = (byte[])maximum.Clone(); badCrc[^1] ^= 1;
            SerialLabWire.AssertBytes(await Request(SerialLabWire.Hex(badCrc),"hex",encodedHex:true),
                SerialLabWire.Encode(0xe0,BinaryContract.Sequence(1024),Encoding.ASCII.GetBytes("CRC"))); ++checks;
            byte[] oversizedHeader = [0xa5,0x5a,2,1,0,0,0,1,4];
            SerialLabWire.AssertBytes(await Request(SerialLabWire.Hex(oversizedHeader),"hex",encodedHex:true),
                SerialLabWire.Encode(0xe0,1,Encoding.ASCII.GetBytes("LEN"))); ++checks;
            foreach (string name in FixtureContract.Names)
                foreach (string txMode in new[] {"text","hex"})
                {
                    string run = Guid.NewGuid().ToString("N");
                    var received = await Request($"RUN {name} {run}",txMode);
                    FixtureContract.AssertResponse(received,name,run,identity.Build); ++checks;
                    Console.WriteLine($"PASS {name} via {txMode}, GUI mode retained: {originalMode}");
                }
            var after = await Call("serial_get_status",Target());
            File.WriteAllText(Path.Combine(directory,"final-status.json"),after.GetRawText());
            if (after.GetProperty("mode").GetString()!=originalMode || after.GetProperty("baudRate").GetInt32()!=115200 ||
                after.GetProperty("byteTimeoutMs").GetInt32()!=before.GetProperty("byteTimeoutMs").GetInt32())
                throw new Exception("GUI state changed during test; no automatic mode switch/clear will be attempted.");
            ++checks;
        }
        catch (Exception error) { failure = error.ToString(); Console.Error.WriteLine(failure); }
        finally
        {
            Console.CancelKeyPress -= interrupt;
            File.WriteAllText(Path.Combine(directory,"report.json"),JsonSerializer.Serialize(new {
                mode=inspectOnly ? "plugin-MCP-preflight" : "plugin-MCP-live",buildId=identity.Build,checks,
                passed=failure is null,hardwareVerified=!inspectOnly && failure is null,
                failure,instanceId=instance,calls,captures,originalMode,modeSwitches=0,
                validatorAssemblySha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(System.Reflection.Assembly.GetExecutingAssembly().Location))),
                binaryPayloadLengths=BinaryContract.Lengths,maxPayloadBytes=1024,maxFrameBytes=1035
            },json));
        }
        return failure is null ? 0 : 1;
    }
}
