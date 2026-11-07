using System.Buffers.Binary;
using System.Text.Json;
using SerialMonitor.Core.Protocols;
using SerialMonitor.Core.Protocols.VirtualModbus;

namespace SerialMonitor.Core.Tests;

public sealed class VirtualModbusDeviceTests
{
    internal static VirtualModbusRegisterDefinition Point(int address = 0, string area = "holding", string type = "uint16", object? value = null) => new()
    { Name = $"Point {address}", Address = address, Area = area, DataType = type, Value = JsonSerializer.SerializeToElement(value ?? 250) };
    internal static VirtualModbusDefinition Map(params VirtualModbusRegisterDefinition[] points) => new()
    { SchemaVersion = 1, Devices = [new() { Name = "Sensor", UnitId = 1, Registers = points.ToList() }] };
    internal static byte[] Adu(string hex)
    {
        var body = Convert.FromHexString(hex);
        var result = new byte[body.Length + 2]; body.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(body.Length), ModbusRtuCrc.Compute(body));
        return result;
    }
    private static VirtualModbusDeviceStore Store(params VirtualModbusRegisterDefinition[] points)
    { var store = new VirtualModbusDeviceStore(); store.Add(Map(points)); return store; }

    [Fact]
    public void ReadHolding_KnownWireResponse()
    {
        var result = Store(Point(), Point(1, value: 100)).ProcessRequest(Adu("010300000002"));
        Assert.Equal("01030400FA0064DBE9", Convert.ToHexString(result.Response!));
        Assert.True(result.Valid); Assert.True(result.Matched); Assert.False(result.IsException);
    }

    [Theory]
    [InlineData("bigEndian", "highFirst", 0x4148, 0x0000)]
    [InlineData("bigEndian", "lowFirst", 0x0000, 0x4148)]
    [InlineData("littleEndian", "highFirst", 0x4841, 0x0000)]
    [InlineData("littleEndian", "lowFirst", 0x0000, 0x4841)]
    public void Float32_ByteAndWordOrders(string bytes, string words, int first, int second)
    {
        var point = Point(type: "float32", value: 12.5) with { ByteOrder = bytes, WordOrder = words };
        Assert.Equal(new ushort[] { (ushort)first, (ushort)second }, VirtualModbusValueCodec.InitialWords(point));
        Assert.Equal("12.5", VirtualModbusValueCodec.Decode(point, VirtualModbusValueCodec.InitialWords(point)));
    }

    [Theory]
    [InlineData("int16", -12.3, 0.1, -123)]
    [InlineData("uint16", 25, 0.1, 250)]
    [InlineData("int16", 12, -0.5, -24)]
    public void EngineeringValues_UseScale(string type, double value, double scale, int raw)
    {
        var point = Point(type: type, value: value) with { Scale = scale };
        var words = VirtualModbusValueCodec.InitialWords(point);
        Assert.Equal(unchecked((ushort)raw), words[0]);
        Assert.Equal(value, double.Parse(VirtualModbusValueCodec.Decode(point, words), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("uint16", "-1")]
    [InlineData("uint16", "65536")]
    [InlineData("int16", "32768")]
    [InlineData("uint32", "4294967296")]
    [InlineData("float32", "1e100")]
    [InlineData("float64", "NaN")]
    public void OutOfRangeEdit_IsRejected(string type, string value)
    { Assert.Throws<FormatException>(() => VirtualModbusValueCodec.Encode(Point(type: type), value)); }

    [Fact]
    public void UiEdits_AndProductWrites_ReadBackAndPersistExactBits()
    {
        var store = Store(Point());
        store.SetValue(1, 0, "123");
        Assert.Equal("123", store.Page(1, 0)[0].Value);
        Assert.Equal(Adu("01060000002A"), store.ProcessRequest(Adu("01060000002A")).Response);
        var restored = new VirtualModbusDeviceStore();
        restored.Add(VirtualModbusJson.Parse(VirtualModbusJson.Serialize(store.Export())));
        Assert.Equal("42", restored.Page(1, 0)[0].Value);
    }

    [Theory]
    [InlineData("010300000000", 3)]
    [InlineData("01030000007E", 3)]
    [InlineData("0103FFFF0002", 2)]
    [InlineData("010300010001", 2)]
    [InlineData("01060000000100", 3)]
    [InlineData("0110000000020400010002", 2)]
    [InlineData("0110000000010400010002", 3)]
    [InlineData("010800000000", 1)]
    public void InvalidRequests_ReturnProtocolExceptionsWithoutMutation(string request, int code)
    {
        var store = Store(Point());
        var response = store.ProcessRequest(Adu(request));
        Assert.True(response.IsException); Assert.Equal(code, response.Response![2]);
        Assert.Equal("250", store.Page(1, 0)[0].Value);
    }

    [Fact]
    public void ReadOnlyHolding_RejectsProductWrite_ButAllowsUiEdit()
    {
        var store = Store(Point() with { Writable = false });
        Assert.Equal(2, store.ProcessRequest(Adu("010600000001")).Response![2]);
        store.SetValue(1, 0, "12"); Assert.Equal("12", store.Page(1, 0)[0].Value);
    }

    [Fact]
    public void Broadcast_WritesAllMappedDevices_WithoutResponse()
    {
        var store = Store(Point());
        store.Add(Map(Point()) with { Devices = [new() { Name = "Second", UnitId = 2, Registers = [Point()] }] });
        var reply = store.ProcessRequest(Adu("00060000002A"));
        Assert.Null(reply.Response); Assert.True(reply.Broadcast);
        Assert.Equal("42", store.Page(1, 0)[0].Value); Assert.Equal("42", store.Page(2, 0)[0].Value);
        Assert.Null(store.ProcessRequest(Adu("000300000001")).Response);
    }

    [Fact]
    public void UnknownId_BadCrc_AndIncomingException_DoNotReply()
    {
        var store = Store(Point());
        Assert.Null(store.ProcessRequest(Adu("020300000001")).Response);
        var corrupt = Adu("010600000001"); corrupt[^1] ^= 1;
        Assert.False(store.ProcessRequest(corrupt).Valid);
        Assert.False(store.ProcessRequest(Adu("018303")).Valid);
        Assert.Equal("250", store.Page(1, 0)[0].Value);
    }

    [Fact]
    public void Coils_Discretes_AndMultipleBitWrites()
    {
        var points = Enumerable.Range(0, 9).Select(i => Point(i, "coil", "bool", i % 2 == 0)).ToList();
        points.Add(Point(0, "discrete", "bool", true));
        var store = Store(points.ToArray());
        Assert.Equal(Adu("0101025501"), store.ProcessRequest(Adu("010100000009")).Response);
        Assert.Equal(Adu("010F00000009"), store.ProcessRequest(Adu("010F0000000902AA00")).Response);
        Assert.Equal(Adu("010102AA00"), store.ProcessRequest(Adu("010100000009")).Response);
        Assert.Equal(Adu("01020101"), store.ProcessRequest(Adu("010200000001")).Response);
        Assert.Equal(3, store.ProcessRequest(Adu("010500000001")).Response![2]);
        Assert.Equal(Adu("01050000FF00"), store.ProcessRequest(Adu("01050000FF00")).Response);
    }

    [Fact]
    public void MultipleRegisters_AreAtomic_AndFloatNonfiniteBitsCanExport()
    {
        var store = Store(Point(type: "float32", value: 1.0));
        Assert.Equal(Adu("011000000002"), store.ProcessRequest(Adu("011000000002047FC00000")).Response);
        Assert.Equal("NaN", store.Page(1, 0)[0].Value);
        var restored = new VirtualModbusDeviceStore(); restored.Add(VirtualModbusJson.Parse(VirtualModbusJson.Serialize(store.Export())));
        Assert.Equal(new ushort[] { 0x7FC0, 0 }, restored.Page(1, 0)[0].RawWords);
    }

    [Fact]
    public void Add_IsAtomic_ForDuplicateIdsAndOverlappingMultiwordRanges()
    {
        var store = Store(Point());
        Assert.Throws<FormatException>(() => store.Add(Map(Point())));
        Assert.Single(store.Devices());
        Assert.Throws<FormatException>(() => Store(Point(type: "float32", value: 1), Point(1)));
        var invalid = Map(Point()) with { Devices = [new() { Name = "New", UnitId = 2, Registers = [Point()] }, new() { Name = "Conflict", UnitId = 1, Registers = [Point()] }] };
        Assert.Throws<FormatException>(() => store.Add(invalid)); Assert.Single(store.Devices());
    }

    [Fact]
    public void IdChangeAndRemoval_ChangeRoutingImmediately()
    {
        var store = Store(Point()); store.ChangeUnitId(1, 7);
        Assert.Null(store.ProcessRequest(Adu("010300000001")).Response);
        Assert.NotNull(store.ProcessRequest(Adu("070300000001")).Response);
        store.Remove(7); Assert.Empty(store.Devices());
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"devices\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"devices\":[],\"comment\":\"extra\"}")]
    [InlineData("{\"schemaVersion\":2,\"devices\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"devices\":null}")]
    [InlineData("{\"devices\":[]}")]
    [InlineData("null")]
    public void InvalidJson_IsActionableFormatError(string json) => Assert.Throws<FormatException>(() => VirtualModbusJson.Parse(json));

    [Fact]
    public void MaximumWorkspace_UnicodeAndLiveWrites_AlwaysRoundTrips()
    {
        var points = Enumerable.Range(0, 8192).Select(i => Point(i) with { Name = $"온도 센서 {i}", Description = "페이지 12: 시험 값" }).ToArray();
        var store = Store(points);
        store.SetValue(1, 0, "65535"); store.ChangeUnitId(1, 247);
        var json = VirtualModbusJson.Serialize(store.Export());
        var restored = new VirtualModbusDeviceStore(); restored.Add(VirtualModbusJson.Parse(json));
        Assert.Equal(8192, restored.Devices()[0].RegisterCount); Assert.Equal("65535", restored.Page(247, 0)[0].Value);
    }

    [Fact]
    public void PathologicalMetadata_IsRejectedBeforeStoreMutation()
    {
        var point = Point() with { Description = new string('\0', 1024) };
        var store = new VirtualModbusDeviceStore();
        Assert.Throws<FormatException>(() => store.Add(Map(Enumerable.Range(0, 8192).Select(i => point with { Address = i }).ToArray())));
        Assert.Empty(store.Devices());
    }
}
