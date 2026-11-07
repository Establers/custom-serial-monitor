using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

// Independent request/response oracle. Live mode owns the port only when no GUI does.
// Captures remain bounded; the serial callback never writes files or touches controls.
static class RtuServerCheck
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static byte[] Adu(params byte[] body)
    {
        int crc=0xffff;
        foreach(byte b in body) { crc^=b; for(int j=0;j<8;j++) crc=(crc&1)!=0?(crc>>1)^0xa001:crc>>1; }
        return [..body,(byte)crc,(byte)(crc>>8)];
    }
    static byte[] Bad(byte[] bytes) { var copy=(byte[])bytes.Clone(); copy[^1]^=1; return copy; }
    static readonly byte[] Normal=Adu(1,3,4,0,250,0,100);
    sealed record Case(string Name,byte[] Tx,byte[] Rx,int Exception=0,bool BadCrc=false);
    static readonly Case[] Cases = [
        new("NORMAL",Adu(1,3,0,0,0,2),Normal),
        new("EXCEPTION",Adu(1,3,2,0,0,2),Adu(1,0x83,2),Exception:1),
        new("CRC",Adu(1,3,1,0,0,2),Bad(Normal),BadCrc:true),
        new("INPUT",Adu(1,4,0,1,0,1),Adu(1,4,2,0,100)),
        new("ZERO",Adu(1,3,0,0,0,0),Adu(1,0x83,3),Exception:1),
        new("OVERSIZE",Adu(1,3,0,0,0,126),Adu(1,0x83,3),Exception:1),
        new("FUNCTION",Adu(1,6,0,0,0,1),Adu(1,0x86,1),Exception:1),
        new("BROADCAST",Adu(0,3,0,0,0,2),[]),
        new("OTHER_UNIT",Adu(2,3,0,0,0,2),[]),
        new("BAD_REQUEST_CRC",Bad(Adu(1,3,0,0,0,2)),[]),
        new("AFTER_BAD_REQUEST",Adu(1,3,0,0,0,2),Normal),
        new("TRUNCATED_REQUEST",Adu(1,3,0,0,0,2)[..3],[]),
        new("AFTER_TRUNCATED",Adu(1,3,0,0,0,2),Normal),
        new("NESTED_BAD_REQUEST",Convert.FromHexString("01030A010300000002C40B1122D8F9"),[]),
        new("OVERFLOW",Enumerable.Repeat((byte)0x55,300).Concat(Adu(1,3,0,0,0,2)).ToArray(),[]),
        new("AFTER_OVERFLOW",Adu(1,3,0,0,0,2),Normal),
        new("AFTER_CORRUPT_RESPONSE",Adu(1,3,0,0,0,2),Normal)
    ];
    static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    static async Task<object> Analyze(Case item,byte[] bytes,CancellationToken stop)
    {
        Require(bytes.AsSpan().SequenceEqual(item.Rx),item.Name+" unexpected RX: "+Convert.ToHexString(bytes));
        await using var analyzer=new ModbusRtuAnalyzer(); analyzer.SetEnabled(true);
        if(bytes.Length>0) Require(analyzer.TryEnqueue(bytes,ProtocolDirection.Rx,1,DateTimeOffset.Now,
            new(Stopwatch.GetTimestamp(),true,40,1.75)),"Analyzer enqueue rejected");
        await Task.Delay(100,stop);
        var s=analyzer.Snapshot(); var a=s.Statistics;
        int count=bytes.Length>0?1:0;
        Require(s.LastError is null && s.TotalFrames==count && a.Rx==count && a.Tx==0 &&
            a.CrcValid==(item.BadCrc?0:count) && a.CrcErrors==(item.BadCrc?1:0) &&
            a.Exceptions==item.Exception && a.Incomplete==0 && a.Undecoded==0 && s.DroppedBytes==0 && s.DroppedChunks==0,
            item.Name+" analyzer counters: "+JsonSerializer.Serialize(s));
        return new {name=item.Name,tx=Convert.ToHexString(item.Tx),rx=Convert.ToHexString(bytes),s.TotalFrames,s.Statistics,s.DroppedBytes,s.DroppedChunks};
    }
    static async Task VerifyThreeExchanges(CancellationToken stop)
    {
        await using var analyzer=new ModbusRtuAnalyzer(); analyzer.SetEnabled(true);
        long ticks=Stopwatch.GetTimestamp();
        foreach(var item in Cases.Take(3))
        {
            Require(analyzer.TryEnqueue(item.Tx,ProtocolDirection.Tx,1,DateTimeOffset.Now,new(ticks,true,40,1.75)),"TX rejected");
            ticks+=Stopwatch.Frequency/50;
            Require(analyzer.TryEnqueue(item.Rx,ProtocolDirection.Rx,1,DateTimeOffset.Now,new(ticks,true,40,1.75)),"RX rejected");
            ticks+=Stopwatch.Frequency;
        }
        await Task.Delay(200,stop);
        var s=analyzer.Snapshot(); var a=s.Statistics;
        Require(s.LastError is null && s.TotalFrames==6 && a.Tx==3 && a.Rx==3 && a.CrcValid==5 && a.CrcErrors==1 &&
            a.Exceptions==1 && a.Incomplete==0 && a.Undecoded==0 && s.DroppedChunks==0 && s.DroppedBytes==0,
            "Three HEX exchanges counters: "+JsonSerializer.Serialize(s));
        Console.WriteLine("PASS 3 HEX exchanges: TX 3 / RX 3 / CRC valid 5 / CRC error 1 / exception 1 / incomplete 0 / drops 0");
    }
    public static async Task Run(string[] args)
    {
        using var stop=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        if(args is ["server-offline",var directory])
        {
            for(int i=0;i<9;i++) await Analyze(Cases[i],File.ReadAllBytes(Path.Combine(directory,$"rtu-{i}.bin")),stop.Token);
            await VerifyThreeExchanges(stop.Token);
            Console.WriteLine("PASS 9 independent native RTU replies + application analyzer counters");
            return;
        }
        if(args is not ["server-live",_,_,_]) throw new ArgumentException("server-offline BUILD_DIR | server-live PORT MANIFEST NEW_EVIDENCE_DIR");
        string port=args[1], evidence=Path.GetFullPath(args[3]);
        Require(!Directory.Exists(evidence),"Use a new evidence directory"); Directory.CreateDirectory(evidence);
        using var manifest=JsonDocument.Parse(File.ReadAllText(args[2]));
        string build=manifest.RootElement.GetProperty("buildId").GetString()!;
        string version=manifest.RootElement.GetProperty("firmwareVersion").GetString()!;
        await using var serial=new SerialService();
        var gate=new object(); List<byte[]>? active=null; int retained=0; bool overflow=false;
        var errors=new System.Collections.Concurrent.ConcurrentQueue<string>();
        serial.Error+=(_,message)=>errors.Enqueue(message);
        serial.CaptureBytesReceived+=(_,chunk)=> {
            lock(gate) {
                if(active is null) return;
                if(active.Count>=4096 || chunk.Bytes.Length>65536-retained) {overflow=true;return;}
                active.Add(chunk.Bytes); retained+=chunk.Bytes.Length;
            }
        };
        await serial.ConnectAsync(new SerialSettings {PortName=port,BaudRate=115200},
            new SerialReceiveOptions {UseNativeIdleTimeout=true,IdleTimeoutMs=40},stop.Token);
        var drain=Task.Run(async()=> {await foreach(var _ in serial.ReceivedBytes.ReadAllAsync(stop.Token)) {}},stop.Token);
        var results=new List<object>();
        async Task<byte[]> Request(string name,byte[] tx)
        {
            lock(gate) {active=[];retained=0;overflow=false;}
            await serial.SendBytesAsync(tx,Convert.ToHexString(tx),stop.Token);
            await Task.Delay(550,stop.Token);
            byte[] bytes;
            lock(gate) {bytes=active!.SelectMany(c=>c).ToArray();active=null;}
            File.WriteAllBytes(Path.Combine(evidence,name+".tx.bin"),tx);
            File.WriteAllBytes(Path.Combine(evidence,name+".rx.bin"),bytes);
            Require(!overflow && errors.IsEmpty,"Incomplete transport evidence: "+string.Join(";",errors));
            return bytes;
        }
        async Task Text(string name,string command,string expected)
        {
            byte[] rx=await Request(name,Encoding.ASCII.GetBytes(command+"\r\n"));
            Require(Encoding.ASCII.GetString(rx)==expected,name+" mismatch: "+Convert.ToHexString(rx));
        }
        try
        {
            // ST-LINK VCP can retain bytes from the pre-flash under-reset probes.
            // Observe/drain them without TX before starting a command's capture window.
            lock(gate) {active=[];retained=0;overflow=false;}
            await Task.Delay(600,stop.Token);
            byte[] buffered;
            lock(gate) {
                buffered=active!.SelectMany(c=>c).ToArray();
                active=null;
            }
            File.WriteAllBytes(Path.Combine(evidence,"preflight-buffered.bin"),buffered);
            Require(!overflow && errors.IsEmpty,"Preflight capture transport error");
            await Text("ID","ID?","ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1\r\n");
            await Text("BUILD","BUILD?",$"BUILD version={version} protocol=1 id={build}\r\n");
            await Text("RTU","RTU","OK RTU\r\n");
            foreach(var item in Cases)
            {
                byte[] bytes=await Request(item.Name,item.Tx);
                results.Add(await Analyze(item,bytes,stop.Token));
                Console.WriteLine($"PASS UART HEX {item.Name}: {Convert.ToHexString(item.Tx)} -> {Convert.ToHexString(bytes)}");
            }
            await VerifyThreeExchanges(stop.Token);
            // Inspect MCU transport counters after tests, then leave the board ready for HEX requests.
            await Text("LAB","LAB","OK LAB\r\n");
            byte[] status=await Request("STATUS",Encoding.ASCII.GetBytes("STATUS?\r\n"));
            Require(Encoding.ASCII.GetString(status).Contains("rx_drops=0 uart_errors=0 tx_rejected=0",StringComparison.Ordinal),"MCU transport errors");
            await Text("RTU_FINAL","RTU","OK RTU\r\n");
            File.WriteAllText(Path.Combine(evidence,"report.json"),JsonSerializer.Serialize(new {
                passed=true,port,baud=115200,serialFormat="8N1",byteTimeoutMs=40,build,version,
                firmwareSha256=manifest.RootElement.GetProperty("sha256").GetString(),hardwareVerified=true,
                validatorSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(RtuServerCheck).Assembly.Location))),
                analyzerSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ModbusRtuAnalyzer).Assembly.Location))),
                scope="Real UART HEX TX/RX through SerialService; RX-only analyzer replay, GUI not exercised",results},Json));
        }
        catch(Exception ex)
        {
            File.WriteAllText(Path.Combine(evidence,"failure.json"),JsonSerializer.Serialize(new {passed=false,error=ex.ToString(),results},Json));
            throw;
        }
        finally
        {
            lock(gate) active=null;
            await serial.DisconnectAsync(CancellationToken.None);
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
