using System.Diagnostics;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class BridgeDeviceChunkGrouperTests
{
    [Fact]
    public void AdjacentChunksInsideHexTimeout_AreEmittedAsOneWriteGroup()
    {
        var grouper = new BridgeDeviceChunkGrouper();
        grouper.Append(CreateChunk(new byte[] { 1, 2 }, 0, 40));
        grouper.Append(CreateChunk(new byte[] { 3 }, 5, 40));
        grouper.Append(CreateChunk(new byte[] { 4, 5 }, 39, 40));

        Assert.Null(grouper.GetImmediateFlushReason(ToTimestamp(39)));
        Assert.Equal(
            BridgeGroupFlushReason.IdleTimeout,
            grouper.GetFlushReasonBeforeAppend(CreateChunk(new byte[] { 6 }, 79, 40)));

        var grouped = grouper.BuildAndReset(BridgeGroupFlushReason.IdleTimeout);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, grouped.Bytes);
        Assert.Equal(ToTimestamp(39), grouped.ReceivedTimestamp);
        Assert.False(grouped.EndsAtNativeIdleBoundary);
        Assert.Equal(0, grouped.AppliedIdleTimeoutMs);
        Assert.Equal(40, grouped.ReplayIdleGapMs);
        Assert.Equal(40, grouped.DeviceToVirtualGroupTimeoutMs);
        Assert.False(grouper.HasData);
    }

    [Fact]
    public void ExactHexTimeout_StartsTheNextGroupLikeTheLogPipeline()
    {
        var grouper = new BridgeDeviceChunkGrouper();
        grouper.Append(CreateChunk(new byte[] { 1 }, 10, 15));

        Assert.Equal(
            BridgeGroupFlushReason.IdleTimeout,
            grouper.GetFlushReasonBeforeAppend(CreateChunk(new byte[] { 2 }, 25, 15)));
    }

    [Fact]
    public void NativeIdleBoundary_ClosesGroupWithoutAnotherTimerWait()
    {
        var grouper = new BridgeDeviceChunkGrouper();
        grouper.Append(CreateChunk(new byte[] { 1 }, 10, 40, endsAtNativeIdleBoundary: true));

        Assert.Equal(
            BridgeGroupFlushReason.NativeIdleBoundary,
            grouper.GetImmediateFlushReason(ToTimestamp(10)));
        var grouped = grouper.BuildAndReset(BridgeGroupFlushReason.NativeIdleBoundary);
        Assert.True(grouped.EndsAtNativeIdleBoundary);
        Assert.Equal(40, grouped.AppliedIdleTimeoutMs);
        Assert.Equal(40, grouped.ReplayIdleGapMs);
    }

    [Fact]
    public void GroupSizeIsBoundedForContinuousStreams()
    {
        var grouper = new BridgeDeviceChunkGrouper();
        grouper.Append(CreateChunk(
            new byte[BridgeDeviceChunkGrouper.MaxGroupedBytes - 1],
            0,
            40));
        grouper.Append(CreateChunk(new byte[] { 1 }, 1, 40));

        Assert.Equal(
            BridgeGroupFlushReason.MaximumSize,
            grouper.GetImmediateFlushReason(ToTimestamp(1)));
        Assert.Equal(
            BridgeGroupFlushReason.MaximumSize,
            grouper.GetFlushReasonBeforeAppend(CreateChunk(new byte[] { 2 }, 2, 40)));
        Assert.Equal(
            BridgeDeviceChunkGrouper.MaxGroupedBytes,
            grouper.BuildAndReset(BridgeGroupFlushReason.MaximumSize).Bytes.Length);
    }

    [Fact]
    public void ContinuousStream_FlushesAtMaximumLatencyBeforeSizeLimit()
    {
        var grouper = new BridgeDeviceChunkGrouper();
        grouper.Append(CreateChunk(new byte[] { 1 }, 0, groupTimeoutMs: 5_000));
        grouper.Append(CreateChunk(new byte[] { 2 }, 99, groupTimeoutMs: 5_000));

        Assert.Equal(
            BridgeGroupFlushReason.MaximumLatency,
            grouper.GetFlushReasonBeforeAppend(
                CreateChunk(new byte[] { 3 }, 100, groupTimeoutMs: 5_000)));
        var grouped = grouper.BuildAndReset(BridgeGroupFlushReason.MaximumLatency);
        Assert.Equal(new byte[] { 1, 2 }, grouped.Bytes);
        Assert.False(grouped.EndsAtNativeIdleBoundary);
        Assert.Equal(0, grouped.ReplayIdleGapMs);
    }

    [Fact]
    public void QuietContinuousGroup_WaitsOnlyUntilMaximumLatency()
    {
        var grouper = new BridgeDeviceChunkGrouper();
        grouper.Append(CreateChunk(new byte[] { 1 }, 0, groupTimeoutMs: 5_000));

        var wait = grouper.GetNextWait(ToTimestamp(25));

        Assert.Equal(BridgeGroupFlushReason.MaximumLatency, wait.TimeoutReason);
        Assert.InRange(wait.Delay.TotalMilliseconds, 74.9, 75.1);
    }

    [Fact]
    public void ConfigurationChange_DoesNotCreateReplayIdleGap()
    {
        var grouper = new BridgeDeviceChunkGrouper();
        grouper.Append(CreateChunk(new byte[] { 1 }, 0, groupTimeoutMs: 5_000));

        Assert.Equal(
            BridgeGroupFlushReason.ConfigurationChanged,
            grouper.GetFlushReasonBeforeAppend(
                CreateChunk(new byte[] { 2 }, 1, groupTimeoutMs: 0)));
        var grouped = grouper.BuildAndReset(BridgeGroupFlushReason.ConfigurationChanged);
        Assert.False(grouped.EndsAtNativeIdleBoundary);
        Assert.Equal(0, grouped.AppliedIdleTimeoutMs);
        Assert.Equal(0, grouped.ReplayIdleGapMs);
    }

    private static BridgeRxChunk CreateChunk(
        byte[] bytes,
        double milliseconds,
        int groupTimeoutMs,
        bool endsAtNativeIdleBoundary = false) =>
        new(
            bytes,
            ToTimestamp(milliseconds),
            endsAtNativeIdleBoundary,
            AppliedIdleTimeoutMs: 0)
        {
            DeviceToVirtualGroupTimeoutMs = groupTimeoutMs
        };

    private static long ToTimestamp(double milliseconds) =>
        (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000d);
}
