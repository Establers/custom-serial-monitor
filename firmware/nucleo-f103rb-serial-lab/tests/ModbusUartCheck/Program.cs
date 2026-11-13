using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using SerialMonitor.Core.Protocols;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

if (args.Length > 0 && args[0].StartsWith("mstp-",StringComparison.Ordinal))
{
    await MstpUartCheck.Run(args);
    return;
}

if (args.Length > 0 && args[0].StartsWith("server-",StringComparison.Ordinal))
{
    await RtuServerCheck.Run(args);
    return;
}

// Owns one real port only when no GUI owns it. No UI automation, no flash/reset,
// no retry after TX, and no files are written from the receive observer.
if (args.Length == 0) throw new ArgumentException("offline BUILD_DIR | probe PORT | live PORT MANIFEST NEW_EVIDENCE_DIR");
using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(3));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
if (args is ["offline", var directory])
{
    foreach (var item in Fixtures.All)
    {
        byte[] actual = File.ReadAllBytes(Path.Combine(directory,"modbus-" + item.Name + ".bin"));
        Require(actual.AsSpan().SequenceEqual(item.Bytes),"Independent C fixture mismatch: " + item.Name);
        await using var analyzer = new ModbusRtuAnalyzer(); analyzer.SetEnabled(true);
        long time = Stopwatch.GetTimestamp();
        if (item.Name == "GAP")
        {
            analyzer.TryEnqueue(actual.AsMemory(0,3),ProtocolDirection.Rx,1,DateTimeOffset.Now,new(time,true,40,1.75));
            analyzer.TryEnqueue(actual.AsMemory(3),ProtocolDirection.Rx,1,DateTimeOffset.Now,new(time + Stopwatch.Frequency * 80 / 1000,true,40,1.75));
        }
        else analyzer.TryEnqueue(actual,ProtocolDirection.Rx,1,DateTimeOffset.Now,new(time,true,40,1.75));
        await Task.Delay(150,stop.Token); item.Assert(analyzer.Snapshot());
        Console.WriteLine("PASS native C bytes + application analyzer: " + item.Name);
    }
    await using var limited = new ModbusRtuAnalyzer(queueByteLimit:8); limited.SetEnabled(true);
    Require(!limited.TryEnqueue(new byte[9],ProtocolDirection.Rx,1,DateTimeOffset.Now),"Queue rejection missing");
    Require(limited.Snapshot().DroppedChunks == 1 && limited.Snapshot().DroppedBytes == 9,"Queue drop counters");
    Console.WriteLine("PASS deterministic analysis queue rejection (offline; not wire loss)");
    string examples = Path.Combine(Path.GetFullPath(directory),"..","sequences","modbus");
    foreach(string file in Directory.GetFiles(examples,"*.cli.json"))
    {
        using var definition=JsonDocument.Parse(File.ReadAllText(file));
        AutomationSequenceValidation.Parse(definition.RootElement);
    }
    // The extended register-monitor plan has its own validator. Keep this
    // legacy fixture contract pinned to its original two definitions.
    var legacyGui = new[] { "01-modbus-cases.gui.json", "02-rtu-server.gui.json" };
    var sequences=await new CommandSequenceFileService().LoadAsync(legacyGui.Select(name => Path.Combine(examples,name)).ToArray(),stop.Token);
    Require(sequences.Count==2 && sequences.Sum(s=>s.Steps.Count)==36,"Modbus GUI sequence definitions");
    Console.WriteLine("PASS legacy fixture and true RTU HEX CLI/GUI RX Check sequence parsers");
    return;
}
bool probe = args is ["probe", _];
if (!probe && args is not ["live", _, _, _]) throw new ArgumentException("Invalid arguments");
string port = args[1];
string? evidence = probe ? null : Path.GetFullPath(args[3]);
if (evidence is not null)
{
    Require(!Directory.Exists(evidence),"Use a new evidence directory"); Directory.CreateDirectory(evidence);
}
await using var serial = new SerialService();
var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
serial.Error += (_, message) => errors.Enqueue(message);
var gate = new object();
List<Observed>? active = null;
ModbusRtuAnalyzer? currentAnalyzer = null;
int retainedBytes = 0;
bool overflow = false;
var anchor = DateTimeOffset.Now; long anchorTicks = Stopwatch.GetTimestamp();
serial.CaptureBytesReceived += (generation,chunk) =>
{
    var observedAt = anchor + Stopwatch.GetElapsedTime(anchorTicks,chunk.ReceivedTimestamp);
    lock (gate)
    {
        if (active is null) return;
        if (active.Count >= 4096 || chunk.Bytes.Length > 1_048_576 - retainedBytes) { overflow = true; return; }
        active.Add(new(chunk.Bytes,chunk.ReceivedTimestamp,chunk.EndsAtNativeIdleBoundary)); retainedBytes += chunk.Bytes.Length;
        currentAnalyzer?.TryEnqueue(chunk.Bytes,ProtocolDirection.Rx,generation,observedAt,
            new(chunk.ReceivedTimestamp,chunk.EndsAtNativeIdleBoundary,serial.AppliedReceiveIdleTimeoutMs,
                ProtocolInputTiming.FrameGapMilliseconds(serial.AppliedBaudRate,10)));
    }
};
await serial.ConnectAsync(new SerialSettings {PortName=port,BaudRate=115200},
    new SerialReceiveOptions {UseNativeIdleTimeout=true,IdleTimeoutMs=40},stop.Token);
var drain = Task.Run(async () => { await foreach(var _ in serial.ReceivedBytes.ReadAllAsync(stop.Token)) { } },stop.Token);
var results = new List<object>();
async Task<List<Observed>> Request(string command,int milliseconds,ModbusRtuAnalyzer? analyzer = null)
{
    lock(gate) { active = new(); currentAnalyzer = analyzer; retainedBytes=0; overflow=false; }
    try
    {
        await serial.SendBytesAsync(Encoding.ASCII.GetBytes(command + "\r\n"),command,stop.Token);
        await Task.Delay(milliseconds,stop.Token);
    }
    finally { lock(gate) currentAnalyzer=null; }
    List<Observed> received;
    lock(gate) { received=active!; active=null; }
    Require(!overflow,"Evidence buffer overflow; partial evidence is not a pass");
    Require(errors.IsEmpty,"Serial transport error: " + string.Join("; ",errors));
    return received;
}
try
{
    string id = Encoding.ASCII.GetString(Join(await Request("ID?",650)));
    Require(id.Contains("ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1\r\n",StringComparison.Ordinal),"Wrong firmware/target: " + id);
    if (probe) { Console.Write(id); Console.Write(Encoding.ASCII.GetString(Join(await Request("BUILD?",650)))); return; }
    Require(Encoding.ASCII.GetString(Join(await Request("STOP",650))).Contains("OK STOP\r\n",StringComparison.Ordinal),"STOP not acknowledged");
    using var manifest=JsonDocument.Parse(File.ReadAllText(args[2]));
    string build=manifest.RootElement.GetProperty("buildId").GetString()!;
    string version=manifest.RootElement.GetProperty("firmwareVersion").GetString()!;
    var buildRx=Join(await Request("BUILD?",350));
    Require(Encoding.ASCII.GetString(buildRx)==$"BUILD version={version} protocol=1 id={build}\r\n","Manifest does not match board BUILD?");
    File.WriteAllBytes(Path.Combine(evidence!,"BUILD.bin"),buildRx);
    Require(Encoding.ASCII.GetString(Join(await Request("FAULT CLEAR",350)))=="OK FAULT CLEAR\r\n","FAULT CLEAR not acknowledged");
    foreach(var item in Fixtures.All)
    {
        await using var analyzer=new ModbusRtuAnalyzer(); analyzer.SetEnabled(true);
        string command="MODBUS " + item.Name;
        var received=await Request(command,650,analyzer);
        await Task.Delay(100,stop.Token);
        byte[] raw=Join(received);
        File.WriteAllBytes(Path.Combine(evidence!,item.Name+".bin"),raw);
        File.WriteAllText(Path.Combine(evidence!,item.Name+"-chunks.json"),JsonSerializer.Serialize(received.Select(c=>new {bytes=c.Bytes.Length,c.Timestamp,c.NativeBoundary}),Options()));
        Require(raw.AsSpan().SequenceEqual(item.Bytes),"UART bytes differ: " + item.Name);
        var snapshot=analyzer.Snapshot(); item.Assert(snapshot);
        if(item.Name=="GAP") Require(received.Count>=2 && Stopwatch.GetElapsedTime(received[0].Timestamp,received[^1].Timestamp).TotalMilliseconds>=50,"Split gap was not observed");
        results.Add(new {name=item.Name,command,rxBytes=raw.Length,chunks=received.Count,snapshot.Statistics,snapshot.DroppedChunks,snapshot.DroppedBytes});
        Console.WriteLine($"PASS UART {item.Name}: {raw.Length} bytes, {snapshot.TotalFrames} results, CRC errors {snapshot.Statistics.CrcErrors}");
    }
    await using(var analyzer=new ModbusRtuAnalyzer())
    {
        analyzer.SetEnabled(true);
        var raw=Join(await Request("MODBUS NORMAL 10005 0",11000,analyzer));
        await Task.Delay(100,stop.Token);
        File.WriteAllBytes(Path.Combine(evidence!,"RETENTION.bin"),raw);
        byte[] normal=Fixtures.All[0].Bytes;
        Require(raw.Length==normal.Length*10005,"Retention wire byte count");
        for(int i=0;i<10005;i++) Require(raw.AsSpan(i*normal.Length,normal.Length).SequenceEqual(normal),"Retention wire byte order/payload");
        var s=analyzer.Snapshot();
        Require(s.TotalFrames==10005 && s.RetainedCount==10000 && s.EvictedFrames==5 && s.Statistics.CrcValid==10005 && s.Statistics.CrcErrors==0 && s.Statistics.Incomplete==0 && s.DroppedChunks==0 && s.DroppedBytes==0,"Retention/cumulative/drop counters");
        analyzer.Clear(); Require(analyzer.Snapshot().TotalFrames==10005,"Clear reset cumulative counters");
        results.Add(new {name="RETENTION",bytes=raw.Length,s.TotalFrames,s.RetainedCount,s.EvictedFrames,s.Statistics,s.DroppedChunks,s.DroppedBytes});
        Console.WriteLine("PASS UART 10005 frames: retain 10000, expire 5, no CRC errors or analysis drops");
    }
    string status=Encoding.ASCII.GetString(Join(await Request("STATUS?",350)));
    Require(status.Contains("rx_drops=0 uart_errors=0 tx_rejected=0",StringComparison.Ordinal),"MCU transport counters: " + status);
    File.WriteAllText(Path.Combine(evidence!,"board-status.txt"),status);
    File.WriteAllText(Path.Combine(evidence!,"report.json"),JsonSerializer.Serialize(new {passed=true,port,baud=115200,serialFormat="8N1",byteTimeoutMs=40,build,version,
        firmwareSha256=manifest.RootElement.GetProperty("sha256").GetString(),hardwareVerified=true,
        validatorSha256=AssemblyHash(typeof(Fixture)),serialServiceSha256=AssemblyHash(typeof(SerialService)),analyzerSha256=AssemblyHash(typeof(ModbusRtuAnalyzer)),
        scope="Real STM32 UART through application SerialService and analysis core; RX only; GUI not exercised",results},Options()));
}
catch(Exception ex)
{
    if(evidence is not null) File.WriteAllText(Path.Combine(evidence,"failure.json"),JsonSerializer.Serialize(new {passed=false,error=ex.ToString(),completed=results},Options()));
    throw;
}
finally
{
    lock(gate) { active=null; currentAnalyzer=null; }
    await serial.DisconnectAsync(CancellationToken.None);
    await drain.WaitAsync(TimeSpan.FromSeconds(5));
}
static byte[] Join(IEnumerable<Observed> chunks)=>chunks.SelectMany(c=>c.Bytes).ToArray();
static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
static JsonSerializerOptions Options()=>new() {WriteIndented=true};
static string AssemblyHash(Type type)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(type.Assembly.Location)));
sealed record Observed(byte[] Bytes,long Timestamp,bool NativeBoundary);
sealed record Fixture(string Name,byte[] Bytes,long Total,long Valid,long CrcErrors=0,long Incomplete=0,long Undecoded=0,long Exceptions=0)
{
    public void Assert(ProtocolAnalysisSnapshot s)
    {
        var a=s.Statistics;
        if(s.LastError is not null || s.TotalFrames!=Total || a.Rx!=Total || a.Tx!=0 || a.CrcValid!=Valid || a.CrcErrors!=CrcErrors || a.Incomplete!=Incomplete || a.Undecoded!=Undecoded || a.Exceptions!=Exceptions || s.DroppedChunks!=0 || s.DroppedBytes!=0)
            throw new InvalidOperationException(Name+" analysis mismatch: "+JsonSerializer.Serialize(s));
        if(Name=="PREFIX" && s.Frames![0].Kind!=ModbusFrameKind.Response) throw new InvalidOperationException("Prefix collision invented a request");
    }
}
static class Fixtures
{
    // Independent PC oracle: separate constant bodies and bitwise CRC, not MCU output or production CRC.
    static byte[] Adu(params byte[] body)
    {
        int crc=0xffff; foreach(byte b in body) { crc^=b; for(int j=0;j<8;j++) crc=(crc&1)!=0?(crc>>1)^0xa001:crc>>1; }
        return body.Concat(new[]{(byte)crc,(byte)(crc>>8)}).ToArray();
    }
    static byte[] Bad(byte[] input) { var bytes=(byte[])input.Clone(); bytes[^1]^=1; return bytes; }
    static readonly byte[] Normal=Adu(1,3,4,0,250,0,100);
    public static readonly Fixture[] All = {
        new("NORMAL",Normal,1,1),new("CRC",Bad(Normal),1,0,CrcErrors:1),new("EXCEPTION",Adu(1,0x83,2),1,1,Exceptions:1),
        new("PREFIX",Convert.FromHexString("010304000001853A00"),1,1),
        new("NESTED",Convert.FromHexString("01080000010300000002C40BF3AA"),1,1,Undecoded:1),
        new("CORRUPT",Convert.FromHexString("01030A010300000002C40B1122D8F9"),1,0,CrcErrors:1),
        new("PAIR",Adu(1,3,0,0,0,2).Concat(Normal).ToArray(),2,2),
        new("TRUNCATED",Normal[..3],1,0,Incomplete:1),new("GAP",Normal,2,0,Incomplete:2),
        new("MAX",Adu(new byte[]{1,3,250}.Concat(Enumerable.Range(0,250).Select(i=>(byte)i)).ToArray()),1,1)
    };
}
