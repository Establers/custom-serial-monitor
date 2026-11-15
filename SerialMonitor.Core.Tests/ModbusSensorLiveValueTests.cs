using System.Buffers.Binary;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;

namespace SerialMonitor.Core.Tests;
public sealed class ModbusSensorLiveValueTests
{
    private static ModbusSensorDefinition Definition(params ModbusSensorPoint[] points) => new()
    { SchemaVersion=1, Name="Live", UnitId=1, Points=points.ToList() };
    private static byte[] Packet(params byte[] payload)
    {
        var crc = ModbusRtuCrc.Compute(payload); return [..payload,(byte)crc,(byte)(crc>>8)];
    }
    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void LocalValuesCanChangeEveryAreaWithoutGrantingMasterWriteAccess(int area)
    {
        var engine = new ModbusSensorEngine(Definition(new ModbusSensorPoint {Name="Input",Area=area,Address=0,InitialValue="0"}));
        var initial = engine.GetSnapshot();
        engine.SetPointValue(0,"1");
        Assert.Equal((ushort)1,engine.GetSnapshot().Points[0].Words[0]);
        Assert.Equal(ModbusSensorChangeSource.User,engine.GetSnapshot().Points[0].Source);
        Assert.Equal((ushort)0,initial.Points[0].Words[0]);
        var response = engine.Respond(ModbusRtuRequestBuilder.Read(1,(byte)area,0,1))!;
        Assert.Equal((byte)1,response[^3]);
        if (area is 1 or 3)
        {
            var write = engine.Respond(ModbusRtuRequestBuilder.WriteSingle(1,(byte)(area == 1 ? 5 : 6),0,0))!;
            Assert.Equal(2,write[2]); Assert.Equal((ushort)1,engine.GetSnapshot().Points[0].Words[0]);
        }
    }
    [Fact] public void ScaledSwappedFloatEditsAndRestoresKeepWholeValuesAndDefinitions()
    {
        var point = new ModbusSensorPoint {Name="Temperature",Area=4,Address=25,InitialValue="25.5",ValueType=ModbusValueType.Float32,Scale=0.1,SwapBytes=true,SwapWords=true};
        var definition = Definition(point,new() {Name="Setting",Area=3,Address=1,InitialValue="10",Writable=true});
        var engine = new ModbusSensorEngine(definition);
        engine.SetPointValue(0,"35.75"); engine.SetPointValue(1,"30");
        Assert.Equal(35.75,ModbusItemCodec.Decode(engine.GetSnapshot().Points[0].Words.AsSpan(),point.Item()).Value,5);
        engine.ResetValues(0);
        Assert.Equal(25.5,ModbusItemCodec.Decode(engine.GetSnapshot().Points[0].Words.AsSpan(),point.Item()).Value,5);
        Assert.Equal((ushort)30,engine.GetSnapshot().Points[1].Words[0]);
        Assert.Equal(ModbusSensorChangeSource.Reset,engine.GetSnapshot().Points[0].Source);
        engine.ResetValues(); Assert.Equal((ushort)10,engine.GetSnapshot().Points[1].Words[0]);
        Assert.Equal("25.5",definition.Points[0].InitialValue); Assert.False(definition.Points[0].Writable);
    }
    [Theory] [InlineData("bad")] [InlineData("65536")] [InlineData("0.5")] [InlineData("NaN")]
    public void InvalidEditsAreAtomicAndDoNotCreateUiUpdates(string value)
    {
        var engine = new ModbusSensorEngine(Definition(new ModbusSensorPoint {Name="P",Area=3,Address=0,InitialValue="5"}));
        var before = engine.GetSnapshot();
        Assert.ThrowsAny<ArgumentException>(()=>engine.SetPointValue(0,value));
        Assert.ThrowsAny<ArgumentException>(()=>engine.ResetValues(10));
        Assert.Same(before,engine.GetSnapshot());
        engine.SetPointValue(0,"5"); Assert.Same(before,engine.GetSnapshot());
    }
    [Theory] [InlineData(5)] [InlineData(6)] [InlineData(15)] [InlineData(16)] [InlineData(22)] [InlineData(23)]
    public void AllMasterWriteFunctionsPublishTheChangedValue(int function)
    {
        var area = function is 5 or 15 ? 1 : 3;
        var engine = new ModbusSensorEngine(Definition(new ModbusSensorPoint {Name="P",Area=area,Address=0,InitialValue="0",Writable=true}));
        var before = engine.GetSnapshot();
        var request = function switch {
            5 or 6 => ModbusRtuRequestBuilder.WriteSingle(1,(byte)function,0,1),
            15 or 16 => ModbusRtuRequestBuilder.WriteMultiple(1,(byte)function,0,[1]),
            22 => Packet(1,22,0,0,0,0,0,1),
            _ => Packet(1,23,0,0,0,1,0,0,0,1,2,0,1) };
        Assert.NotNull(engine.Respond(request));
        var after = engine.GetSnapshot();
        Assert.Equal((ushort)1,after.Points[0].Words[0]); Assert.Equal(ModbusSensorChangeSource.Master,after.Points[0].Source);
        Assert.Equal((ushort)0,before.Points[0].Words[0]);
        engine.Respond(ModbusRtuRequestBuilder.Read(1,(byte)area,0,1)); Assert.Same(after,engine.GetSnapshot());
    }
    [Fact] public void PartialWordAndBroadcastWritesKeepRawStateWithoutLosingProvenance()
    {
        var engine = new ModbusSensorEngine(Definition(new ModbusSensorPoint {Name="Wide",Area=3,Address=0,InitialValue="0",ValueType=ModbusValueType.UInt32,Writable=true}));
        Assert.Null(engine.Respond(ModbusRtuRequestBuilder.WriteSingle(0,6,1,65535)));
        var state = engine.GetSnapshot().Points[0];
        Assert.Equal(new ushort[] {0,65535},state.Words); Assert.Equal(ModbusSensorChangeSource.Master,state.Source);
        var response = engine.Respond(ModbusRtuRequestBuilder.Read(1,3,0,2))!;
        Assert.Equal((uint)65535,BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(3)));
    }
}
