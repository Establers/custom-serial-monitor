using SerialMonitor.Core.Protocols;

namespace SerialMonitor.Core.Tests;

public sealed class ModbusRtuTests
{
    internal static readonly byte[] Request = Convert.FromHexString("010300000002C40B");
    private static readonly DateTimeOffset Time = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void KnownCrcVector_UsesModbusPolynomialAndLowByteFirst()
    {
        Assert.Equal(0x0BC4, ModbusRtuCrc.Compute(Request.AsSpan(0, 6)));
        Assert.Equal(0, ModbusRtuCrc.Compute(Request));
    }

    [Fact]
    public void EveryReadSplit_AndCoalescedFrames_PreservePayload()
    {
        var response = Frame(1, 3, 4, 0, 250, 0, 100);
        for (var split = 0; split <= Request.Length; split++)
        {
            var packets = new List<ModbusRtuFrame>();
            var parser = new ModbusRtuFramer(ProtocolDirection.Rx);
            parser.Feed(Request.AsSpan(0, split), Time, packets.Add);
            parser.Feed(Request.AsSpan(split).ToArray().Concat(response).Concat(response).ToArray(), Time, packets.Add);
            parser.Flush(packets.Add);
            Assert.Equal(3, packets.Count);
            Assert.Equal(Request, packets[0].Bytes.ToArray());
            Assert.Equal(ModbusFrameKind.Request, packets[0].Kind);
            Assert.Equal(ModbusFrameKind.Response, packets[1].Kind);
            Assert.All(packets, packet => Assert.Equal(ModbusCrcStatus.Valid, packet.CrcStatus));
            Assert.Contains("+0: 250", packets[1].Describe());
            Assert.Contains("+1: 100", packets[1].Describe());
        }
    }

    [Fact]
    public void BadCrcThenValidResponse_ResynchronizesWithoutValidatingBadData()
    {
        var bad = Request.ToArray(); bad[^1] ^= 1;
        var response = Frame(1, 3, 2, 0, 25);
        var packets = Parse(bad.Concat(response).ToArray());
        Assert.Equal(2, packets.Count);
        Assert.Equal(ModbusCrcStatus.Invalid, packets[0].CrcStatus);
        Assert.Equal(ModbusFrameKind.Incomplete, packets[0].Kind);
        Assert.DoesNotContain("16-bit register values", packets[0].Describe());
        Assert.Equal(response, packets[1].Bytes.ToArray());
        Assert.Equal(ModbusCrcStatus.Valid, packets[1].CrcStatus);
    }

    [Fact]
    public void TruncatedAndUnknownFrames_AreNotInventedAsRegisterValues()
    {
        var truncated = Assert.Single(Parse(Request[..3]));
        Assert.Equal(ModbusCrcStatus.Unavailable, truncated.CrcStatus);
        var unknown = Assert.Single(Parse(Frame(1, 0x41, 0x12, 0x34, 0x56)));
        Assert.Equal(ModbusFrameKind.Undecoded, unknown.Kind);
        Assert.Equal(ModbusCrcStatus.Valid, unknown.CrcStatus);
    }

    [Fact]
    public void ExceptionsAndEchoLikeLayouts_AreExplicit()
    {
        var exception = Assert.Single(Parse(Frame(1, 0x83, 2)));
        Assert.Equal(ModbusFrameKind.Exception, exception.Kind);
        Assert.Contains("Illegal data address", exception.Describe());
        var echoedWrite = Assert.Single(Parse(Frame(1, 6, 0, 0, 0, 10)));
        Assert.Equal(ModbusFrameKind.RequestOrResponse, echoedWrite.Kind);
        var sentWrite = Assert.Single(Parse(Frame(1, 6, 0, 0, 0, 10), ProtocolDirection.Tx));
        Assert.Equal(ModbusFrameKind.Request, sentWrite.Kind);
    }

    [Fact]
    public void MaximumSizedFrame_AndLongNoise_StayWithinFrameLimit()
    {
        var packet = Frame(new byte[] { 1, 3, 250 }.Concat(new byte[250]).ToArray());
        var parsed = Assert.Single(Parse(packet));
        Assert.Equal(255, parsed.Length);
        var maximum = Assert.Single(Parse(Frame(new byte[] { 1, 0x41 }.Concat(new byte[252]).ToArray())));
        Assert.Equal(256, maximum.Length);
        Assert.Equal(ModbusCrcStatus.Valid, maximum.CrcStatus);
        var noise = Parse(Enumerable.Repeat((byte)0xFF, 100_000).ToArray());
        Assert.All(noise, item => Assert.InRange(item.Length, 1, 256));
        Assert.Equal(100_000, noise.Sum(item => item.Length));
        Assert.DoesNotContain(noise, item => item.CrcStatus == ModbusCrcStatus.Valid);
    }

    [Fact]
    public void CancelledFeed_StopsProcessing()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new ModbusRtuFramer(ProtocolDirection.Rx)
            .Feed(Request, Time, _ => { }, cancellation.Token));
    }

    [Theory]
    [InlineData("0141AA010300000002C40BBBCCDCEE")]
    [InlineData("01080000010300000002C40BF3AA")]
    public void NestedRequestBytes_InValidUnsupportedPayload_DoNotBecomeInventedRequests(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        Assert.Equal(0, ModbusRtuCrc.Compute(bytes));
        var packet = Assert.Single(Parse(bytes));
        Assert.Equal(bytes, packet.Bytes.ToArray());
        Assert.Equal(ModbusCrcStatus.Valid, packet.CrcStatus);
        Assert.Equal(ModbusFrameKind.Undecoded, packet.Kind);
    }

    [Fact]
    public void LongerDiagnosticFrame_WithValidShortPrefix_PreservesOuterFrame()
    {
        var shortFrame = Frame(1, 8, 0, 0, 0, 0);
        var outer = Frame(shortFrame.Concat(new byte[] { 0x12, 0x34 }).ToArray());
        var packet = Assert.Single(Parse(outer));
        Assert.Equal(outer, packet.Bytes.ToArray());
        Assert.Equal(ModbusFrameKind.Undecoded, packet.Kind);
    }

    [Fact]
    public void ResponseWithCrcValidRequestPrefix_IsNotSplitAtEightBytes()
    {
        var response = Convert.FromHexString("010304000001853A00");
        Assert.Equal(0, ModbusRtuCrc.Compute(response));
        Assert.Equal(0, ModbusRtuCrc.Compute(response.AsSpan(0, 8)));
        for (var split = 0; split <= response.Length; split++)
        {
            var parser = new ModbusRtuFramer(ProtocolDirection.Rx);
            var packets = new List<ModbusRtuFrame>();
            parser.Feed(response.AsSpan(0, split), Time, packets.Add);
            parser.Feed(response.AsSpan(split), Time, packets.Add);
            parser.Flush(packets.Add);
            var packet = Assert.Single(packets);
            Assert.Equal(ModbusFrameKind.Response, packet.Kind);
            Assert.Equal(response, packet.Bytes.ToArray());
        }
    }

    [Fact]
    public void RealRequestFollowedByBroadcast_IsNotMisreadAsLongerResponse()
    {
        var request = Convert.FromHexString("010304000001853A");
        var broadcast = Frame(0, 6, 0, 0, 0, 1);
        var packets = Parse(request.Concat(broadcast).ToArray());
        Assert.Equal(2, packets.Count);
        Assert.Equal(ModbusFrameKind.Request, packets[0].Kind);
        Assert.Equal(request, packets[0].Bytes.ToArray());
        Assert.Equal(broadcast, packets[1].Bytes.ToArray());
    }

    [Fact]
    public void CrcValidity_IsIndependentOfAddressOrExceptionMeaning()
    {
        foreach (var bytes in new[] { Convert.FromHexString("FA0300000002D180"), Frame(250, 0x83, 2) })
        {
            var packet = Assert.Single(Parse(bytes));
            Assert.Equal(0, ModbusRtuCrc.Compute(bytes));
            Assert.Equal(ModbusCrcStatus.Valid, packet.CrcStatus);
            Assert.Equal(ModbusFrameKind.Undecoded, packet.Kind);
        }
    }

    [Fact]
    public void CompleteCorruptResponse_ProtectsItsPayloadFromRecovery()
    {
        var bytes = Convert.FromHexString("01030A010300000002C40B1122D8F9");
        var parser = new ModbusRtuFramer(ProtocolDirection.Rx);
        var packets = new List<ModbusRtuFrame>();
        parser.Feed(bytes, Time, packets.Add, qualifiedBoundaryAtEnd: true);
        var packet = Assert.Single(packets);
        Assert.Equal(bytes, packet.Bytes.ToArray());
        Assert.Equal(ModbusCrcStatus.Invalid, packet.CrcStatus);
        Assert.Equal(ModbusFrameKind.Incomplete, packet.Kind);
    }

    [Fact]
    public void MaximumSizedCorruptFrame_AlsoProtectsItsNestedPayload()
    {
        var body = new byte[254]; body[0] = 1; body[1] = 12; body[2] = 251;
        Request.CopyTo(body, 10);
        var bytes = Frame(body); bytes[^1] ^= 1;
        var packets = new List<ModbusRtuFrame>();
        new ModbusRtuFramer(ProtocolDirection.Rx).Feed(bytes, Time, packets.Add, qualifiedBoundaryAtEnd: true);
        var packet = Assert.Single(packets);
        Assert.Equal(256, packet.Length);
        Assert.Equal(ModbusCrcStatus.Invalid, packet.CrcStatus);
    }

    [Fact]
    public void BufferedFrames_PreserveTheirOriginalChunkObservationTimes()
    {
        var parser = new ModbusRtuFramer(ProtocolDirection.Rx);
        var packets = new List<ModbusRtuFrame>();
        parser.Feed(Request.AsSpan(0, 3), Time, packets.Add);
        parser.Feed(Request.AsSpan(3), Time.AddMilliseconds(10), packets.Add);
        parser.Feed(Request, Time.AddMilliseconds(20), packets.Add);
        parser.Flush(packets.Add);
        Assert.Equal(2, packets.Count);
        Assert.Equal(Time, packets[0].Timestamp);
        Assert.Equal(Time.AddMilliseconds(20), packets[1].Timestamp);
    }

    private static List<ModbusRtuFrame> Parse(byte[] bytes, ProtocolDirection direction = ProtocolDirection.Rx)
    {
        var packets = new List<ModbusRtuFrame>();
        var parser = new ModbusRtuFramer(direction);
        parser.Feed(bytes, Time, packets.Add);
        parser.Flush(packets.Add);
        return packets;
    }

    // Independent bitwise test encoder; fixtures are not built with the
    // production table implementation being verified.
    internal static byte[] Frame(params byte[] body)
    {
        var crc = 0xFFFF;
        foreach (var value in body)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
        }
        return body.Concat(new[] { (byte)crc, (byte)(crc >> 8) }).ToArray();
    }
}
