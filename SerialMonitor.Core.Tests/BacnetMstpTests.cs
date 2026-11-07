using SerialMonitor.Core.Protocols;

namespace SerialMonitor.Core.Tests;

public sealed class BacnetMstpTests
{
    public static readonly byte[] Request = Convert.FromHexString("55FF0301020003C84142432F9F");
    public static readonly byte[] Response = Convert.FromHexString("55FF04020100036C4142432F9F");
    internal static byte[] Frame(byte type, byte destination, byte source, byte[] data)
    {
        var bytes = new byte[8+data.Length+(data.Length>0?2:0)];
        bytes[0]=0x55; bytes[1]=0xFF; bytes[2]=type; bytes[3]=destination; bytes[4]=source;
        bytes[5]=(byte)(data.Length>>8); bytes[6]=(byte)data.Length; bytes[7]=BacnetMstpCrc.Header(bytes.AsSpan(2,5));
        data.CopyTo(bytes,8);
        if(data.Length>0) {ushort crc=BacnetMstpCrc.Data(data);bytes[^2]=(byte)crc;bytes[^1]=(byte)(crc>>8);}
        return bytes;
    }
    static List<BacnetMstpFrame> Parse(byte[] bytes, bool flush = true)
    {
        var framer=new BacnetMstpFramer(ProtocolDirection.Rx); var result=new List<BacnetMstpFrame>();
        framer.Feed(bytes,DateTimeOffset.Now,result.Add); if(flush) framer.Flush(result.Add);
        Assert.InRange(framer.PendingBytes,0,BacnetMstpFrame.MaximumBytes); return result;
    }
    [Fact]
    public void IndependentKnownVectorsAndLayout()
    {
        Assert.Equal(0x89,BacnetMstpCrc.Header("123456789"u8));
        Assert.Equal(0x906E,BacnetMstpCrc.Data("123456789"u8));
        Assert.Equal(Request,Frame(3,1,2,"ABC"u8.ToArray()));
        Assert.Equal(Response,Frame(4,2,1,"ABC"u8.ToArray()));
        var frame=Assert.Single(Parse(Response));
        Assert.Equal((byte)1,frame.Source); Assert.Equal((byte)2,frame.Destination);
        Assert.Equal(ProtocolFrameStatus.Decoded,frame.Status); Assert.Equal(ProtocolCrcStatus.Valid,frame.CrcStatus);
        Assert.Contains("Source MAC: 1; destination MAC: 2",frame.Describe());
        Assert.True(frame.MatchesCode(4)); Assert.False(frame.MatchesCode(0x84));
    }
    [Fact]
    public void AllChunkSplitsAndCoalescedFramesKeepWireOrder()
    {
        for(int split=1;split<Response.Length;split++)
        {
            var parser=new BacnetMstpFramer(ProtocolDirection.Rx); var frames=new List<BacnetMstpFrame>();
            parser.Feed(Response.AsSpan(0,split),DateTimeOffset.Now,frames.Add,qualifiedBoundaryAtEnd:true);
            Assert.Empty(frames);
            parser.Feed(Response.AsSpan(split),DateTimeOffset.Now,frames.Add);
            Assert.Equal(Response,Assert.Single(frames).Bytes.ToArray());
        }
        var packets=Parse(Request.Concat(Response).Concat(Response).ToArray());
        Assert.Equal(new byte?[]{3,4,4},packets.Select(f=>f.FrameType));
    }
    [Fact]
    public void LargeInputKeepsPartialTailAcrossAssemblyBufferCapacity()
    {
        var parser=new BacnetMstpFramer(ProtocolDirection.Rx);var frames=new List<BacnetMstpFrame>();
        parser.Feed(Enumerable.Range(0,300).SelectMany(_=>Response).Concat(Request[..7]).ToArray(),DateTimeOffset.Now,frames.Add);
        Assert.Equal(300,frames.Count);Assert.All(frames,f=>Assert.Equal(ProtocolFrameStatus.Decoded,f.Status));
        Assert.Equal(7,parser.PendingBytes);
        parser.Feed(Request.AsSpan(7),DateTimeOffset.Now,frames.Add);
        Assert.Equal(301,frames.Count);Assert.Equal(Request,frames[^1].Bytes.ToArray());Assert.Equal(0,parser.PendingBytes);
    }
    [Fact]
    public void DataCrcErrorDoesNotCreateFramesFromEmbeddedPreambles()
    {
        var inner=Frame(0,2,1,[]); var outer=Frame(4,2,1,inner.Concat(Request).ToArray()); outer[^1]^=1;
        var frames=Parse(outer.Concat(Response).ToArray());
        Assert.Equal(2,frames.Count); Assert.Equal(outer,frames[0].Bytes.ToArray());
        Assert.Equal(ProtocolCrcStatus.Valid,frames[0].HeaderCrc); Assert.Equal(ProtocolCrcStatus.Invalid,frames[0].DataCrc);
        Assert.Equal(ProtocolFrameStatus.CrcError,frames[0].Status); Assert.Equal(Response,frames[1].Bytes.ToArray());
    }
    [Fact]
    public void HeaderCrcErrorQuarantinesPayloadAndRecoversAtBoundary()
    {
        var bad=(byte[])Response.Clone(); bad[7]^=1;
        var parser=new BacnetMstpFramer(ProtocolDirection.Rx); var result=new List<BacnetMstpFrame>();
        parser.Feed(bad.AsSpan(0,8),DateTimeOffset.Now,result.Add); Assert.Empty(result);
        parser.Feed(bad.AsSpan(8),DateTimeOffset.Now,result.Add,qualifiedBoundaryAtEnd:true);
        var first=Assert.Single(result); Assert.Equal(ProtocolCrcStatus.Invalid,first.HeaderCrc);
        Assert.Equal(ProtocolCrcStatus.Unavailable,first.DataCrc); Assert.Null(first.Source); Assert.Null(first.FrameType);
        Assert.Equal(bad,first.Bytes.ToArray());
        parser.Feed(Response,DateTimeOffset.Now,result.Add); Assert.Equal(2,result.Count);
        Assert.Equal(ProtocolCrcStatus.Valid,result[1].CrcStatus);
        Assert.Equal(2,Parse(bad.Concat(Response).ToArray()).Count);
    }
    [Fact]
    public void BadHeaderWithOptionalPadRecoversNextFrameAcrossEveryChunkSplit()
    {
        var bad=(byte[])Response.Clone();bad[7]^=1;
        var bytes=bad.Concat(new byte[]{0xFF}).Concat(Response).ToArray();
        for(int split=0;split<=bytes.Length;split++)
        {
            var parser=new BacnetMstpFramer(ProtocolDirection.Rx);var frames=new List<BacnetMstpFrame>();
            parser.Feed(bytes.AsSpan(0,split),DateTimeOffset.Now,frames.Add);
            parser.Feed(bytes.AsSpan(split),DateTimeOffset.Now,frames.Add);parser.Flush(frames.Add);
            Assert.Equal(2,frames.Count);Assert.Equal(bad,frames[0].Bytes.ToArray());
            Assert.Equal(ProtocolCrcStatus.Invalid,frames[0].HeaderCrc);
            Assert.Equal(Response,frames[1].Bytes.ToArray());Assert.Equal(ProtocolCrcStatus.Valid,frames[1].CrcStatus);
            Assert.Equal(0,parser.PendingBytes);
        }
    }
    [Fact]
    public void ZeroDataControlFramesDoNotRequireDataCrc_AndOneOptionalPadIsIgnored()
    {
        var token=Frame(0,2,1,[]);
        var frames=Parse(token.Concat(new byte[]{0xFF}).Concat(Response).Concat(new byte[]{0xFF}).ToArray());
        Assert.Equal(2,frames.Count); Assert.All(frames,f=>Assert.Equal(ProtocolCrcStatus.Valid,f.CrcStatus));
        Assert.Equal(8,frames[0].Length); Assert.Equal(ProtocolCrcStatus.Unavailable,frames[0].DataCrc);
    }
    [Fact]
    public void MaxClassicPayloadIsBoundedAndNotSplitAtInnerFrames()
    {
        var max=Frame(4,2,1,Enumerable.Range(0,501).Select(i=>(byte)i).ToArray());
        Assert.Equal(511,max.Length); var frame=Assert.Single(Parse(max));
        Assert.Equal(ProtocolCrcStatus.Valid,frame.CrcStatus); Assert.Equal(max,frame.Bytes.ToArray());
    }
    [Fact]
    public void TruncatedFrameIsIncompleteAndDoesNotAcquirePayloadMeaning()
    {
        for(int length=1;length<Response.Length;length++)
        {
            var frame=Assert.Single(Parse(Response[..length]));
            Assert.Equal(ProtocolFrameStatus.Incomplete,frame.Status); Assert.NotEqual(ProtocolCrcStatus.Valid,frame.CrcStatus);
        }
    }
    [Fact]
    public void InvalidControlLayoutsAndVendorTypesKeepCrcSeparateFromDecoding()
    {
        foreach(var bytes in new[]{Frame(0,2,1,"ABC"u8.ToArray()),Frame(5,2,1,[]),Frame(128,2,1,"ABC"u8.ToArray()),Frame(4,2,255,[])})
        {
            var frame=Assert.Single(Parse(bytes)); Assert.Equal(ProtocolFrameStatus.Undecoded,frame.Status);
            Assert.Equal(ProtocolCrcStatus.Valid,frame.CrcStatus);
        }
    }
    [Fact]
    public void UnsupportedExtendedCrcIsNeverReportedAsValidOrBadClassicCrc()
    {
        var frame=new byte[15]; frame[0]=0x55;frame[1]=0xFF;frame[2]=32;frame[3]=2;frame[4]=1;frame[6]=5;
        frame[7]=BacnetMstpCrc.Header(frame.AsSpan(2,5));
        var parsed=Assert.Single(Parse(frame));
        Assert.Equal(ProtocolFrameStatus.Undecoded,parsed.Status); Assert.Equal(ProtocolCrcStatus.Unavailable,parsed.CrcStatus);
    }
    [Fact]
    public void UntrustedLengthNoiseAndResetCannotGrowAssemblyOrJoinSessions()
    {
        var invalid=new byte[10000]; Request.CopyTo(invalid,0); invalid[5]=invalid[6]=255;
        var frames=Parse(invalid); Assert.NotEmpty(frames);
        Assert.All(frames,f=>Assert.InRange(f.Length,1,BacnetMstpFrame.MaximumBytes));
        var parser=new BacnetMstpFramer(ProtocolDirection.Rx); var result=new List<BacnetMstpFrame>();
        parser.Feed(Request.AsSpan(0,5),DateTimeOffset.Now,result.Add); parser.Reset();
        parser.Feed(Response,DateTimeOffset.Now,result.Add); Assert.Equal(Response,Assert.Single(result).Bytes.ToArray());
    }
    internal static async Task<ProtocolAnalysisSnapshot> Wait(IProtocolAnalyzer analyzer,Func<ProtocolAnalysisSnapshot,bool> predicate)
    {
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while(true) {var s=analyzer.Snapshot();if(predicate(s))return s;await Task.Delay(5,stop.Token);}
    }
    [Fact]
    public async Task CumulativeCrcCountersRetentionClearAndResetAreIndependent()
    {
        await using var analyzer=new BacnetMstpAnalyzer(historyCapacity:2);analyzer.SetEnabled(true);
        var badHeader=(byte[])Response.Clone();badHeader[7]^=1;
        var badData=(byte[])Response.Clone();badData[^1]^=1;
        analyzer.TryEnqueue(Response.Concat(badHeader).Concat(badData).Concat(Response).ToArray(),ProtocolDirection.Rx,1,DateTimeOffset.Now);
        var s=await Wait(analyzer,s=>s.TotalFrames==4);
        Assert.Equal(2,s.RetainedCount);Assert.Equal(2,s.EvictedFrames);Assert.Equal(2,s.Statistics.CrcValid);
        Assert.Equal(2,s.Statistics.CrcErrors);Assert.Equal(1,s.Statistics.HeaderCrcErrors);Assert.Equal(1,s.Statistics.DataCrcErrors);
        Assert.Equal(0,s.Statistics.Incomplete); Assert.Null(analyzer.Snapshot(s.Version).Results);
        analyzer.Clear(); Assert.Equal(4,analyzer.Snapshot().TotalFrames); Assert.Empty(analyzer.Snapshot().Results!);
        analyzer.ResetStatistics(); Assert.Equal(0,analyzer.Snapshot().Statistics.HeaderCrcErrors);
        Assert.Equal(0,analyzer.Snapshot().TotalFrames);
    }
    [Fact]
    public async Task DefaultHistoryRetainsTenThousandAndCountsAllFrames()
    {
        await using var analyzer=new BacnetMstpAnalyzer();analyzer.SetEnabled(true);
        Assert.True(analyzer.TryEnqueue(Enumerable.Range(0,10005).SelectMany(_=>Response).ToArray(),ProtocolDirection.Rx,1,DateTimeOffset.Now));
        var s=await Wait(analyzer,s=>s.TotalFrames==10005);
        Assert.Equal(10000,s.RetainedCount);Assert.Equal(5,s.EvictedFrames);Assert.Equal(6,s.Results![0].Id);
        Assert.Equal(10005,s.Statistics.CrcValid);Assert.Equal(0,s.DroppedChunks);
        Assert.InRange(s.RetainedBytes,1,BacnetMstpAnalyzer.DefaultHistoryByteLimit);
    }
    [Fact]
    public async Task QueueRejectionAndSessionBoundariesCannotCreateValidJoinedFrames()
    {
        await using(var limited=new BacnetMstpAnalyzer(queueByteLimit:8))
        {
            limited.SetEnabled(true); Assert.False(limited.TryEnqueue(Request,ProtocolDirection.Rx,1,DateTimeOffset.Now));
            Assert.Equal(1,limited.Snapshot().DroppedChunks);Assert.Equal(Request.Length,limited.Snapshot().DroppedBytes);
        }
        await using var analyzer=new BacnetMstpAnalyzer();analyzer.SetEnabled(true);
        analyzer.TryEnqueue(Request.AsMemory(0,8),ProtocolDirection.Rx,1,DateTimeOffset.Now);
        await Wait(analyzer,s=>s.PendingBytes==0);
        analyzer.TryEnqueue(Response,ProtocolDirection.Rx,2,DateTimeOffset.Now);
        var state=await Wait(analyzer,s=>s.TotalFrames>=1);
        Assert.Equal(1,state.Statistics.CrcValid);Assert.Equal(Response,Assert.Single(state.Results!).Bytes.ToArray());
    }
    [Fact]
    public async Task ProtocolSelectionInvalidatesPartialInputAndVersions_AndModbusStillWorks()
    {
        await using var analyzer=new ProtocolAnalyzer(); analyzer.SetEnabled(true);
        analyzer.TryEnqueue(Convert.FromHexString("010300000002C40B"),ProtocolDirection.Tx,1,DateTimeOffset.Now);
        var old=await Wait(analyzer,s=>s.TotalFrames==1); Assert.NotNull(old.Frames);
        analyzer.SelectProtocol(ProtocolKind.BacnetMstp);
        var empty=analyzer.Snapshot(old.Version);Assert.NotEqual(old.Version,empty.Version);Assert.NotNull(empty.Results);Assert.Empty(empty.Results!);
        analyzer.TryEnqueue(Response,ProtocolDirection.Rx,1,DateTimeOffset.Now);
        Assert.IsType<BacnetMstpFrame>(Assert.Single((await Wait(analyzer,s=>s.TotalFrames==1)).Results!));
        analyzer.SelectProtocol(ProtocolKind.ModbusRtu);
        analyzer.TryEnqueue(Convert.FromHexString("010300000002C40B"),ProtocolDirection.Tx,1,DateTimeOffset.Now);
        Assert.IsType<ModbusRtuFrame>(Assert.Single((await Wait(analyzer,s=>s.TotalFrames==1)).Results!));
    }
}
