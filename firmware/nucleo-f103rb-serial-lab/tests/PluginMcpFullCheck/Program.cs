using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

if (args.Length != 3) throw new ArgumentException("PluginMcpFullCheck <plugin-root> <new-evidence-directory> discover|safe|state|cancel|remaining|quick|user-setup|user-monitor|user-observe|user-status");
await using var suite = await Suite.CreateAsync(args[0], args[1], args[2]);
await suite.RunAsync();

sealed class Suite : IAsyncDisposable
{
    private readonly string root, directory, phase;
    private readonly McpClient client;
    private readonly JsonSerializerOptions json = new() { WriteIndented = true };
    private readonly ConcurrentDictionary<string,int> successes = new(), rejections = new();
    private readonly List<string> cases = new(), ownedSequences = new(), observations = new();
    private readonly List<object> captures = new();
    private readonly HashSet<string> toolNames = new(StringComparer.Ordinal);
    private readonly string tag = "F103RB MCP full " + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + " " + Guid.NewGuid().ToString("N")[..6];
    private int calls;
    private string instance = "", originalMode = "", activeRun = "";
    private int originalTimeout = 40;
    private bool standaloneCapture, disconnected, acquired;
    private string? failure;
    private JsonElement initialState;

    private Suite(string root, string directory, string phase, McpClient client)
    { this.root=root; this.directory=directory; this.phase=phase; this.client=client; }

    public static async Task<Suite> CreateAsync(string root, string directory, string phase)
    {
        root=Path.GetFullPath(root); directory=Path.GetFullPath(directory);
        if (phase is not ("discover" or "safe" or "state" or "cancel" or "remaining" or "quick" or "user-setup" or "user-monitor" or "user-observe" or "user-status")) throw new ArgumentException("Invalid phase");
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) throw new Exception("Use a new evidence directory");
        Directory.CreateDirectory(directory);
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"mcp.json")));
        var server=manifest.RootElement.GetProperty("mcpServers").GetProperty("serial-monitor");
        var command=server.GetProperty("command").GetString()!;
        var arguments=server.GetProperty("args").EnumerateArray().Select(a=>a.GetString()!.Replace("${PLUGIN_ROOT}",root,StringComparison.Ordinal)).ToArray();
        File.WriteAllText(Path.Combine(directory,"transport.json"),JsonSerializer.Serialize(new {
            pluginRoot=root, manifest=Path.Combine(root,"mcp.json"), command, arguments,
            cliOverride=Environment.GetEnvironmentVariable("SERIAL_MONITOR_CLI"),
            client="Official ModelContextProtocol.Core 2.3.0 SDK; all device operations use plugin MCP tools"
        },new JsonSerializerOptions{WriteIndented=true}));
        var transport=new StdioClientTransport(new StdioClientTransportOptions {
            Name="SerialMonitorFullFeatureCheck",Command=command,Arguments=arguments,WorkingDirectory=root,
            StandardErrorLines=line=>Console.Error.WriteLine("PLUGIN: "+line)
        });
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return new(root,directory,phase,await McpClient.CreateAsync(transport,loggerFactory:new WireLogFactory(directory),cancellationToken:deadline.Token));
    }

    private Dictionary<string,object?> Target(params (string Key,object? Value)[] fields)
    {
        var result=new Dictionary<string,object?>{["instanceId"]=instance};
        foreach(var field in fields) result[field.Key]=field.Value;
        return result;
    }
    private void Save(string file,object value)=>File.WriteAllText(Path.Combine(directory,file),JsonSerializer.Serialize(value,json));
    private static void Need(bool condition,string message) { if(!condition)throw new Exception(message); }
    private void Pass(string name) { cases.Add(name);Console.WriteLine("PASS "+name); }
    private static bool Ok(JsonElement envelope)=>envelope.GetProperty("success").GetBoolean();
    private static JsonElement Data(JsonElement envelope)=>envelope.GetProperty("data");
    private static string Code(JsonElement envelope)=>envelope.GetProperty("error").GetProperty("code").GetString()!;

    private async Task<JsonElement> Call(string name,Dictionary<string,object?> arguments,bool allowError=false,CancellationToken cancellation=default)
    {
        int number=Interlocked.Increment(ref calls);
        Save($"{number:D4}-{name}-request.json",arguments);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var reply=await client.CallToolAsync(name,arguments,cancellationToken:deadline.Token);
            Save($"{number:D4}-{name}-result.json",reply);
            using var document=JsonDocument.Parse(string.Join("\n",reply.Content.OfType<TextContentBlock>().Select(b=>b.Text)));
            var envelope=document.RootElement.Clone();
            if(Ok(envelope))successes.AddOrUpdate(name,1,(_,count)=>count+1);
            else rejections.AddOrUpdate(name,1,(_,count)=>count+1);
            if(!allowError && !Ok(envelope))throw new Exception($"{name} failed (no TX retry): {envelope}");
            return envelope;
        }
        catch(McpException error) when(allowError)
        {
            Save($"{number:D4}-{name}-protocol-error.json",new{type=error.GetType().FullName,error.Message});
            // Protocol invalid-parameter/unknown-tool responses are expected only
            // in explicitly invalid requests. Transport failures are not passes.
            Need(error.Message.Contains("parameter",StringComparison.OrdinalIgnoreCase)||error.Message.Contains("argument",StringComparison.OrdinalIgnoreCase)||
                error.Message.Contains("tool",StringComparison.OrdinalIgnoreCase)||error.Message.Contains("invalid",StringComparison.OrdinalIgnoreCase),"Unexpected MCP protocol failure: "+error.Message);
            rejections.AddOrUpdate(name,1,(_,count)=>count+1);
            return JsonSerializer.SerializeToElement(new{success=false,error=new{code="mcp_protocol_error",message=error.Message}});
        }
        catch(OperationCanceledException)
        {
            Save($"{number:D4}-{name}-client-canceled.json",new{canceled=true,reason="SDK request cancellation; no automatic TX retry"});
            throw;
        }
    }
    private async Task ExpectError(string name,Dictionary<string,object?> arguments,params string[] codes)
    {
        var result=await Call(name,arguments,true);
        Need(!Ok(result),"Invalid request unexpectedly succeeded: "+name);
        if(codes.Length>0)Need(codes.Contains(Code(result)),"Unexpected error code for "+name+": "+Code(result));
    }
    private async Task<JsonElement> State()=>Data(await Call("serial_get_status",Target()));
    private async Task WaitIdle()
    {
        for(int i=0;i<60;i++) { if(!(await State()).GetProperty("busy").GetBoolean())return;await Task.Delay(50); }
        throw new Exception("Target did not return idle");
    }
    private async Task WaitBusy()
    {
        for(int i=0;i<40;i++) { if((await State()).GetProperty("busy").GetBoolean())return;await Task.Delay(25); }
        throw new Exception("Owned test operation did not become busy");
    }

    private async Task<List<JsonElement>> ListAll(string tool,string property,int limit=1)
    {
        var list=new List<JsonElement>();int offset=0;
        do
        {
            var page=Data(await Call(tool,Target(("offset",offset),("limit",limit))));
            foreach(var item in page.GetProperty(property).EnumerateArray())list.Add(item.Clone());
            if(!page.GetProperty("hasMore").GetBoolean())break;
            int next=page.GetProperty("nextOffset").GetInt32();Need(next>offset,"List cursor did not advance");offset=next;
        }while(true);
        return list;
    }
    private async Task<byte[]> Read(JsonElement data,string? format=null,int limit=4096,string expectedStatus="completed",bool startAtZero=false)
    {
        var capture=data.GetProperty("capture");string id=capture.GetProperty("captureId").GetString()!;
        Need(capture.GetProperty("captureStatus").GetString()==expectedStatus,"Unexpected capture state: "+capture);
        Need(!capture.GetProperty("rxGapDetected").GetBoolean()&&capture.GetProperty("droppedBytes").GetInt64()==0&&capture.GetProperty("serialErrors").GetInt64()==0,"RX gap/drop/serial error");
        JsonElement page=data.GetProperty("page");
        if(startAtZero)
        {
            var read=Target(("captureId",id),("offset",0),("limit",limit));if(format is not null)read["format"]=format;
            page=Data(await Call("serial_read_capture",read)).GetProperty("page");
        }
        string selected=page.GetProperty("format").GetString()!;
        if(format is not null)Need(selected==format,"Capture format mismatch");
        using var stream=new MemoryStream();int pages=0;long offset=0;
        while(true)
        {
            Need(page.GetProperty("offset").GetInt64()==offset,"Capture page cursor mismatch");
            byte[] bytes=selected=="hex"?Convert.FromHexString(string.Concat(page.GetProperty("text").GetString()!.Where(c=>!char.IsWhiteSpace(c)))):
                new UTF8Encoding(false,true).GetBytes(page.GetProperty("text").GetString()!);
            stream.Write(bytes);pages++;
            long next=page.GetProperty("nextOffset").GetInt64();Need(next-offset==bytes.Length,"Page byte offset differs from data length");offset=next;
            if(!page.GetProperty("hasMore").GetBoolean())break;
            Need(bytes.Length>0,"RX page made no progress");
            var read=Target(("captureId",id),("offset",offset),("limit",limit));
            // Omission deliberately tests retaining the initial preview format.
            if(format is not null)read["format"]=format;
            page=Data(await Call("serial_read_capture",read)).GetProperty("page");
        }
        byte[] result=stream.ToArray();
        Need(result.LongLength==page.GetProperty("totalBytes").GetInt64(),"Incomplete capture tail");
        if(selected=="hex")Need(result.LongLength==capture.GetProperty("storedBytes").GetInt64(),"Raw RX byte count mismatch");
        File.WriteAllBytes(Path.Combine(directory,id+"-"+selected+".bin"),result);
        Save(id+"-capture.json",capture);captures.Add(new{captureId=id,format=selected,bytes=result.Length,pages,status=expectedStatus});return result;
    }
    private async Task<JsonElement> RequestData(string command,string tx="text",int duration=500,string? format="hex",int preview=64,string ending="crlf")
    {
        var request=Target(("port","COM3"),("command",command),("txMode",tx),("lineEnding",ending),("durationMs",duration),("previewBytes",preview));
        if(format is not null)request["format"]=format;
        return Data(await Call("serial_send_and_capture",request));
    }
    private async Task<byte[]> Request(string command,string tx="text",int duration=500,string? format="hex",int preview=64,string ending="crlf")=>
        await Read(await RequestData(command,tx,duration,format,preview,ending));
    private static void Text(byte[] actual,string expected)=>SerialLabWire.AssertBytes(actual,Encoding.UTF8.GetBytes(expected));
    private async Task<long> DeviceCommands()
    {
        string status=Encoding.UTF8.GetString(await Request("STATUS?"));
        var match=Regex.Match(status,@"\bcommands=(\d+)\b");Need(match.Success,"Missing device command counter");return long.Parse(match.Groups[1].Value);
    }
    private object Sequence(string name,string token,int repeat=1,bool wait=false)=>new {
        name,repeatCount=repeat,steps=new[]{new {
            name="Owned test PING",command="PING "+token,txMode="text",lineEnding="crlf",captureMs=wait?8000:200,delayAfterMs=0,
            expect=new{kind="text",value="PONG "+token}
        }}
    };

    public async Task RunAsync()
    {
        try
        {
            var tools=await client.ListToolsAsync();foreach(var tool in tools)toolNames.Add(tool.Name);
            Save("tools.json",tools.Select(t=>new{t.Name,t.Description}));
            var capabilities=Data(await Call("serial_get_capabilities",new()));Save("capabilities.json",capabilities);
            Save("plugin.json",JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"plugin.json"))).RootElement);
            var instances=Data(await Call("serial_list_instances",new())).GetProperty("instances");
            var found=new List<(string Id,JsonElement State)>();
            foreach(var entry in instances.EnumerateArray())
            {
                string id=entry.GetProperty("instanceId").GetString()!;
                var reply=await Call("serial_get_status",new(){["instanceId"]=id},true);if(!Ok(reply))continue;
                var state=Data(reply);
                if((state.GetProperty("connected").GetBoolean()||phase=="user-setup")&&state.GetProperty("port").GetString()=="COM3")found.Add((id,state));
            }
            Need(found.Count==1,"Require exactly one GUI already connected to COM3");instance=found[0].Id;initialState=found[0].State;
            if(phase!="discover")Need(!initialState.GetProperty("busy").GetBoolean(),"COM3 GUI is busy with unrelated work");
            if(phase=="user-setup"&&!initialState.GetProperty("connected").GetBoolean())
            {
                await Call("serial_connect",Target(("port","COM3"),("baudRate",115200)));
                initialState=await State();
            }
            Need(initialState.GetProperty("baudRate").GetInt32()==115200&&initialState.GetProperty("dataBits").GetInt32()==8&&
                initialState.GetProperty("parity").GetString()=="None"&&initialState.GetProperty("stopBits").GetString()=="One"&&initialState.GetProperty("handshake").GetString()=="None","Require 115200/8N1/no handshake");
            originalMode=initialState.GetProperty("mode").GetString()!;originalTimeout=initialState.GetProperty("byteTimeoutMs").GetInt32();Save("initial-status.json",initialState);
            acquired=phase!="discover";
            Console.WriteLine("Initial state "+initialState.GetRawText());
            Pass($"discover {toolNames.Count} MCP tools and unique idle COM3 GUI {instance}");
            if(phase=="safe")await SafeAsync();
            if(phase=="state")await StateChangesAsync();
            if(phase=="cancel")await CaptureCancellationAndBusy("F103RB LAB - CLI smoke");
            if(phase=="remaining")await RemainingAsync();
            if(phase=="quick")await QuickCommandFixturesAsync();
            if(phase=="user-setup")
            {
                await WaitIdle();
                Save("previous-firmware-build.json",new{response=Encoding.UTF8.GetString(await Request("BUILD?"))});
                await WaitIdle();Pass("onboarding COM3 connection and previous firmware BUILD response");
            }
            if(phase=="user-monitor")await UserMonitorAsync();
            if(phase=="user-observe")await UserObserveAsync();
            if(phase=="user-status")
            {
                await WaitIdle();
                string reply=Encoding.UTF8.GetString(await Request("MONITOR?",duration:500));
                await WaitIdle();
                var state=Regex.Match(reply,@"MONITOR enabled=1 heartbeat_ms=1000 debounce_ms=20 user=(PRESSED|RELEASED) presses=(\d+) releases=(\d+) missed=0\r\n");
                Need(state.Success,"Monitor is not running cleanly");
                uint presses=uint.Parse(state.Groups[2].Value),releases=uint.Parse(state.Groups[3].Value);
                Save("physical-button-counters.json",new{presses,releases,user=state.Groups[1].Value,reply,physicalPressReleaseObserved=presses>0&&releases>0});
                Need(presses>0&&releases>0,"No physical button press/release counters after user confirmation");
                Pass($"live PC13 counters confirm {presses} physical presses and {releases} releases; monitor remains ON");
            }
        }
        catch(Exception error){failure=error.ToString();Console.Error.WriteLine(failure);}
        finally
        {
            if(acquired)
            {
                try
                {
                    if(activeRun!="") { await Call("serial_sequence_cancel",Target(("runId",activeRun)));await WaitIdle();activeRun=""; }
                    if(standaloneCapture) { await Call("serial_cancel_capture",Target());await WaitIdle();standaloneCapture=false; }
                    if(disconnected) { await Call("serial_connect",Target(("port","COM3"),("baudRate",115200)));disconnected=false; }
                    foreach(string name in ownedSequences.ToArray())
                    { await Call("serial_sequence_delete",Target(("name",name)));ownedSequences.Remove(name); }
                    if(phase!="quick"&&!phase.StartsWith("user-",StringComparison.Ordinal))await Call("serial_configure",Target(("port","COM3"),("byteTimeoutMs",originalTimeout)));
                    if((await State()).GetProperty("mode").GetString()!=originalMode)
                        await Call("serial_set_mode",Target(("mode",originalMode)));
                    Save("final-status.json",await State());
                }
                catch(Exception cleanup){failure??=cleanup.ToString();Console.Error.WriteLine("Cleanup: "+cleanup.Message);}
            }
            var coverage=toolNames.Order().Select(name=>new{name,positive=successes.GetValueOrDefault(name),negative=rejections.GetValueOrDefault(name)}).ToArray();
            Save("report.json",new{phase,path="actual plugin manifest -> plugin launcher -> MCP -> GUI -> COM3",instanceId=instance,
                cases,observations,failure,calls,captures,coverage,uncovered=coverage.Where(c=>c.positive==0).Select(c=>c.name),ownedSequencesRemaining=ownedSequences});
            Console.WriteLine("Evidence: "+directory);
        }
        if(failure is not null)Environment.ExitCode=1;else Console.WriteLine($"PASS {cases.Count} {phase} checks; {calls} plugin MCP calls");
    }

    private async Task<byte[]> ObserveUser(int duration)
    {
        await WaitIdle();
        var response=Data(await Call("serial_capture_rx",Target(("port","COM3"),("durationMs",duration),("previewBytes",64),("format","hex"))));
        byte[] bytes=await Read(response);
        await WaitIdle();return bytes;
    }
    private void CheckHeartbeat(byte[] bytes,int minimum)
    {
        var rows=Regex.Matches(Encoding.UTF8.GetString(bytes),@"INFO HEARTBEAT seq=(\d+) uptime_ms=(\d+) user=(PRESSED|RELEASED) presses=\d+ releases=\d+ rx_drops=0 uart_errors=0 missed=0\r\n");
        Need(rows.Count>=minimum,"Missing clean heartbeat records");
        for(int i=1;i<rows.Count;i++)
        {
            uint previous=uint.Parse(rows[i-1].Groups[1].Value),current=uint.Parse(rows[i].Groups[1].Value);
            uint delta=unchecked(uint.Parse(rows[i].Groups[2].Value)-uint.Parse(rows[i-1].Groups[2].Value));
            Need(unchecked(current-previous)==1&&delta>=990&&delta<=1010,"Heartbeat sequence/timing mismatch");
        }
        Pass($"{rows.Count} consecutive clean heartbeat records, 1000ms firmware timestamp spacing");
    }
    private async Task UserMonitorAsync()
    {
        CheckHeartbeat(await ObserveUser(3500),3); // Boot-on behavior, before MONITOR ON.
        Need(Encoding.UTF8.GetString(await Request("STOP",duration:200)).Contains("OK STOP\r\n"),"STOP ACK missing");
        await WaitIdle();
        string manifest=Path.GetFullPath(Path.Combine(root,"..","..","firmware","nucleo-f103rb-serial-lab","build-user-monitor","manifest.json"));
        using var identity=JsonDocument.Parse(File.ReadAllText(manifest));Save("firmware-manifest.json",identity.RootElement);
        string build=Encoding.UTF8.GetString(await Request("BUILD?",duration:200));await WaitIdle();
        Need(build.Contains("version="+identity.RootElement.GetProperty("firmwareVersion").GetString())&&
            build.Contains("id="+identity.RootElement.GetProperty("buildId").GetString()),"Running firmware differs from flashed image");
        Pass("running firmware version/build ID matches new image manifest");
        Need(Encoding.UTF8.GetString(await Request("MONITOR?",duration:200)).Contains("MONITOR enabled=0"),"STOP did not disable monitor");await WaitIdle();
        Text(await Request("MONITOR OFF",duration:200),"OK MONITOR OFF\r\n");await WaitIdle();
        Need((await ObserveUser(1300)).Length==0,"MONITOR OFF did not suppress background output");
        Pass("STOP/MONITOR OFF keep precision serial tests quiet");
        Text(await Request("MONITOR ON extra",duration:200),"ERR ARGS MONITOR ON|OFF\r\n");await WaitIdle();
        string button=Encoding.UTF8.GetString(await Request("BUTTON?",duration:200));await WaitIdle();
        Need(Regex.IsMatch(button,@"^BUTTON [01]\r\n$"),"PC13 state query failed");Pass("physical PC13 button state query");
        byte[] payload=Enumerable.Range(0,256).Select(i=>(byte)i).ToArray();
        SerialLabWire.AssertBytes(await Request(SerialLabWire.Hex(SerialLabWire.Encode(2,1300,payload)),"hex",duration:300,ending:"none"),SerialLabWire.Encode(0x82,1300,payload));
        await WaitIdle();Pass("existing 00..FF binary CRC echo remains byte-exact with monitor off");
        Text(await Request("MONITOR ON",duration:200),"OK MONITOR ON\r\n");await WaitIdle();
        CheckHeartbeat(await ObserveUser(3500),3);
        Pass("monitor left ON for user; GUI display mode and byte timeout preserved");
    }
    private async Task UserObserveAsync()
    {
        var existing=await ListAll("serial_sequence_list","sequences");
        var presets=new[]{
            (Name:"F103RB USER - Monitor ON",Command:"MONITOR ON"),
            (Name:"F103RB USER - Monitor OFF",Command:"MONITOR OFF"),
            (Name:"F103RB USER - Button status",Command:"MONITOR?")
        };
        foreach(var preset in presets)
        {
            if(existing.Any(item=>item.GetProperty("name").GetString()==preset.Name))
            {
                var current=Data(await Call("serial_sequence_get",Target(("name",preset.Name))));
                var steps=current.GetProperty("steps");
                Need(current.GetProperty("repeatCount").GetInt32()==1&&steps.GetArrayLength()==1&&
                    steps[0].GetProperty("command").GetString()==preset.Command&&steps[0].GetProperty("txMode").GetString()=="text"&&
                    steps[0].GetProperty("lineEnding").GetString()=="crlf","Existing USER preset differs; no automatic overwrite");
            }
            else
            {
                await Call("serial_sequence_save",Target(("sequence",new{
                    name=preset.Name,repeatCount=1,steps=new[]{new{
                        name=preset.Command,command=preset.Command,txMode="text",lineEnding="crlf",captureMs=500,delayAfterMs=0,
                        expect=new{kind="text",value=preset.Command=="MONITOR?"?"MONITOR enabled=":"OK "+preset.Command}
                    }}
                })));
                // User-facing reusable presets are intentionally retained, not test-owned cleanup data.
            }
        }
        Save("retained-user-presets.json",presets.Select(p=>new{p.Name,p.Command}));
        Pass("three reusable USER monitor sequences saved/reused without executing or overwriting user definitions");
        byte[] bytes=await ObserveUser(25000);CheckHeartbeat(bytes,23);
        string text=Encoding.UTF8.GetString(bytes);
        int presses=Regex.Matches(text,@"INFO USER PRESS count=\d+ uptime_ms=\d+\r\n").Count;
        int releases=Regex.Matches(text,@"INFO USER RELEASE count=\d+ held_ms=\d+ uptime_ms=\d+\r\n").Count;
        Save("physical-button-observation.json",new{windowMs=25000,presses,releases,physicalPressReleaseObserved=presses>0&&releases>0});
        observations.Add($"Physical button observation: {presses} presses, {releases} releases. No synthetic GPIO changes or simulated physical presses.");
        Console.WriteLine($"USER OBSERVATION: {presses} presses, {releases} releases in 25 seconds");
    }

    private async Task QuickCommandFixturesAsync()
    {
        await WaitIdle();
        Need(Encoding.UTF8.GetString(await Request("ID?")).Contains("ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1"),"Wrong firmware");
        await WaitIdle();
        Pass("NUCLEO-F103RB lab firmware identity");
        string fixturePath=Path.GetFullPath(Path.Combine(root,"..","..","firmware","nucleo-f103rb-serial-lab","results","quickcmd-created-cases.json"));
        using var fixtures=JsonDocument.Parse(File.ReadAllText(fixturePath));
        Save("model-created-fixtures.json",fixtures.RootElement);
        var before=await ListAll("serial_list_commands","commands");Save("quick-command-catalog-before.json",before);
        foreach(var fixture in fixtures.RootElement.GetProperty("cases").EnumerateArray())
        {
            string name=fixture.GetProperty("name").GetString()!;
            string command=fixture.GetProperty("command").GetString()!;
            string mode=fixture.GetProperty("txMode").GetString()!;
            string ending=fixture.GetProperty("lineEnding").GetString()!;
            byte[] expected=Convert.FromHexString(fixture.GetProperty("expectedRxHex").GetString()!);
            await WaitIdle();
            byte[] actual=await Request(command,tx:mode,ending:ending,preview:4);
            await WaitIdle();
            SerialLabWire.AssertBytes(actual,expected);
            Pass($"{name}: persisted model fixture replay via plugin, {mode}, complete {actual.Length}-byte RX matches");
        }
        var after=await ListAll("serial_list_commands","commands");Save("quick-command-catalog-after.json",after);
        Need(JsonSerializer.Serialize(before)==JsonSerializer.Serialize(after),"Existing GUI Quick CMD catalog changed unexpectedly");
        Need((await State()).GetProperty("mode").GetString()==originalMode,"GUI mode changed unexpectedly");
        Pass("existing GUI Quick CMD catalog and display mode preserved");
        observations.Add("The real app model validates Quick CMD creation and ProfileService saves/loads test-only commands in the preceding xUnit check. This phase replays those persisted definitions through actual plugin MCP, not a GUI Quick CMD button click.");
        observations.Add("Live plugin has list_commands only for Quick CMD; no create/edit/delete/execute-by-name API. GUI creation/button automation was blocked by window-owner validation. Those UI actions are NOT counted as passed.");
    }

    private async Task SafeAsync()
    {
        Need(Encoding.UTF8.GetString(await Request("ID?")).Contains("ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1"),"Wrong firmware");
        Text(await Request("STOP"),"OK STOP\r\n");Text(await Request("FAULT CLEAR"),"OK FAULT CLEAR\r\n");
        Pass("DUT identity and deterministic test baseline");
        var ports=Data(await Call("serial_list_ports",Target())).GetProperty("ports").EnumerateArray().Select(p=>p.GetString()).ToArray();Need(ports.Contains("COM3"),"COM3 missing from ports");
        Pass("port enumeration");
        var commands=await ListAll("serial_list_commands","commands");Save("saved-command-snapshot.json",commands);
        await Call("serial_list_commands",Target(("offset",commands.Count),("limit",100)));
        Pass($"saved command list: all {commands.Count} records, limit=1 paging and end cursor");
        var existing=await ListAll("serial_sequence_list","sequences");Save("sequence-snapshot-before.json",existing);
        Pass($"sequence list: all {existing.Count} records via limit=1 paging");
        long generation=initialState.GetProperty("sessionGeneration").GetInt64();
        await Call("serial_connect",Target(("port","COM3"),("baudRate",115200)));
        Need((await State()).GetProperty("sessionGeneration").GetInt64()==generation,"Matching connect unexpectedly replaced serial session");
        await Call("serial_set_mode",Target(("mode",originalMode)));Pass("matching connect and same-mode set are idempotent");

        foreach(int timeout in new[]{1,5000,originalTimeout})
        {
            await Call("serial_configure",Target(("port","COM3"),("byteTimeoutMs",timeout)));
            Need((await State()).GetProperty("byteTimeoutMs").GetInt32()==timeout,"Timeout readback mismatch");
        }
        Pass("live byte timeout min=1/max=5000 and restore, without a mode switch");
        await ChangeBaud(230400);await ChangeBaud(115200);Pass("MCP baud change on device and host 115200 -> 230400 -> 115200");

        var defaultData=Data(await Call("serial_send_and_capture",Target(("port","COM3"),("command","PING defaults"),("durationMs",200))));
        Need(defaultData.GetProperty("page").GetProperty("format").GetString()=="text","Text request default preview was not text");Text(await Read(defaultData),"PONG defaults\r\n");
        foreach(string ending in new[]{"cr","lf","crlf"})Text(await Request("PING "+ending,ending:ending),"PONG "+ending+"\r\n");
        Text(await Request("PING none\n",ending:"none"),"PONG none\r\n");Pass("default text preview and CR/LF/CRLF/None line endings");
        string unicode="ECHO   한글 🚀 \"quote\" $() `literal` \\  ";
        var utf8=await RequestData(unicode,format:"text",preview:4);
        var textBytes=await Read(utf8,limit:4);Text(textBytes,unicode+"\r\n");
        var rawBytes=await Read(utf8,format:"hex",limit:4,startAtZero:true);SerialLabWire.AssertBytes(rawBytes,textBytes);
        Pass("UTF-8/whitespace/quotes survive 4-byte text pages, omitted format, and HEX reread from offset 0");
        byte[] payload=Enumerable.Range(0,256).Select(i=>(byte)i).ToArray();
        var request=SerialLabWire.Encode(2,1234,payload);var expected=SerialLabWire.Encode(0x82,1234,payload);
        var binary=await RequestData(SerialLabWire.Hex(request),"hex",format:null,ending:"crlf");
        Need(binary.GetProperty("page").GetProperty("format").GetString()=="hex","HEX default preview was not hex");
        SerialLabWire.AssertBytes(await Read(binary),expected);Pass("all 256 binary values, default HEX preview, CRC and ignored HEX lineEnding");
        var burst=await Request("BURST 150 32",duration:2000,preview:4);
        SerialLabWire.AssertTextRecords(burst,"BURST",150,0,0);SerialLabWire.AssertBurstPayload(burst,32);Pass("full paged burst capture: 150 exact records");
        var silence=await RequestData("SILENT",duration:0,preview:65536);Need((await Read(silence)).Length==0,"SILENT zero-window capture was not empty");
        Pass("capture duration minimum 0 and preview maximum 65536");

        foreach(var (tx,word) in new[]{("text","SEND-TEXT"),("hex","SEND-HEX")})
        {
            string command="DELAY 800 "+word;
            if(tx=="hex")command=SerialLabWire.Hex(Encoding.ASCII.GetBytes(command+"\r\n"));
            var sent=Data(await Call("serial_send",Target(("port","COM3"),("command",command),("txMode",tx),("lineEnding","crlf"),("format","hex"))));
            Need(sent.GetProperty("capture").GetProperty("sendStatus").GetString()=="sent","serial_send did not send");await Read(sent);
            var observed=Data(await Call("serial_capture_rx",Target(("port","COM3"),("durationMs",1300),("previewBytes",4),("format","hex"))));
            Need(observed.GetProperty("capture").GetProperty("sendStatus").GetString()=="not_requested","Observe unexpectedly sent a command");
            Text(await Read(observed),word+"\r\n");
        }
        Pass("serial_send text/HEX followed by RX-only capture, verified delayed device replies");

        string normalName=tag+" normal",cancelName=tag+" cancel";
        long beforeSave=await DeviceCommands();
        var saved=Data(await Call("serial_sequence_save",Target(("sequence",Sequence(normalName,"BEFORE")))));
        ownedSequences.Add(normalName);Need(saved.GetProperty("persisted").GetBoolean()&&!saved.GetProperty("replaced").GetBoolean(),"New sequence was not persisted");
        Need(await DeviceCommands()==beforeSave+1,"Registering sequence transmitted device data");Pass("sequence save persists a new owned definition without TX");
        await ExpectError("serial_sequence_save",Target(("sequence",Sequence(normalName,"WRONG"))),"sequence_exists");
        var current=Data(await Call("serial_sequence_get",Target(("name",normalName))));Need(current.GetProperty("steps")[0].GetProperty("command").GetString()=="PING BEFORE","Rejected duplicate altered definition");
        Pass("duplicate sequence rejected and original definition preserved");
        var complete=new {name=normalName,repeatCount=2,steps=new object[]{
            new{name="Match",command="PING REPEAT",txMode="text",lineEnding="crlf",captureMs=150,delayAfterMs=0,expect=new{kind="text",value="PONG REPEAT"}},
            new{name="Expected miss",command="SILENT",txMode="text",lineEnding="crlf",captureMs=100,delayAfterMs=0,expect=new{kind="text",value="MUST-NOT-ARRIVE"}},
            new{name="Continue after miss",command="PING CONTINUE",txMode="text",lineEnding="crlf",captureMs=150,delayAfterMs=0,expect=new{kind="text",value="PONG CONTINUE"}}
        }};
        var replaced=Data(await Call("serial_sequence_save",Target(("sequence",complete),("overwrite",true))));Need(replaced.GetProperty("replaced").GetBoolean(),"Explicit overwrite not applied");
        Pass("explicit overwrite replaces only suite-owned sequence");
        long beforeRun=await DeviceCommands();var started=Data(await Call("serial_sequence_start",Target(("name",normalName),("port","COM3"))));activeRun=started.GetProperty("runId").GetString()!;
        var run=await WaitRun(activeRun);Need(run.GetProperty("state").GetString()=="completed","Normal repeated run failed");activeRun="";
        var steps=run.GetProperty("steps").EnumerateArray().ToArray();Need(steps.Length==6,"Repeat did not execute exactly six steps");
        for(int i=0;i<6;i++)Need(steps[i].GetProperty("matchStatus").GetString()==(i%3==1?"not_matched":"matched"),"Unexpected exact-match verdict");
        Need(await DeviceCommands()==beforeRun+7,"Sequence retried or omitted a TX");await ReadRun(run);
        var latest=Data(await Call("serial_sequence_status",Target()));Need(latest.GetProperty("runId").GetString()==run.GetProperty("runId").GetString(),"Latest run lookup wrong");
        var idleCancel=Data(await Call("serial_sequence_cancel",Target(("runId",run.GetProperty("runId").GetString()!))));Need(!idleCancel.GetProperty("canceled").GetBoolean(),"Completed run cancel should be false");
        Pass("background run/status/latest, repeats, expected miss continues, no retry, complete RX and idle cancel");

        await Call("serial_sequence_save",Target(("sequence",Sequence(cancelName,"CANCEL",wait:true))));ownedSequences.Add(cancelName);
        await CaptureCancellationAndBusy(normalName);
        await SequenceCancellationAndReconnect(cancelName);
        await ErrorMatrix(normalName,binary.GetProperty("capture").GetProperty("captureId").GetString()!);

        foreach(string name in ownedSequences.ToArray())
        {
            var deleted=Data(await Call("serial_sequence_delete",Target(("name",name))));Need(deleted.GetProperty("deleted").GetBoolean()&&deleted.GetProperty("persisted").GetBoolean(),"Delete not persisted");ownedSequences.Remove(name);
            await ExpectError("serial_sequence_get",Target(("name",name)),"sequence_not_found");
            await ExpectError("serial_sequence_delete",Target(("name",name)),"sequence_not_found");
        }
        var after=await ListAll("serial_sequence_list","sequences");Save("sequence-snapshot-after.json",after);
        Need(existing.Select(x=>x.GetRawText()).SequenceEqual(after.Select(x=>x.GetRawText())),"Pre-existing sequence metadata changed");
        Pass("delete own sequences, reject missing names, preserve all pre-existing sequence metadata");
        Text(await Request("PING full-suite-ready"),"PONG full-suite-ready\r\n");Pass("same-mode final communication and idle recovery");
    }

    private async Task ChangeBaud(int rate)
    {
        Need(Encoding.UTF8.GetString(await Request("BAUD "+rate,duration:200)).Contains("OK BAUD "+rate),"Missing old-baud transition ACK");
        await Call("serial_configure",Target(("port","COM3"),("baudRate",rate)));
        await Read(Data(await Call("serial_capture_rx",Target(("port","COM3"),("durationMs",1100),("format","hex")))));
        Text(await Request("PING baud-"+rate),"PONG baud-"+rate+"\r\n");
        Need((await State()).GetProperty("baudRate").GetInt32()==rate,"Host baud readback wrong");
    }
    private async Task<JsonElement> WaitRun(string runId)
    {
        for(int i=0;i<100;i++)
        {
            var run=Data(await Call("serial_sequence_status",Target(("runId",runId))));
            if(run.GetProperty("state").GetString()!="running")return run;await Task.Delay(50);
        }
        throw new Exception("Owned run did not complete/cancel in time");
    }
    private async Task ReadRun(JsonElement run)
    {
        var capture=run.GetProperty("capture");string id=capture.GetProperty("captureId").GetString()!;
        var data=Data(await Call("serial_read_capture",Target(("captureId",id),("offset",0),("limit",4),("format","hex"))));
        await Read(data,format:"hex",expectedStatus:capture.GetProperty("captureStatus").GetString()!);
    }

    private async Task CaptureCancellationAndBusy(string sequenceName)
    {
        long before=await DeviceCommands();standaloneCapture=true;
        var pending=Call("serial_capture_rx",Target(("port","COM3"),("durationMs",60000),("format","hex")),true);
        await WaitBusy();
        await ExpectError("serial_send",Target(("port","COM3"),("command","PING MUST-NOT-SEND")),"busy");
        await ExpectError("serial_send_and_capture",Target(("port","COM3"),("command","PING MUST-NOT-SEND")),"busy");
        await ExpectError("serial_configure",Target(("port","COM3"),("byteTimeoutMs",40)),"busy");
        await ExpectError("serial_set_mode",Target(("mode",originalMode)),"busy");
        await ExpectError("serial_disconnect",Target(),"busy");
        await ExpectError("serial_sequence_start",Target(("name",sequenceName),("port","COM3")),"busy");
        var canceled=Data(await Call("serial_cancel_capture",Target()));Need(canceled.GetProperty("canceled").GetBoolean(),"Active capture was not canceled");
        var result=await pending;Need(!Ok(result)&&Code(result)=="canceled","Canceled capture did not retain canceled result");
        await Read(Data(result),expectedStatus:"canceled");standaloneCapture=false;await WaitIdle();
        Need(await DeviceCommands()==before+1,"Busy rejection transmitted a device command");
        var again=Data(await Call("serial_cancel_capture",Target()));Need(!again.GetProperty("canceled").GetBoolean(),"Idle capture cancel should be false");
        Pass("60s capture upper bound, six busy guards, explicit cancel with RX evidence, idle cancel and zero rejected TX");
        standaloneCapture=true;using var cancellation=new CancellationTokenSource();
        var sdkPending=Call("serial_capture_rx",Target(("port","COM3"),("durationMs",60000),("format","hex")),true,cancellation.Token);
        await WaitBusy();cancellation.Cancel();
        bool stopped=false;try{await sdkPending;}catch(OperationCanceledException){stopped=true;}
        Need(stopped,"SDK cancellation did not cancel the request");
        await Task.Delay(150);
        if((await State()).GetProperty("busy").GetBoolean())
        {
            // Inspect the SDK's own outgoing trace rather than inventing an ID.
            // SendNotificationAsync is the SDK's typed notification API; no
            // hand-written JSON-RPC is used for this interoperability check.
            string line=File.ReadLines(Path.Combine(directory,"sdk-wire.log")).Last(s=>s.Contains("sending method 'tools/call' request")&&s.Contains("\"name\":\"serial_capture_rx\""));
            var match=Regex.Match(line,"\\\"id\\\":([0-9]+)");Need(match.Success,"Could not identify own SDK capture request ID");
            long requestId=long.Parse(match.Groups[1].Value);
            bool automaticNotification=File.ReadLines(Path.Combine(directory,"sdk-wire.log")).Any(s=>s.Contains("sending message")&&s.Contains("notifications/cancelled"));
            observations.Add("SDK CancellationToken canceled its local await but GUI remained busy; automatic notifications/cancelled observed="+automaticNotification+". Explicit typed notification tested separately.");
            Save("sdk-token-cancel-observation.json",new{localAwaitCanceled=true,guiStillBusy=true,automaticNotification,requestId});
            await client.SendNotificationAsync(NotificationMethods.CancelledNotification,new CancelledNotificationParams{RequestId=new RequestId(requestId),Reason="Cancel this suite-owned RX capture"});
        }
        await WaitIdle();standaloneCapture=false;
        Text(await Request("PING after-sdk-cancel"),"PONG after-sdk-cancel\r\n");Pass("typed MCP cancellation notification stops owned capture and same MCP session recovers");
    }
    private async Task SequenceCancellationAndReconnect(string name)
    {
        // A second official SDK client uses the very same plugin manifest. Only
        // its transport is closed; the GUI and target connection stay running.
        await using(var other=await CreateAsync(root,Path.Combine(directory,"background-client"),"discover"))
        {
            other.instance=instance;
            var start=Data(await other.Call("serial_sequence_start",other.Target(("name",name),("port","COM3"))));activeRun=start.GetProperty("runId").GetString()!;
            other.Save("accepted-run.json",start);
        }
        var running=Data(await Call("serial_sequence_status",Target(("runId",activeRun))));Need(running.GetProperty("state").GetString()=="running","Sequence did not survive MCP client disconnect");
        await ExpectError("serial_cancel_capture",Target(),"busy");
        await ExpectError("serial_send",Target(("port","COM3"),("command","PING MUST-NOT-SEND")),"busy");
        await ExpectError("serial_disconnect",Target(),"busy");
        await ExpectError("serial_sequence_start",Target(("name",name),("port","COM3")),"busy");
        var canceled=Data(await Call("serial_sequence_cancel",Target(("runId",activeRun))));Need(canceled.GetProperty("canceled").GetBoolean(),"Sequence was not canceled");
        var finished=await WaitRun(activeRun);Need(finished.GetProperty("state").GetString()=="canceled","Canceled run state wrong");activeRun="";
        await ReadRun(finished);await WaitIdle();Pass("background run survives MCP client EOF, sequence owns capture, exact-run cancel and partial RX retained");
    }

    private async Task ErrorMatrix(string sequenceName,string captureId)
    {
        long before=await DeviceCommands();
        var invalid=new (string Tool,Dictionary<string,object?> Args)[]{
            ("serial_get_capabilities",new(){["unexpected"]=true}),("serial_list_instances",new(){["unexpected"]=true}),
            ("serial_get_status",Target(("instanceId",null))),("serial_list_ports",Target(("instanceId","missing-mcp-test-instance"))),
            ("serial_list_commands",Target(("limit",0))),("serial_connect",Target(("port","COM3"),("baudRate",0))),
            ("serial_disconnect",Target(("instanceId","missing-mcp-test-instance"))),("serial_set_mode",Target(("mode","invalid-mode"))),
            ("serial_configure",Target()),("serial_sequence_list",Target(("offset",-1))),
            ("serial_sequence_get",Target()),("serial_sequence_save",Target(("sequence",new{name="Invalid",repeatCount=0,steps=Array.Empty<object>()}))),
            ("serial_sequence_delete",Target(("name",tag+" absent"))),("serial_sequence_start",Target(("name",sequenceName),("port","COM4"))),
            ("serial_sequence_status",Target(("runId","missing-mcp-run"))),("serial_sequence_cancel",Target()),
            ("serial_send",Target(("command",""))),("serial_send_and_capture",Target(("command","PING"),("txMode","invalid"))),
            ("serial_capture_rx",Target(("durationMs",-1))),("serial_read_capture",Target(("captureId",new string('0',32)))),
            ("serial_cancel_capture",Target(("instanceId","missing-mcp-test-instance")))
        };
        foreach(var test in invalid)await ExpectError(test.Tool,test.Args);
        Pass("invalid input/target/run/capture rejection for every discovered tool (21 tools)");
        foreach(var args in new[]{Target(("byteTimeoutMs",0)),Target(("byteTimeoutMs",5001)),Target(("baudRate",10000001)),Target(("baudRate",115200.5)),Target(("baudRate","115200"))})
            await ExpectError("serial_configure",args,"invalid_argument","mcp_protocol_error");
        foreach(var args in new[]{Target(("command","GG"),("txMode","hex")),Target(("command","PING"),("previewBytes",3)),
            Target(("command","PING"),("previewBytes",65537)),Target(("command","PING"),("durationMs",60001)),
            Target(("command","PING"),("lineEnding","invalid")),Target(("command","PING"),("porrt","COM3")),
            Target(("command",new string('X',16385))),Target(("command","PING"),("port","COM4"))})
            await ExpectError("serial_send_and_capture",args);
        foreach(var args in new[]{Target(("captureId",captureId),("offset",-1)),Target(("captureId",captureId),("offset",int.MaxValue)),
            Target(("captureId",captureId),("limit",3)),Target(("captureId",captureId),("limit",65537)),Target(("captureId",captureId),("format","invalid"))})
            await ExpectError("serial_read_capture",args);
        await ExpectError("serial_connect",Target(("port","COM4"),("baudRate",115200)));
        await ExpectError("serial_configure",Target(("port","COM4"),("byteTimeoutMs",40)));
        await ExpectError("serial_sequence_save",Target(("sequence",new{FormatVersion=3,Name="GUI file is not MCP definition",RepeatCount=1,Steps=Array.Empty<object>()})));
        await ExpectError("serial_sequence_save",Target(("sequence",new{name=tag+" wildcard",repeatCount=1,steps=new[]{new{command="PING",txMode="text",expect=new{kind="hex",value="AA ??"}}}})));
        await ExpectError("serial_sequence_save",Target(("sequence",new{name=tag+" limit",repeatCount=129,steps=new[]{new{command="PING"}}})));
        await ExpectError("serial_sequence_save",Target(("sequence",new{name=tag+" waits",repeatCount=128,steps=new[]{new{command="PING",captureMs=60000}}})));
        await ExpectError("tool_that_does_not_exist",new());
        Need(await DeviceCommands()==before+1,"Rejected requests transmitted a device command");
        var state=await State();Need(state.GetProperty("port").GetString()=="COM3"&&state.GetProperty("baudRate").GetInt32()==115200&&state.GetProperty("mode").GetString()==originalMode,"Invalid request changed target settings");
        Pass("limits/types/typos/wrong-port/GUI-schema/wildcard rejection, unknown tool recovery, zero invalid TX");
    }

    private async Task StateChangesAsync()
    {
        var before=initialState.GetProperty("sessionGeneration").GetInt64();
        await Call("serial_disconnect",Target());disconnected=true;
        Need(!(await State()).GetProperty("connected").GetBoolean(),"Disconnect did not close COM3");Pass("actual disconnect state");
        await ExpectError("serial_send_and_capture",Target(("port","COM3"),("command","PING MUST-NOT-SEND")),"disconnected");
        await ExpectError("serial_configure",Target(("port","COM3"),("byteTimeoutMs",40)),"disconnected");
        await Call("serial_connect",Target(("port","COM3"),("baudRate",115200)));disconnected=false;
        var after=await State();Need(after.GetProperty("connected").GetBoolean()&&after.GetProperty("sessionGeneration").GetInt64()>before,"New receive session not established");
        Text(await Request("PING after-reconnect"),"PONG after-reconnect\r\n");Pass("disconnected guards, real reconnect, new session and device response");
        string alternative=originalMode=="terminal"?"hex":"terminal";
        foreach(string mode in new[]{alternative,originalMode})
        {
            await Call("serial_set_mode",Target(("mode",mode)));Need((await State()).GetProperty("mode").GetString()==mode,"Mode readback mismatch");
            Text(await Request("PING mode-"+mode),"PONG mode-"+mode+"\r\n");
            var payload=new byte[]{0,10,13,127,128,165,90,255};
            SerialLabWire.AssertBytes(await Request(SerialLabWire.Hex(SerialLabWire.Encode(2,5678,payload)),"hex",ending:"none"),SerialLabWire.Encode(0x82,5678,payload));
            Pass("actual GUI "+mode+" with explicit text and binary TX/RX");
        }
        Need((await State()).GetProperty("mode").GetString()==originalMode,"Initial mode not restored");
        Pass("initial GUI mode restored after exactly two deliberate transitions");
    }

    private async Task RemainingAsync()
    {
        var before=await ListAll("serial_sequence_list","sequences");Save("sequence-snapshot-before.json",before);
        string normal=tag+" remaining-normal",cancel=tag+" remaining-cancel";
        await Call("serial_sequence_save",Target(("sequence",Sequence(normal,"REMAINING"))));ownedSequences.Add(normal);
        await Call("serial_sequence_save",Target(("sequence",Sequence(cancel,"REMAINING-CANCEL",wait:true))));ownedSequences.Add(cancel);
        var start=Data(await Call("serial_sequence_start",Target(("name",normal),("port","COM3"))));activeRun=start.GetProperty("runId").GetString()!;
        var completed=await WaitRun(activeRun);Need(completed.GetProperty("state").GetString()=="completed","Remaining normal sequence failed");activeRun="";
        await ReadRun(completed);Pass("explicit HEX format kept on every saved sequence RX page");
        await SequenceCancellationAndReconnect(cancel);
        var capture=await RequestData("PING capture-reference");await Read(capture);
        await ErrorMatrix(normal,capture.GetProperty("capture").GetProperty("captureId").GetString()!);
        foreach(string name in ownedSequences.ToArray())
        {
            var deleted=Data(await Call("serial_sequence_delete",Target(("name",name))));Need(deleted.GetProperty("deleted").GetBoolean()&&deleted.GetProperty("persisted").GetBoolean(),"Delete not persisted");ownedSequences.Remove(name);
            await ExpectError("serial_sequence_get",Target(("name",name)),"sequence_not_found");
            await ExpectError("serial_sequence_delete",Target(("name",name)),"sequence_not_found");
        }
        var after=await ListAll("serial_sequence_list","sequences");Save("sequence-snapshot-after.json",after);
        Need(before.Select(x=>x.GetRawText()).SequenceEqual(after.Select(x=>x.GetRawText())),"Pre-existing sequence metadata changed");
        Pass("owned sequence deletion and preservation of existing sequence metadata");
        Text(await Request("PING remaining-ready"),"PONG remaining-ready\r\n");Pass("session remains usable after error/cancellation tests");
    }

    public ValueTask DisposeAsync()=>client.DisposeAsync();
}
