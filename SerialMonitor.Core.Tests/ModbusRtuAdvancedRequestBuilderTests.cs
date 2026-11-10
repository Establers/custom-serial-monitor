using SerialMonitor.Core.Protocols;

namespace SerialMonitor.Core.Tests;

public sealed class ModbusRtuAdvancedRequestBuilderTests
{
    [Theory]
    [InlineData(7)] [InlineData(11)] [InlineData(12)] [InlineData(17)]
    public void StatusRequestsContainNoAddressOrDummyData(byte function)
    {
        Check(ModbusRtuRequestBuilder.WithoutData(1, function), [1, function]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.WithoutData(0, function));
    }

    [Fact]
    public void ReadWriteUsesSeparateRangesAndBigEndianValues()
    {
        Check(ModbusRtuRequestBuilder.ReadWriteMultiple(1, 3, 6, 14, new ushort[] { 255, 255, 255 }),
            Convert.FromHexString("011700030006000E00030600FF00FF00FF"));
        Assert.Equal(255, ModbusRtuRequestBuilder.ReadWriteMultiple(1, 65411, 125, 65415, new ushort[121]).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.ReadWriteMultiple(0, 0, 1, 0, new ushort[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.ReadWriteMultiple(1, 0, 126, 0, new ushort[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.ReadWriteMultiple(1, 0, 1, 0, new ushort[122]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.ReadWriteMultiple(1, 65535, 2, 0, new ushort[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.ReadWriteMultiple(1, 0, 1, 65535, new ushort[2]));
    }

    [Fact]
    public void MaskFifoAndDeviceIdentificationUseTheirOwnLayouts()
    {
        Check(ModbusRtuRequestBuilder.MaskWrite(0, 4, 0x00F2, 0x0025), Convert.FromHexString("0016000400F20025"));
        Check(ModbusRtuRequestBuilder.ReadFifo(1, 65535), Convert.FromHexString("0118FFFF"));
        foreach (var access in new[] { 1, 2, 3, 4 })
            Check(ModbusRtuRequestBuilder.ReadDeviceIdentification(247, access, 255), [247, 43, 14, (byte)access, 255]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.ReadFifo(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.ReadDeviceIdentification(1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.ReadDeviceIdentification(1, 5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.ReadDeviceIdentification(1, 1, 256));
    }

    [Fact]
    public void DiagnosticsCoverEveryStandardSubfunctionAndValidateTheirData()
    {
        foreach (var sub in new[] { 0, 1, 2, 3, 4, 10, 11, 12, 13, 14, 15, 16, 17, 18, 20 })
            Check(ModbusRtuRequestBuilder.Diagnostics(1, sub, new ushort[] { 0 }), [1, 8, 0, (byte)sub, 0, 0]);
        Check(ModbusRtuRequestBuilder.Diagnostics(1, 0, new ushort[] { 0xA537, 0x1234 }), Convert.FromHexString("01080000A5371234"));
        Check(ModbusRtuRequestBuilder.Diagnostics(1, 1, new ushort[] { 0xFF00 }), Convert.FromHexString("01080001FF00"));
        Check(ModbusRtuRequestBuilder.Diagnostics(1, 3, new ushort[] { 0x0A00 }), Convert.FromHexString("010800030A00"));
        Assert.Equal(256, ModbusRtuRequestBuilder.Diagnostics(1, 0, new ushort[125]).Length);
        foreach (var sub in new[] { 5, 9, 19, 21, 65536 })
            Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.Diagnostics(1, sub, new ushort[1]));
        Assert.Throws<ArgumentException>(() => ModbusRtuRequestBuilder.Diagnostics(1, 1, new ushort[] { 1 }));
        Assert.Throws<ArgumentException>(() => ModbusRtuRequestBuilder.Diagnostics(1, 3, new ushort[] { 0x000A }));
        Assert.Throws<ArgumentException>(() => ModbusRtuRequestBuilder.Diagnostics(1, 2, new ushort[] { 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.Diagnostics(1, 2, new ushort[2]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.Diagnostics(1, 0, new ushort[126]));
    }

    [Fact]
    public void FileRecordsSupportMultipleNoncontiguousGroups()
    {
        Check(ModbusRtuRequestBuilder.ReadFileRecords(1, new ModbusFileReadRecord[] { new(4, 1, 2), new(3, 9, 2) }),
            Convert.FromHexString("01140E0600040001000206000300090002"));
        Check(ModbusRtuRequestBuilder.WriteFileRecords(0, new ModbusFileWriteRecord[] { new(4, 7, new ushort[] { 0x06AF, 0x04BE, 0x100D }), new(3, 9, new ushort[] { 0xABCD }) }),
            Convert.FromHexString("0015160600040007000306AF04BE100D06000300090001ABCD"));
        Assert.Equal(256, ModbusRtuRequestBuilder.WriteFileRecords(1, new ModbusFileWriteRecord[] { new(1, 0, new ushort[122]) }).Length);
        Check(ModbusRtuRequestBuilder.ReadFileRecords(1, new ModbusFileReadRecord[] { new(65535, 9876, 124) }), Convert.FromHexString("01140706FFFF2694007C"));
    }

    [Fact]
    public void FileRecordLimitsIncludeTheExpectedReadResponseAndCombinedWriteSize()
    {
        Assert.Throws<ArgumentException>(() => ModbusRtuRequestBuilder.ReadFileRecords(1, new ModbusFileReadRecord[] { new(1, 0, 62), new(2, 0, 62) }));
        Assert.Throws<ArgumentException>(() => ModbusRtuRequestBuilder.WriteFileRecords(1, new ModbusFileWriteRecord[] { new(1, 0, new ushort[61]), new(2, 0, new ushort[61]) }));
        foreach (var record in new[] { new ModbusFileReadRecord(0, 0, 1), new(1, 10000, 1), new(1, 9999, 2), new(1, 0, 0), new(1, 0, 125) })
            Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.ReadFileRecords(1, new[] { record }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.ReadFileRecords(1, Array.Empty<ModbusFileReadRecord>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.ReadFileRecords(1, Enumerable.Repeat(new ModbusFileReadRecord(1, 0, 1), 36).ToArray()));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.WriteFileRecords(1, Array.Empty<ModbusFileWriteRecord>()));
    }

    [Fact]
    public void CustomRequestsAreUnicastBoundedAndAppendCrcWithoutReinterpretingData()
    {
        Check(ModbusRtuRequestBuilder.Custom(1, 65, new byte[] { 0, 0xFF, 0x80 }), Convert.FromHexString("014100FF80"));
        Assert.Equal(256, ModbusRtuRequestBuilder.Custom(1, 127, new byte[252]).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.Custom(1, 65, new byte[253]));
        foreach (var code in new[] { 0, 128, 256 })
            Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.Custom(1, code, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuRequestBuilder.Custom(0, 65, []));
    }

    private static void Check(byte[] packet, byte[] expectedWithoutCrc)
    {
        Assert.Equal(expectedWithoutCrc, packet[..^2]);
        Assert.InRange(packet.Length, 4, 256);
        Assert.Equal(0, ModbusRtuCrc.Compute(packet));
    }
}
