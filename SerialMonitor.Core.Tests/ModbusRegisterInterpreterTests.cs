using SerialMonitor.Core.Protocols;

namespace SerialMonitor.Core.Tests;

public sealed class ModbusRegisterInterpreterTests
{
    [Theory]
    [InlineData("41C80000", false, false)]
    [InlineData("C8410000", true, false)]
    [InlineData("000041C8", false, true)]
    [InlineData("0000C841", true, true)]
    public void Float32SupportsAllByteAndWordOrdersWithoutChangingTheFrame(string data, bool bytes, bool words)
    {
        var frame = Response(data);
        var original = frame.Bytes.ToArray();
        var value = Assert.Single(ModbusRegisterInterpreter.Interpret(frame, ModbusValueType.Float32, swapBytes: bytes, swapWords: words).Values);
        Assert.Equal(25d, value.RawValue);
        Assert.Equal(25d, value.Value);
        Assert.Equal(2, value.RegisterCount);
        Assert.Equal(original, frame.Bytes.ToArray());
    }

    [Theory]
    [InlineData(ModbusValueType.UInt16, "FFFF", 65535d)]
    [InlineData(ModbusValueType.Int16, "FFFF", -1d)]
    [InlineData(ModbusValueType.Int16, "8000", -32768d)]
    [InlineData(ModbusValueType.UInt32, "FFFFFFFF", 4294967295d)]
    [InlineData(ModbusValueType.UInt32, "80000001", 2147483649d)]
    [InlineData(ModbusValueType.Int32, "FFFFFFFF", -1d)]
    [InlineData(ModbusValueType.Int32, "80000000", -2147483648d)]
    [InlineData(ModbusValueType.Int32, "80000001", -2147483647d)]
    [InlineData(ModbusValueType.Float32, "C1C80000", -25d)]
    public void NumericTypesPreserveSignednessAndAllIntegerBits(ModbusValueType type, string data, double expected)
    {
        Assert.Equal(expected, Assert.Single(ModbusRegisterInterpreter.Interpret(Response(data), type).Values).RawValue);
    }

    [Fact]
    public void OffsetScaleAndRemainderUseResponseRelativeRegisterPositions()
    {
        var result = ModbusRegisterInterpreter.Interpret(Response("FFFF41C800001234"), ModbusValueType.Float32, startRegister: 1, scale: 0.1);
        var value = Assert.Single(result.Values);
        Assert.Equal(1, value.RegisterOffset);
        Assert.Equal(25, value.RawValue);
        Assert.Equal(2.5, value.Value);
        Assert.Equal(1, result.TrailingRegisters);
        var partial = ModbusRegisterInterpreter.Interpret(Response("1234"), ModbusValueType.Float32);
        Assert.Empty(partial.Values);
        Assert.Equal(1, partial.TrailingRegisters);
    }

    [Fact]
    public void SixteenBitByteSwapIgnoresWordSwapAndAppliesNegativeAndZeroScale()
    {
        var frame = Response("FA000100");
        var values = ModbusRegisterInterpreter.Interpret(frame, ModbusValueType.UInt16, swapBytes: true, swapWords: true, scale: -0.1).Values;
        Assert.Equal(new[] { -25d, -0.1 }, values.Select(value => value.Value));
        Assert.All(ModbusRegisterInterpreter.Interpret(frame, ModbusValueType.Int16, scale: 0).Values, value => Assert.Equal(0, value.Value));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(23)]
    public void SupportedRegisterResponsesDoNotRequireAnObservedRequest(byte function)
    {
        Assert.True(ModbusRegisterInterpreter.CanInterpret(Response("00FA0064", function)));
    }

    [Fact]
    public void RejectsRequestsAmbiguousRolesExceptionsBadCrcAndUnsupportedFunctions()
    {
        var frame = Response("00FA0064");
        foreach (var kind in new[] { ModbusFrameKind.Request, ModbusFrameKind.RequestOrResponse, ModbusFrameKind.Exception, ModbusFrameKind.Incomplete, ModbusFrameKind.Undecoded })
            Assert.False(ModbusRegisterInterpreter.CanInterpret(frame with { Kind = kind }));
        Assert.False(ModbusRegisterInterpreter.CanInterpret(frame with { CrcStatus = ModbusCrcStatus.Invalid }));
        Assert.False(ModbusRegisterInterpreter.CanInterpret(frame with { CrcStatus = ModbusCrcStatus.Unavailable }));
        var corrupted = frame.Bytes.ToArray();
        corrupted[^1] ^= 1;
        Assert.False(ModbusRegisterInterpreter.CanInterpret(frame with { Bytes = corrupted }));
        Assert.False(ModbusRegisterInterpreter.CanInterpret(Response("00FA0064", 1)));
        Assert.Throws<ArgumentException>(() => ModbusRegisterInterpreter.Interpret(frame with { Kind = ModbusFrameKind.Request }, ModbusValueType.UInt16));
    }

    [Fact]
    public void ValidCrcDoesNotPermitMalformedLengthsOrReservedAddresses()
    {
        foreach (var hex in new[] { "010300", "010301FA", "01030400FA", "01030200FA0064", "00030200FA", "F8030200FA" })
            Assert.False(ModbusRegisterInterpreter.CanInterpret(Frame(Convert.FromHexString(hex))));
    }

    [Fact]
    public void MaximumResponseAndInvalidOptionsStayBounded()
    {
        var frame = Response(new string('0', 500));
        var result = ModbusRegisterInterpreter.Interpret(frame, ModbusValueType.UInt16);
        Assert.Equal(125, result.Values.Count);
        Assert.Equal(124, result.Values[^1].RegisterOffset);
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRegisterInterpreter.Interpret(frame, ModbusValueType.UInt16, startRegister: 125));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRegisterInterpreter.Interpret(frame, ModbusValueType.UInt16, startRegister: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRegisterInterpreter.Interpret(frame, (ModbusValueType)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRegisterInterpreter.Interpret(frame, ModbusValueType.UInt16, scale: double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRegisterInterpreter.Interpret(frame, ModbusValueType.UInt16, scale: double.NaN));
    }

    [Fact]
    public void NonFiniteDeviceValuesAndScalingOverflowRemainDistinguishable()
    {
        var values = ModbusRegisterInterpreter.Interpret(Response("7FC000007F800000FF800000"), ModbusValueType.Float32).Values;
        Assert.True(double.IsNaN(values[0].Value));
        Assert.True(double.IsPositiveInfinity(values[1].Value));
        Assert.True(double.IsNegativeInfinity(values[2].Value));
        Assert.All(values, value => Assert.False(value.ScalingOverflow));
        Assert.True(Assert.Single(ModbusRegisterInterpreter.Interpret(Response("FFFF"), ModbusValueType.UInt16, scale: double.MaxValue).Values).ScalingOverflow);
    }

    private static ModbusRtuFrame Response(string data, byte function = 3)
    {
        var payload = Convert.FromHexString(data);
        return Frame(new byte[] { 1, function, (byte)payload.Length }.Concat(payload).ToArray());
    }

    private static ModbusRtuFrame Frame(byte[] body)
    {
        var crc = ModbusRtuCrc.Compute(body);
        var bytes = body.Concat(new byte[] { (byte)crc, (byte)(crc >> 8) }).ToArray();
        return new(1, 1, DateTimeOffset.UnixEpoch, ProtocolDirection.Rx, bytes, ModbusFrameKind.Response, ModbusCrcStatus.Valid);
    }
}
