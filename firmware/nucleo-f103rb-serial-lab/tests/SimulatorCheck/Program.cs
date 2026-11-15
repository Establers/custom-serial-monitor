using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using SerialMonitor.Core.Modbus;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

if (args is not ["identity", "COM3", _] && args is not ["live", "COM3", _, _])
    throw new ArgumentException("identity COM3 NEW_EVIDENCE_DIRECTORY | live COM3 NEW_EVIDENCE_DIRECTORY MANIFEST");
var directory=Path.GetFullPath(args[2]);
if (Directory.Exists(directory)) throw new IOException("Use a new evidence directory.");
Directory.CreateDirectory(directory);
var wire=new List<Wire>(); var results=new List<object>();
var phase="connect"; var overflow=0; string? failure=null;
var rx=Channel.CreateBounded<byte[]>(256);
using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60));
Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;deadline.Cancel();};
await using var serial=new SerialService();
void Record(string direction,byte[] bytes,long timestamp)
{
    lock(wire) { if(wire.Count>=512) { Interlocked.Exchange(ref overflow,1);return; } wire.Add(new(phase,direction,timestamp,Convert.ToHexString(bytes))); }
}
void SetPhase(string value) {lock(wire) phase=value;}
serial.CaptureBytesReceived+=(_,chunk)=> {
    Record("rx",chunk.Bytes,chunk.ReceivedTimestamp);
    if(!rx.Writer.TryWrite(chunk.Bytes.ToArray())) Interlocked.Exchange(ref overflow,1);
};
Task? drain=null;
try
{
    serial.SetTransmitMode(SerialTransmitMode.DirectTest);
    await serial.ConnectAsync(new() {PortName="COM3",BaudRate=115200,DataBits=8,Parity=SerialParityMode.None,
        StopBits=SerialStopBitsMode.One,Handshake=SerialHandshakeMode.None},new(){UseNativeIdleTimeout=true,IdleTimeoutMs=40},deadline.Token);
    drain=Task.Run(async()=>{await foreach(var _ in serial.ReceivedBytes.ReadAllAsync(deadline.Token)){}},deadline.Token);
    await Task.Delay(300,deadline.Token);
    await Text("LAB","OK LAB\r\n");
    var build=await Text("BUILD?","BUILD version=");
    await Text("ID?","ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1\r\n");
    if(args[0]=="live")
    {
        using var manifest=JsonDocument.Parse(await File.ReadAllTextAsync(args[3],deadline.Token));
        var version=manifest.RootElement.GetProperty("firmwareVersion").GetString();
        var buildId=manifest.RootElement.GetProperty("buildId").GetString();
        Require(build.Contains($"version={version} protocol=1 id={buildId}\r\n"),"Device BUILD differs from the supplied manifest.");
        var before=await Text("STATUS?","STATUS ");
        foreach(var name in new[]{"RETRY6","RETRY22","ADVANCED"}) await Check(name);
        var after=await Text("STATUS?","STATUS ");
        foreach(var field in new[]{"parse_errors","rx_drops","uart_errors","tx_rejected"})
            Require(Number(before,field)==Number(after,field),$"Firmware {field} increased.");
        await Text("RTU","OK RTU\r\n");
    }
    Require(Volatile.Read(ref overflow)==0,"Evidence overflow.");
    Require(serial.SerialFrameErrorCount+serial.SerialParityErrorCount+serial.SerialOverrunErrorCount+serial.SerialRxOverErrorCount==0,"Serial driver errors.");
}
catch(Exception ex) { failure=ex.ToString(); Console.Error.WriteLine(ex); }
finally
{
    deadline.Cancel();
    if(drain is not null) {try {await drain;} catch(OperationCanceledException){} }
    var report=new {passed=failure is null,hardwareVerified=failure is null && args[0]=="live",failure,
        port="COM3",baudRate=serial.AppliedBaudRate,serialFormat="8N1",byteTimeoutMs=serial.AppliedReceiveIdleTimeoutMs,
        nativeIdle=serial.UsesNativeReceiveIdleTimeout,overflow=Volatile.Read(ref overflow)!=0,results,
        serialErrors=serial.SerialFrameErrorCount+serial.SerialParityErrorCount+serial.SerialOverrunErrorCount+serial.SerialRxOverErrorCount,
        productionAssembly=typeof(ModbusSensorService).Assembly.Location,
        productionSha256=Hash(typeof(ModbusSensorService).Assembly.Location),coreSha256=Hash(typeof(ModbusSensorEngine).Assembly.Location),
        scope="Production SerialService/ModbusSensorService over ST-LINK UART; board validates exact PC responses. No Computer Use or electrical RS-485 timing certification.",wire};
    await File.WriteAllTextAsync(Path.Combine(directory,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine($"Evidence: {directory}; passed={failure is null}");
}
return failure is null ? 0 : 1;

async Task<string> Text(string command,string expected)
{
    SetPhase("command:"+command);
    while(rx.Reader.TryRead(out _)){} // Already retained in the full wire evidence.
    var bytes=Encoding.ASCII.GetBytes(command+"\r\n"); Record("command-tx",bytes,Stopwatch.GetTimestamp());
    await serial.SendBytesAsync(bytes,"Simulator hardware verification",deadline.Token);
    using var window=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); window.CancelAfter(300);
    var response=new List<byte>();
    try {await foreach(var chunk in rx.Reader.ReadAllAsync(window.Token)) response.AddRange(chunk);}
    catch(OperationCanceledException) when(!deadline.IsCancellationRequested){}
    var text=Encoding.UTF8.GetString(response.ToArray());
    Console.WriteLine(command+" => "+text.Trim());
    Require(text.Contains(expected,StringComparison.Ordinal),$"Unexpected response for {command}; no automatic resend.");
    return text;
}
async Task Check(string name)
{
    const string mask="0116000000F20025962E",write6="0106000000648821",read1="010300000001840A",
        write23="01170000000200000002041234ABCD3D53",read2="010300000002C40B",fifo="0118000081DF";
    string[] requests=name switch {"RETRY6"=>[write6,write6],"RETRY22"=>[mask,mask],_=>[mask,read1,write23,read2,fifo]};
    string[] responses=name=="ADVANCED" ? [mask,"0103020017F84A","0117041234ABCD0334","0103041234ABCD0020","0198018A00"] : requests;
    await Text("MASTER "+name,"OK MASTER "+name+"\r\n");
    SetPhase(name); serial.SetTransmitMode(SerialTransmitMode.Sensor);
    using var stop=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
    using var responseDeadline=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); responseDeadline.CancelAfter(10000);
    var replies=Channel.CreateBounded<byte[]>(8);
    var simulator=new ModbusSensorService(serial,(bytes,_)=>{Record("simulator-tx",bytes,Stopwatch.GetTimestamp()); if(!replies.Writer.TryWrite(bytes)) Interlocked.Exchange(ref overflow,1);});
    var run=simulator.RunAsync(new(){SchemaVersion=1,Name="Hardware test",UnitId=1,ResponseDelayMs=name=="ADVANCED"?0:600,
        Points=[new(){Name="Flags",Area=3,Address=0,InitialValue="18",Writable=true},new(){Name="Value",Area=3,Address=1,InitialValue="18",Writable=true}]},()=>{},stop.Token);
    try
    {
        foreach(var expected in responses)
        {
            var reply=await replies.Reader.ReadAsync(responseDeadline.Token);
            Require(Convert.ToHexString(reply)==expected,"PC response differs from independent vector.");
        }
        await Task.Delay(150,deadline.Token);
        Require(!run.IsCompleted,"Simulator stopped unexpectedly.");
    }
    finally
    {
        stop.Cancel(); try {await run;} catch(OperationCanceledException) when(stop.IsCancellationRequested){}
        serial.SetTransmitMode(SerialTransmitMode.DirectTest);
    }
    string received;
    lock(wire) received=string.Concat(wire.Where(w=>w.Phase==name && w.Direction=="rx").Select(w=>w.Hex));
    Require(received==string.Concat(requests),"Actual device request stream differs from independent vectors.");
    var status=await Text("MASTER?",$"MASTER case={name} result=PASS sent={requests.Length} received={responses.Length} ");
    if(name!="ADVANCED")
    {
        Require(Number(status,"retry_ms") is >=211 and <=220,"Board retry interval outside expected window.");
        Require(Number(status,"first_ms")>=600 && Number(status,"second_ms")-Number(status,"first_ms")>=600,"Response delay not preserved on board.");
    }
    results.Add(new{name,passed=true,requests,responses,deviceReport=status.Trim()});
    Console.WriteLine("PASS "+name);
}
static long Number(string text,string name)=>long.Parse(Regex.Match(text,@"\b"+name+@"=(\d+)\b").Groups[1].Value);
static void Require(bool ok,string reason) {if(!ok) throw new IOException(reason);}
static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
internal sealed record Wire(string Phase,string Direction,long Timestamp,string Hex);
