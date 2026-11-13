using System.Buffers.Binary;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;

namespace SerialMonitor.Core.Tests;

public sealed class ModbusResponseDecoderTests
{
    [Theory]
    [InlineData(1, 2000)]
    [InlineData(2, 2000)]
    [InlineData(3, 125)]
    [InlineData(4, 125)]
    public void ReadDefinitionUsesBuilderAndRejectsInvalidWireRanges(byte function, int maximum)
    {
        var valid = new ModbusReadRequest(247, function, 65536 - maximum, maximum);
        valid.Validate();
        Assert.Equal(ModbusRtuRequestBuilder.Read(247, function, 65536 - maximum, maximum), valid.BuildPacket());
        foreach (var invalid in new[]
        {
            valid with { UnitId = 0 }, valid with { UnitId = 248 },
            valid with { Function = 0 }, valid with { Function = 5 },
            valid with { Address = -1 }, valid with { Address = 65536 },
            valid with { Address = 65535, Quantity = 2 },
            valid with { Quantity = 0 }, valid with { Quantity = maximum + 1 },
            valid with { Quantity = int.MaxValue }
        })
        {
            Assert.Throws<ArgumentOutOfRangeException>(invalid.Validate);
            Assert.Throws<ArgumentOutOfRangeException>(() => invalid.BuildPacket());
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void RegisterReadPreservesEveryRawWordInWireOrder(byte function)
    {
        var request = new ModbusReadRequest(17, function, 1234, 3);
        var frame = WithCrc([17, function, 6, 0xFF, 0xFF, 0x12, 0x34, 0, 0x80]);
        var result = ModbusResponseDecoder.DecodeRead(request, frame);
        Assert.Equal(ModbusResponseStatus.Success, result.Status);
        Assert.Equal(new ushort[] { 65535, 0x1234, 128 }, result.Values);
        Assert.Null(result.ExceptionCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CoilReadUnpacksLowBitFirstAndOnlyReturnsRequestedItems(byte function)
    {
        var request = new ModbusReadRequest(17, function, 19, 10);
        var result = ModbusResponseDecoder.DecodeRead(request, WithCrc([17, function, 2, 0xCD, 1]));
        Assert.Equal(ModbusResponseStatus.Success, result.Status);
        Assert.Equal(new ushort[] { 1, 0, 1, 1, 0, 0, 1, 1, 1, 0 }, result.Values);
        Assert.Equal(ModbusResponseStatus.InvalidResponse,
            ModbusResponseDecoder.DecodeRead(request, WithCrc([17, function, 2, 0xCD, 0x81])).Status);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 8)]
    [InlineData(1, 1999)]
    [InlineData(2, 2000)]
    [InlineData(3, 1)]
    [InlineData(4, 125)]
    public void MinimumAndMaximumReadSizesDecodeExactly(byte function, int quantity)
    {
        var byteCount = function <= 2 ? (quantity + 7) / 8 : quantity * 2;
        var data = new byte[byteCount + 3];
        data[0] = 247;
        data[1] = function;
        data[2] = (byte)byteCount;
        var request = new ModbusReadRequest(247, function, 65536 - quantity, quantity);
        var result = ModbusResponseDecoder.DecodeRead(request, WithCrc(data));
        Assert.Equal(ModbusResponseStatus.Success, result.Status);
        Assert.Equal(quantity, result.Values.Length);
    }

    [Fact]
    public void BadReadCrcCannotProduceValues()
    {
        var request = new ModbusReadRequest(1, 3, 0, 1);
        var frame = WithCrc([1, 3, 2, 0, 7]);
        frame[^1] ^= 1;
        var result = ModbusResponseDecoder.DecodeRead(request, frame);
        Assert.Equal(ModbusResponseStatus.CrcError, result.Status);
        Assert.Empty(result.Values);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void EightByteBitReadRequestEchoNeverBecomesADeviceResponse(byte function)
    {
        for (var quantity = 17; quantity <= 24; quantity++)
        {
            var request = new ModbusReadRequest(1, function, 0x0300, quantity);
            var echoedRequest = request.BuildPacket();
            Assert.Equal(8, echoedRequest.Length);
            Assert.Equal(0, ModbusRtuCrc.Compute(echoedRequest));

            var ambiguous = ModbusResponseDecoder.DecodeRead(request, echoedRequest);

            Assert.Equal(ModbusResponseStatus.InvalidResponse, ambiguous.Status);
            Assert.Empty(ambiguous.Values);
            var distinguishable = ModbusResponseDecoder.DecodeRead(request, WithCrc([1, function, 3, 0, 0, 0]));
            Assert.Equal(ModbusResponseStatus.Success, distinguishable.Status);
            Assert.Equal(quantity, distinguishable.Values.Length);
        }
    }

    [Fact]
    public void ReadRejectsWrongUnitFunctionQuantityExtraBytesAndTruncation()
    {
        var request = new ModbusReadRequest(1, 3, 500, 1);
        foreach (var frame in new[]
        {
            WithCrc([2, 3, 2, 0, 7]), WithCrc([1, 4, 2, 0, 7]),
            WithCrc([1, 3, 4, 0, 7, 0, 8]), WithCrc([1, 3, 2, 0, 7, 0]),
            WithCrc([1, 3, 3, 0, 7]), WithCrc([1, 3, 2, 7]),
            Array.Empty<byte>(), new byte[] { 1, 3, 2 }, new byte[257]
        })
        {
            var result = ModbusResponseDecoder.DecodeRead(request, frame);
            Assert.Equal(ModbusResponseStatus.InvalidResponse, result.Status);
            Assert.Empty(result.Values);
        }
    }

    [Fact]
    public void ShortAndLongExceptionFramesAreRejectedAndValidExceptionRetainsCode()
    {
        var request = new ModbusReadRequest(17, 4, 0, 1);
        var result = ModbusResponseDecoder.DecodeRead(request, WithCrc([17, 0x84, 2]));
        Assert.Equal(ModbusResponseStatus.Exception, result.Status);
        Assert.Equal((byte)2, result.ExceptionCode);
        Assert.Empty(result.Values);
        foreach (var frame in new[]
        {
            WithCrc([17, 0x84]), WithCrc([17, 0x84, 0]),
            WithCrc([17, 0x84, 2, 0]), WithCrc([17, 0x83, 2])
        })
            Assert.Equal(ModbusResponseStatus.InvalidResponse, ModbusResponseDecoder.DecodeRead(request, frame).Status);

        var corrupt = WithCrc([17, 0x84, 2]);
        corrupt[^1] ^= 0x80;
        Assert.Equal(ModbusResponseStatus.CrcError, ModbusResponseDecoder.DecodeRead(request, corrupt).Status);
    }

    [Theory]
    [InlineData(5, 0)]
    [InlineData(5, 1)]
    [InlineData(6, 0)]
    [InlineData(6, 65535)]
    public void SingleWritesRequireExactEchoOfTheRequestedAddressAndValue(byte function, ushort value)
    {
        var request = ModbusRtuRequestBuilder.WriteSingle(17, function, 65535, value);
        Assert.Equal(ModbusResponseStatus.Success, ModbusResponseDecoder.DecodeWrite(request, request).Status);
        foreach (var field in new[] { 0, 1, 2, 3, 4, 5 })
        {
            var response = request[..^2];
            response[field] ^= 1;
            Assert.Equal(ModbusResponseStatus.InvalidResponse,
                ModbusResponseDecoder.DecodeWrite(request, WithCrc(response)).Status);
        }
    }

    [Theory]
    [InlineData(15, 1)]
    [InlineData(15, 1968)]
    [InlineData(16, 1)]
    [InlineData(16, 123)]
    public void MultipleWriteAcknowledgementsContainTheExactStartAndQuantity(byte function, int quantity)
    {
        var request = ModbusRtuRequestBuilder.WriteMultiple(247, function, 65536 - quantity, new ushort[quantity]);
        var ack = WithCrc(request[..6]);
        var result = ModbusResponseDecoder.DecodeWrite(request, ack);
        Assert.Equal(ModbusResponseStatus.Success, result.Status);
        Assert.Empty(result.Values);
        ack[5] ^= 1;
        Assert.Equal(ModbusResponseStatus.InvalidResponse,
            ModbusResponseDecoder.DecodeWrite(request, WithCrc(ack[..^2])).Status);
        Assert.Equal(ModbusResponseStatus.InvalidResponse, ModbusResponseDecoder.DecodeWrite(request, request).Status);
    }

    [Fact]
    public void WriteErrorsCannotBeMistakenForAnAcknowledgement()
    {
        var request = ModbusRtuRequestBuilder.WriteSingle(1, 6, 7, 12);
        var exception = ModbusResponseDecoder.DecodeWrite(request, WithCrc([1, 0x86, 3]));
        Assert.Equal(ModbusResponseStatus.Exception, exception.Status);
        Assert.Equal((byte)3, exception.ExceptionCode);
        var corrupt = request.ToArray();
        corrupt[^1] ^= 1;
        Assert.Equal(ModbusResponseStatus.CrcError, ModbusResponseDecoder.DecodeWrite(request, corrupt).Status);
        Assert.Equal(ModbusResponseStatus.InvalidResponse, ModbusResponseDecoder.DecodeWrite(request, []).Status);
        Assert.Equal(ModbusResponseStatus.InvalidResponse,
            ModbusResponseDecoder.DecodeWrite(request, WithCrc([1, 6, 0, 7, 0, 12, 0])).Status);
    }

    [Fact]
    public void MalformedWriteRequestsAndBroadcastsNeverHaveSuccessfulAcknowledgements()
    {
        var good = ModbusRtuRequestBuilder.WriteSingle(1, 6, 0, 0);
        var invalidRequests = new List<byte[]>
        {
            Array.Empty<byte>(), new byte[257], ModbusRtuRequestBuilder.WriteSingle(0, 6, 0, 0),
            WithCrc([1, 5, 0, 0, 0, 1]), WithCrc([1, 3, 0, 0, 0, 1]),
            WithCrc([1, 16, 0, 0, 0, 0, 0]), WithCrc([1, 16, 0xFF, 0xFF, 0, 2, 4, 0, 1, 0, 2]),
            WithCrc([1, 15, 0, 0, 0, 1, 1, 0x81]), WithCrc([1, 16, 0, 0, 0, 1, 3, 0, 1]),
            WithCrc([1, 15, 0, 0, 0, 9, 2, 0]), WithCrc([1, 6, 0, 0, 0, 0, 0])
        };
        var corrupt = good.ToArray();
        corrupt[^1] ^= 1;
        invalidRequests.Add(corrupt);
        foreach (var request in invalidRequests)
            Assert.Equal(ModbusResponseStatus.InvalidResponse, ModbusResponseDecoder.DecodeWrite(request, good).Status);
    }

    private static byte[] WithCrc(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[payload.Length + 2];
        payload.CopyTo(frame);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(payload.Length), ModbusRtuCrc.Compute(payload));
        return frame;
    }
}
