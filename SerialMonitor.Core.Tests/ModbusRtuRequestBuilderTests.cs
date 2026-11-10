using SerialMonitor.Core.Protocols;

namespace SerialMonitor.Core.Tests;

public sealed class ModbusRtuRequestBuilderTests
{
    [Fact]
    public void KnownRequestIncludesLowByteFirstCrc() =>
        Assert.Equal(Convert.FromHexString("010300000002C40B"), ModbusRtuRequestBuilder.Read(1, 3, 0, 2));

    [Theory]
    [InlineData(1, 2000)]
    [InlineData(2, 2000)]
    [InlineData(3, 125)]
    [InlineData(4, 125)]
    public void ReadsEnforceQuantityAndAddressBounds(byte function, int maximum)
    {
        var packet = ModbusRtuRequestBuilder.Read(247, function, 65536 - maximum, maximum);
        Assert.Equal(8, packet.Length);
        Assert.Equal(0, ModbusRtuCrc.Compute(packet));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.Read(1, function, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.Read(1, function, 0, maximum + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.Read(1, function, 65535, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.Read(0, function, 0, 1));
    }

    [Theory]
    [InlineData(0, "010500000000CDCA")]
    [InlineData(1, "01050000FF008C3A")]
    public void SingleCoilEncodesOnlyTheStandardOffAndOnWords(ushort value, string expected) =>
        Assert.Equal(Convert.FromHexString(expected), ModbusRtuRequestBuilder.WriteSingle(1, 5, 0, value));

    [Fact]
    public void RegisterWordsAreBigEndianAndBroadcastWritesAreAllowed()
    {
        var single = ModbusRtuRequestBuilder.WriteSingle(0, 6, 65535, 0xABCD);
        Assert.Equal(Convert.FromHexString("0006FFFFABCD"), single[..^2]);
        Assert.Equal(0, ModbusRtuCrc.Compute(single));
        var multiple = ModbusRtuRequestBuilder.WriteMultiple(17, 16, 1, new ushort[] { 10, 0x0102 });
        Assert.Equal(Convert.FromHexString("11100001000204000A0102"), multiple[..^2]);
        Assert.Equal(0, ModbusRtuCrc.Compute(multiple));
    }

    [Fact]
    public void MultipleCoilsPackLowBitFirstAndZeroTheUnusedBits()
    {
        var packet = ModbusRtuRequestBuilder.WriteMultiple(17, 15, 19, new ushort[] { 1, 0, 1, 1, 0, 0, 1, 1, 1, 0 });
        Assert.Equal(Convert.FromHexString("110F0013000A02CD01"), packet[..^2]);
        Assert.Equal(0, ModbusRtuCrc.Compute(packet));
    }

    [Theory]
    [InlineData(15, 1968, 255)]
    [InlineData(16, 123, 255)]
    public void MultipleWritesAreBoundedBeforePacketAllocation(byte function, int maximum, int packetLength)
    {
        var values = new ushort[maximum];
        Assert.Equal(packetLength, ModbusRtuRequestBuilder.WriteMultiple(0, function, 65536 - maximum, values).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.WriteMultiple(1, function, 0, Array.Empty<ushort>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.WriteMultiple(1, function, 0, new ushort[maximum + 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.WriteMultiple(1, function, 65535, new ushort[2]));
    }

    [Fact]
    public void InvalidFieldsCannotWrapOrProduceNonstandardRequests()
    {
        foreach (var unit in new[] { -1, 248, 256 })
            Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.WriteSingle(unit, 6, 0, 0));
        foreach (var address in new[] { -1, 65536 })
            Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.WriteSingle(1, 6, address, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.WriteSingle(1, 5, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.WriteMultiple(1, 15, 0, new ushort[] { 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.Read(1, 6, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.WriteSingle(1, 16, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.WriteMultiple(1, 3, 0, new ushort[] { 1 }));
    }
}
