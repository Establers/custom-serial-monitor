using System.Buffers.Binary;
using System.Text.Json;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;

namespace SerialMonitor.Core.Tests;
public sealed class ModbusSensorTests
{
    private static ModbusSensorDefinition Definition() => new() { SchemaVersion = 1, Name = "Sensor", UnitId = 1,
        Points = [new() { Name = "Temperature", Area = 3, Address = 0, ValueType = ModbusValueType.Int16, Scale = .1, InitialValue = "-12.3", Writable = true },
        new() { Name = "Humidity", Area = 3, Address = 1, InitialValue = "50" },
        new() { Name = "Enable", Area = 1, Address = 0, InitialValue = "1", Writable = true }] };
    [Fact] public void ReadEncodesScaledSignedValueAndValidCrc()
    {
        var response = new ModbusSensorEngine(Definition()).Respond(ModbusRtuRequestBuilder.Read(1, 3, 0, 2))!;
        Assert.Equal("010304FF850032", Convert.ToHexString(response[..^2])); CheckCrc(response);
    }
    [Fact] public void WritesReadBackAndBroadcastDoesNotReply()
    {
        var engine = new ModbusSensorEngine(Definition());
        var write = ModbusRtuRequestBuilder.WriteSingle(1, 6, 0, 250);
        Assert.Equal(write, engine.Respond(write));
        Assert.Equal((ushort)250, BinaryPrimitives.ReadUInt16BigEndian(engine.Respond(ModbusRtuRequestBuilder.Read(1,3,0,1))!.AsSpan(3)));
        Assert.Null(engine.Respond(ModbusRtuRequestBuilder.WriteSingle(0,6,0,100)));
        Assert.Equal((ushort)100, BinaryPrimitives.ReadUInt16BigEndian(engine.Respond(ModbusRtuRequestBuilder.Read(1,3,0,1))!.AsSpan(3)));
    }
    [Fact] public void MultiWriteIsAtomicAndReadonlyIsNotModified()
    {
        var engine = new ModbusSensorEngine(Definition());
        var error = engine.Respond(ModbusRtuRequestBuilder.WriteMultiple(1,16,0,new ushort[]{10,20}))!;
        Assert.Equal("019002", Convert.ToHexString(error[..^2])); CheckCrc(error);
        Assert.Equal((ushort)65413, BinaryPrimitives.ReadUInt16BigEndian(engine.Respond(ModbusRtuRequestBuilder.Read(1,3,0,1))!.AsSpan(3)));
    }
    [Fact] public void CoilReadAndWriteUsePackedBits()
    {
        var engine = new ModbusSensorEngine(Definition());
        Assert.Equal("01010101", Convert.ToHexString(engine.Respond(ModbusRtuRequestBuilder.Read(1,1,0,1))![..^2]));
        var write = ModbusRtuRequestBuilder.WriteSingle(1,5,0,0); Assert.Equal(write, engine.Respond(write));
        Assert.Equal("01010100", Convert.ToHexString(engine.Respond(ModbusRtuRequestBuilder.Read(1,1,0,1))![..^2]));
    }
    [Fact] public void CorruptAndOtherUnitRequestsAreSilent()
    {
        var engine = new ModbusSensorEngine(Definition());
        Assert.Null(engine.Respond(ModbusRtuRequestBuilder.Read(2,3,0,1)));
        var bad = ModbusRtuRequestBuilder.Read(1,3,0,1); bad[^1] ^= 1; Assert.Null(engine.Respond(bad));
    }
    [Fact] public void FramingHandlesSplitsCoalescingAndNoiseWithoutUnboundedBuffer()
    {
        var framer = new ModbusRequestFramer(); var request = ModbusRtuRequestBuilder.Read(1,3,0,1);
        Assert.Empty(framer.Feed(Enumerable.Repeat((byte)255, 100000).ToArray()));
        Assert.Empty(framer.Feed(request[..3]));
        var frames = framer.Feed(request[3..].Concat(request).ToArray()).ToArray();
        Assert.Equal(2, frames.Length); Assert.All(frames, frame => Assert.Equal(request, frame));
    }
    [Fact] public void JsonRejectsDuplicatesUnknownFieldsMissingAddressAndOverlaps()
    {
        var json = JsonSerializer.Serialize(Definition(), ModbusSensorDefinition.JsonOptions);
        Assert.Equal(3, ModbusSensorDefinition.Parse(json).Points.Count);
        Assert.ThrowsAny<Exception>(() => ModbusSensorDefinition.Parse(json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1")));
        Assert.ThrowsAny<Exception>(() => ModbusSensorDefinition.Parse(json.Replace("\"address\": 0,", "")));
        Assert.ThrowsAny<Exception>(() => ModbusSensorDefinition.Parse(json.Replace("\"name\": \"Sensor\"", "\"script\": \"run\"")));
        var definition = Definition(); definition.Points[1].Address = 0; Assert.ThrowsAny<Exception>(definition.Validate);
    }
    private static void CheckCrc(byte[] packet) => Assert.Equal(ModbusRtuCrc.Compute(packet.AsSpan(0,packet.Length-2)), BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(packet.Length-2)));

    [Fact] public void IdleBoundaryDiscardsAnIncompleteRequestInsteadOfJoiningTheNextFrame()
    {
        var framer = new ModbusRequestFramer();
        var request = ModbusRtuRequestBuilder.Read(1,3,0,1);
        Assert.Empty(framer.Feed(request[..4], endsAtIdleBoundary: true));
        Assert.Equal(request, Assert.Single(framer.Feed(request, endsAtIdleBoundary: true)));
    }

    [Fact] public void ValidMultipleWriteCanContainAWholeReadPacketAsItsData()
    {
        var nested = ModbusRtuRequestBuilder.Read(1,3,0,1);
        var words = Enumerable.Range(0,4).Select(i => BinaryPrimitives.ReadUInt16BigEndian(nested.AsSpan(i*2))).ToArray();
        var write = ModbusRtuRequestBuilder.WriteMultiple(1,16,0,words);
        for (var split = 1; split < write.Length; split++)
        {
            var framer = new ModbusRequestFramer();
            Assert.Empty(framer.Feed(write[..split]));
            Assert.Equal(write, Assert.Single(framer.Feed(write[split..], endsAtIdleBoundary: true)));
        }
    }

    [Fact] public void InvalidValuesRangesAndUnsupportedFunctionsHaveExplicitResponses()
    {
        var engine = new ModbusSensorEngine(Definition());
        static byte[] Packet(params byte[] payload)
        {
            var result = new byte[payload.Length+2]; payload.CopyTo(result,0);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(payload.Length), ModbusRtuCrc.Compute(payload)); return result;
        }
        Assert.Equal("018303", Convert.ToHexString(engine.Respond(Packet(1,3,0,0,0,0))![..^2]));
        Assert.Equal("018502", Convert.ToHexString(engine.Respond(Packet(1,5,255,255,255,0))![..^2]));
        Assert.Equal("018503", Convert.ToHexString(engine.Respond(Packet(1,5,0,0,0,1))![..^2]));
        Assert.Equal("018801", Convert.ToHexString(engine.Respond(Packet(1,8,0,0,0,0))![..^2]));
        Assert.Null(engine.Respond(Packet(0,3,0,0,0,1)));
        Assert.Null(engine.Respond(Packet(0,5,0,0,0,1)));
        Assert.Equal("01010101", Convert.ToHexString(engine.Respond(ModbusRtuRequestBuilder.Read(1,1,0,1))![..^2]));
    }

    [Theory]
    [InlineData(7)] [InlineData(11)] [InlineData(12)] [InlineData(17)] [InlineData(24)] [InlineData(43)]
    public void ShortUnsupportedRequestsSurviveSplitsAndCoalescing(byte function)
    {
        byte[] payload = function switch { 24 => [1, 24, 0, 0], 43 => [1, 43, 14, 1, 0], _ => [1, function] };
        var request = Packet(payload);
        var read = ModbusRtuRequestBuilder.Read(1, 3, 0, 1);
        for (var split = 1; split < request.Length; split++)
        {
            var framer = new ModbusRequestFramer();
            Assert.Empty(framer.Feed(request[..split]));
            var frames = framer.Feed(request[split..].Concat(read).ToArray(), true).ToArray();
            Assert.Equal(2, frames.Length);
            Assert.Equal(request, frames[0]); Assert.Equal(read, frames[1]);
            var response = new ModbusSensorEngine(Definition()).Respond(frames[0])!;
            Assert.Equal(Packet([1, (byte)(function | 128), 1]), response);
        }
    }

    [Theory]
    [InlineData("010741E2")] [InlineData("0118000081DF")]
    public void ShortRequestRecoveryPreservesCrcAndIdleBoundaries(string requestHex)
    {
        var request = Convert.FromHexString(requestHex);
        var corrupt = request.ToArray(); corrupt[^1] ^= 1;
        var framer = new ModbusRequestFramer();
        Assert.Empty(framer.Feed(corrupt, true));
        Assert.Empty(framer.Feed(request[..2], true));
        Assert.Empty(framer.Feed(request[2..], true));
        byte[] noise = [1, 16, 0, 0, 0, 100, 200];
        Assert.Equal(request, Assert.Single(framer.Feed(noise.Concat(request).ToArray(), true)));
    }

    [Fact] public void MultipleWriteDoesNotExtractShortRequestFromItsPayload()
    {
        var write = ModbusRtuRequestBuilder.WriteMultiple(1, 16, 0, new ushort[] { 0x0107, 0x41E2 });
        for (var split = 1; split < write.Length; split++)
        {
            var framer = new ModbusRequestFramer();
            Assert.Empty(framer.Feed(write[..split]));
            Assert.Equal(write, Assert.Single(framer.Feed(write[split..], true)));
        }
    }

    private static byte[] Packet(byte[] payload)
    {
        var packet = new byte[payload.Length + 2]; payload.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(payload.Length), ModbusRtuCrc.Compute(payload));
        return packet;
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void EveryReadAreaHasItsOwnAddressSpace(byte area)
    {
        var d = new ModbusSensorDefinition { SchemaVersion = 1, Name = "Areas", UnitId = 1,
            Points = [new() { Name = "Point", Area = area, Address = 65535, InitialValue = "1" }] };
        var response = new ModbusSensorEngine(d).Respond(ModbusRtuRequestBuilder.Read(1, area, 65535, 1))!;
        Assert.Equal(area, response[1]); Assert.Equal(1, response[^3]); CheckCrc(response);
    }
    [Theory] [InlineData(15,1)] [InlineData(16,3)]
    public void MultipleWritesUpdateAllCells(byte function, byte area)
    {
        var d = new ModbusSensorDefinition { SchemaVersion = 1, Name = "Writes", UnitId = 1,
            Points = Enumerable.Range(0,9).Select(i => new ModbusSensorPoint { Name = $"P{i}", Area = area, Address = i, InitialValue = "0", Writable = true }).ToList() };
        var engine = new ModbusSensorEngine(d);
        var write = ModbusRtuRequestBuilder.WriteMultiple(1,function,0,Enumerable.Repeat((ushort)1,9).ToArray());
        var response = engine.Respond(write)!; Assert.Equal(write[..6], response[..6]); CheckCrc(response);
        var read = engine.Respond(ModbusRtuRequestBuilder.Read(1,area,0,9))!;
        if (area == 1) Assert.Equal(new byte[]{255,1}, read[3..^2]);
        else Assert.All(Enumerable.Range(0,9), i => Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(read.AsSpan(3+i*2))));
    }
}
