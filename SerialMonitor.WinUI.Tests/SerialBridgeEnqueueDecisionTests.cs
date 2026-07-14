using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialBridgeEnqueueDecisionTests
{
    [Fact]
    public void StoppedBridge_ReturnsStoppedWithoutTouchingQueue()
    {
        var queue = new BoundedByteQueue<byte[]>(maxChunks: 1, maxBytes: 16);

        var result = SerialBridgeService.TryEnqueueVirtualToDevice(
            bridgeRunning: false,
            queue,
            new byte[] { 1, 2, 3 });

        Assert.Equal(BridgeQueueEnqueueResult.BridgeStopped, result);
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.ByteCount);
    }

    [Fact]
    public void RunningBridge_WithCapacity_EnqueuesNormally()
    {
        var queue = new BoundedByteQueue<byte[]>(maxChunks: 1, maxBytes: 16);

        var result = SerialBridgeService.TryEnqueueVirtualToDevice(
            bridgeRunning: true,
            queue,
            new byte[] { 1, 2, 3 });

        Assert.Equal(BridgeQueueEnqueueResult.Enqueued, result);
        Assert.Equal(1, queue.Count);
        Assert.Equal(3, queue.ByteCount);
    }

    [Fact]
    public void RunningBridge_WithFullQueue_ReturnsActualOverflow()
    {
        var queue = new BoundedByteQueue<byte[]>(maxChunks: 1, maxBytes: 16);
        Assert.True(queue.TryEnqueue(new byte[] { 0xAA }, 1));

        var result = SerialBridgeService.TryEnqueueVirtualToDevice(
            bridgeRunning: true,
            queue,
            new byte[] { 1, 2, 3 });

        Assert.Equal(BridgeQueueEnqueueResult.Overflow, result);
        Assert.Equal(1, queue.Count);
        Assert.Equal(1, queue.ByteCount);
    }

    [Fact]
    public void StopRace_WinsOverAFullQueue_AndIsNotClassifiedAsOverflow()
    {
        var queue = new BoundedByteQueue<byte[]>(maxChunks: 1, maxBytes: 16);
        Assert.True(queue.TryEnqueue(new byte[] { 0xAA }, 1));

        var result = SerialBridgeService.TryEnqueueVirtualToDevice(
            bridgeRunning: false,
            queue,
            new byte[] { 1, 2, 3 });

        Assert.Equal(BridgeQueueEnqueueResult.BridgeStopped, result);
        Assert.Equal(1, queue.Count);
        Assert.Equal(1, queue.ByteCount);
    }
}
