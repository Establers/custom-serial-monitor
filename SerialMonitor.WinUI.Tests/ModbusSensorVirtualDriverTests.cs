using Microsoft.Win32;
using SerialMonitor.Core.Modbus;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using Xunit.Abstractions;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusSensorVirtualDriverTests(ITestOutputHelper output)
{
    [Com0comPair0Fact]
    public async Task Ee071ExampleRespondsAcrossWindowsSerialDriverAndStopsWithoutMoreTx()
    {
        // Only the registry-verified emulator pair is allowed. Never open a
        // physical port or redirect this test via arbitrary environment input.
        VerifyEmulator("CNCA0"); VerifyEmulator("CNCB0");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        await using var serial = new SerialService();
        await using var master = new SerialService();
        serial.SetTransmitMode(SerialTransmitMode.Sensor);
        master.SetTransmitMode(SerialTransmitMode.DirectTest);
        await serial.ConnectAsync(Settings("CNCA0"), new(), token);
        await master.ConnectAsync(Settings("CNCB0"), new(), token);
        var definition = ModbusSensorDefinition.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "docs", "modbus_sensor_ee071.json"), token));
        var sensor = new ModbusSensorService(serial, (_, _) => {});
        var live = new ModbusSensorSession();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var run = sensor.RunAsync(definition, () => {}, stop.Token, live);
        // The app normally drains these chunks through LogPipeline.
        var drain = Task.Run(async () => { await foreach (var _ in serial.ReceivedBytes.ReadAllAsync(token)) { } }, token);
        try
        {
            await AssertQuiet(master, token);
            Assert.Equal(0, serial.WrittenByteCount);
            var vectors = new[]
            {
                ("F70300190002015A", "F70304000041CC5C39"),
                ("F70400190002B49A", "F70404000041CC5D8E"),
                ("F70300270003A156", "F7030609F61E6E1388ACFE")
            };
            for (var i = 0; i < 60; i++)
            {
                var (request, expected) = vectors[i % vectors.Length];
                await master.SendBytesAsync(Convert.FromHexString(request), "EE071 test", token);
                Assert.Equal(expected, Convert.ToHexString(await ReadExact(master, expected.Length/2, token)));
            }
            var beforeEdit = serial.WrittenByteCount;
            await live.SetValueAsync(0,"35.5",token);
            Assert.Equal(beforeEdit,serial.WrittenByteCount);
            await master.SendBytesAsync(Convert.FromHexString(vectors[0].Item1),"Live temperature",token);
            var edited = await ReadExact(master,9,token);
            Assert.Equal("F703040000420E",Convert.ToHexString(edited[..^2]));
            Assert.Equal(SerialMonitor.Core.Protocols.ModbusRtuCrc.Compute(edited.AsSpan(0,7)),System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(edited.AsSpan(7)));
            Assert.Equal(ModbusSensorChangeSource.User,live.Snapshot!.Points[0].Source);
            await live.ResetAsync(0,token);
            await master.SendBytesAsync(Convert.FromHexString(vectors[0].Item1),"Reset temperature",token);
            Assert.Equal(vectors[0].Item2,Convert.ToHexString(await ReadExact(master,9,token)));
            // Bad CRC and another unit must not produce a reply.
            await master.SendBytesAsync(Convert.FromHexString("F70300190002015B"), "Bad CRC", token);
            await AssertQuiet(master, token);
            await master.SendBytesAsync(SerialMonitor.Core.Protocols.ModbusRtuRequestBuilder.Read(246,3,25,2), "Other unit", token);
            await AssertQuiet(master, token);
            stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Null(serial.ExclusiveSessionOwner);
            var written = serial.WrittenByteCount;
            await master.SendBytesAsync(Convert.FromHexString(vectors[0].Item1), "After stop", token);
            await AssertQuiet(master, token);
            Assert.Equal(written, serial.WrittenByteCount);
            Assert.Equal(0, serial.SerialFrameErrorCount + serial.SerialParityErrorCount + serial.SerialOverrunErrorCount + serial.SerialRxOverErrorCount);
            output.WriteLine($"60 EE071 exchanges plus live read-only temperature edit/reset over CNCA0/CNCB0 at 9600 8E1; {written} response bytes; no unsolicited TX.");
        }
        finally
        {
            stop.Cancel(); deadline.Cancel();
            await Record.ExceptionAsync(() => run);
            await Record.ExceptionAsync(() => drain);
        }
    }

    [Com0comPair0Fact]
    public async Task UnsupportedRequestsAndAdvancedWritesRoundTripAcrossTheWindowsDriver()
    {
        VerifyEmulator("CNCA0"); VerifyEmulator("CNCB0");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        await using var serial = new SerialService();
        await using var master = new SerialService();
        serial.SetTransmitMode(SerialTransmitMode.Sensor); master.SetTransmitMode(SerialTransmitMode.DirectTest);
        await serial.ConnectAsync(Settings("CNCA0"), new(), token);
        await master.ConnectAsync(Settings("CNCB0"), new(), token);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var service = new ModbusSensorService(serial, (_,_)=>{});
        var run = service.RunAsync(new() { SchemaVersion=1,Name="Controller",UnitId=1,
            Points=[new() {Name="Flags",Area=3,Address=0,InitialValue="18",Writable=true},
                    new() {Name="Setpoint",Area=3,Address=1,InitialValue="18",Writable=true}] },()=>{},stop.Token);
        var drain = Task.Run(async () => { await foreach(var _ in serial.ReceivedBytes.ReadAllAsync(token)) {} },token);
        try
        {
            // Independent fixed packets, including wire CRCs; FC22 example: 0x12 -> 0x17.
            var vectors = new[] {
                ("010741E2","0187018230"),
                ("0116000000F20025962E","0116000000F20025962E"),
                ("010300000001840A","0103020017F84A"),
                ("0118000081DF","0198018A00"),
                ("01170000000200000002041234ABCD3D53","0117041234ABCD0334") };
            foreach (var (request,response) in vectors)
            {
                await master.SendBytesAsync(Convert.FromHexString(request),"Advanced simulator test",token);
                Assert.Equal(response,Convert.ToHexString(await ReadExact(master,response.Length/2,token)));
            }
            Assert.Equal(36,serial.WrittenByteCount);
            output.WriteLine("FC07/24 exceptions, FC22 mask write, FC03 readback, FC23 write-before-read: 5 exact exchanges at 9600 8E1.");
        }
        finally
        {
            stop.Cancel(); deadline.Cancel();
            await Record.ExceptionAsync(()=>run); await Record.ExceptionAsync(()=>drain);
        }
    }

    private static SerialSettings Settings(string port) => new()
    { PortName = port, BaudRate = 9600, DataBits = 8, Parity = SerialParityMode.Even, StopBits = SerialStopBitsMode.One };

    private static void VerifyEmulator(string port)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\com0com\port\" + port);
        Assert.NotNull(key); Assert.Equal("com0com", key.GetValue("Service"));
        using var parameters = key.OpenSubKey("Device Parameters");
        Assert.Equal(port, parameters?.GetValue("PortName"));
    }

    private static async Task<byte[]> ReadExact(SerialService serial, int length, CancellationToken token)
    {
        var bytes = new List<byte>();
        while (bytes.Count < length) bytes.AddRange((await serial.ReceivedBytes.ReadAsync(token)).Bytes);
        Assert.Equal(length, bytes.Count); return bytes.ToArray();
    }

    private static async Task AssertQuiet(SerialService serial, CancellationToken token)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
        window.CancelAfter(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await serial.ReceivedBytes.ReadAsync(window.Token));
        token.ThrowIfCancellationRequested();
    }
}
