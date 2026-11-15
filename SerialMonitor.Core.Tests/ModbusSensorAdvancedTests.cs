using System.Buffers.Binary;
using SerialMonitor.Core.Modbus;

namespace SerialMonitor.Core.Tests;

public sealed class ModbusSensorAdvancedTests
{
    private static ModbusSensorDefinition Definition(int count = 4) => new()
    {
        SchemaVersion = 1, Name = "Controller", UnitId = 1,
        Points = Enumerable.Range(0, count).Select(i => new ModbusSensorPoint
        { Name = $"P{i}", Area = 3, Address = i, InitialValue = "18", Writable = true }).ToList()
    };
    private static byte[] Packet(params byte[] payload)
    {
        var crc = SerialMonitor.Core.Protocols.ModbusRtuCrc.Compute(payload);
        return [.. payload, (byte)crc, (byte)(crc >> 8)];
    }
    private static ushort Read(ModbusSensorEngine engine, int address) => BinaryPrimitives.ReadUInt16BigEndian(
        engine.Respond(Packet(1,3,(byte)(address>>8),(byte)address,0,1))!.AsSpan(3));
    private static byte[] ReadWrite(int readAddress, int readCount, int writeAddress, params ushort[] values)
    {
        var payload = new byte[11 + values.Length * 2]; payload[0] = 1; payload[1] = 23;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), (ushort)readAddress);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4), (ushort)readCount);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6), (ushort)writeAddress);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(8), (ushort)values.Length);
        payload[10] = (byte)(values.Length * 2);
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(11 + i * 2), values[i]);
        return Packet(payload);
    }

    [Fact] public void MaskWriteMatchesSpecificationExampleAndEchoesWholeRequest()
    {
        var engine = new ModbusSensorEngine(Definition());
        var request = Packet(1,22,0,0,0,0xF2,0,0x25);
        Assert.Equal(request, engine.Respond(request));
        Assert.Equal(0x17, Read(engine, 0));
        Assert.Equal(18, Read(engine, 1));
    }
    [Fact] public void MaskWriteBroadcastUpdatesWithoutReplyAndReadonlyRejectsWrite()
    {
        var definition = Definition(); definition.Points[1].Writable = false;
        var engine = new ModbusSensorEngine(definition);
        Assert.Null(engine.Respond(Packet(0,22,0,0,0,0,0,99)));
        Assert.Equal(99, Read(engine,0));
        Assert.Equal(Packet(1,0x96,2), engine.Respond(Packet(1,22,0,1,0,0,0,99)));
        Assert.Equal(18, Read(engine,1));
        Assert.Equal(Packet(1,0x96,3), engine.Respond(Packet(1,22,0,0,0,0)));
    }
    [Fact] public void CombinedReadSeesWrittenValuesWhenRangesOverlap()
    {
        var engine = new ModbusSensorEngine(Definition());
        Assert.Equal(Packet(1,23,6,0,18,0xAB,0xCD,0x12,0x34), engine.Respond(ReadWrite(0,3,1,0xABCD,0x1234)));
        Assert.Equal(0xABCD, Read(engine,1)); Assert.Equal(0x1234, Read(engine,2));
    }
    [Fact] public void CombinedWriteIsAtomicWhenReadRangeOrWritePermissionsAreInvalid()
    {
        var definition = Definition(); definition.Points[2].Writable = false;
        var engine = new ModbusSensorEngine(definition);
        Assert.Equal(Packet(1,0x97,2), engine.Respond(ReadWrite(4,1,0,9)));
        Assert.Equal(Packet(1,0x97,2), engine.Respond(ReadWrite(0,1,1,9,10)));
        Assert.Equal(Packet(1,0x97,2), engine.Respond(ReadWrite(65535,2,0,9)));
        Assert.Equal(18, Read(engine,0)); Assert.Equal(18, Read(engine,1));
    }
    [Theory] [InlineData(0)] [InlineData(126)]
    public void CombinedReadRejectsOutOfRangeQuantities(int count)
    {
        var engine = new ModbusSensorEngine(Definition(126));
        Assert.Equal(Packet(1,0x97,3), engine.Respond(ReadWrite(0,count,0,99)));
        Assert.Equal(18, Read(engine,0));
    }
    [Fact] public void CombinedWriteLimitsAndByteCountAreValidatedBeforeMutation()
    {
        var engine = new ModbusSensorEngine(Definition(125));
        Assert.Equal(Packet(1,0x97,3), engine.Respond(ReadWrite(0,1,0)));
        Assert.Null(engine.Respond(ReadWrite(0,1,0,new ushort[122]))); // Exceeds the RTU ADU limit.
        var tooMany = ReadWrite(0,1,0,99)[..^2]; tooMany[9] = 122;
        Assert.Equal(Packet(1,0x97,3), engine.Respond(Packet(tooMany)));
        var malformed = ReadWrite(0,1,0,99)[..^2]; malformed[10] = 1;
        Assert.Equal(Packet(1,0x97,3), engine.Respond(Packet(malformed)));
        Assert.Equal(18, Read(engine,0));
        var request = ReadWrite(0,125,0,Enumerable.Repeat((ushort)7,121).ToArray());
        Assert.Equal(255, request.Length);
        var response = engine.Respond(request)!;
        Assert.Equal(255, response.Length); Assert.Equal(250,response[2]);
        Assert.Equal(7, Read(engine,120)); Assert.Equal(18, Read(engine,121));
    }
    [Fact] public void CombinedBroadcastOtherUnitAndBadCrcAreSilentAndDoNotWrite()
    {
        var engine = new ModbusSensorEngine(Definition());
        var payload = ReadWrite(0,1,0,99)[..^2];
        payload[0] = 0; Assert.Null(engine.Respond(Packet(payload)));
        payload[0] = 2; Assert.Null(engine.Respond(Packet(payload)));
        payload[0] = 1; var bad = Packet(payload); bad[^1] ^= 1; Assert.Null(engine.Respond(bad));
        Assert.Equal(18, Read(engine,0));
    }
    [Fact] public void AdvancedRequestsSurviveEverySplitAndCoalescedFrames()
    {
        var requests = new[] { Packet(1,22,0,0,0,0xF2,0,0x25), ReadWrite(0,2,0,7,8) };
        foreach (var request in requests)
        {
            for (var split = 1; split < request.Length; split++)
            {
                var framer = new ModbusRequestFramer();
                Assert.Empty(framer.Feed(request[..split]));
                Assert.Equal(request, Assert.Single(framer.Feed(request[split..], true)));
            }
        }
        Assert.Equal(requests, new ModbusRequestFramer().Feed([.. requests[0], .. requests[1]], true).ToArray());
    }
    [Fact] public void TruncatedCombinedPrefixCannotHideAValidRequestAtIdle()
    {
        var good = Packet(1,22,0,0,0,0xF2,0,0x25);
        byte[] prefix = [1,23,0,0,0,1,0,0,0,100,200];
        Assert.Equal(good, Assert.Single(new ModbusRequestFramer().Feed([.. prefix, .. good], true)));
    }
}
