using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class NativeReadBoundaryTrackerTests
{
    [Fact]
    public void MultipleNativeCompletions_AccumulatedBeforeConsumerRuns_KeepEveryBoundary()
    {
        var tracker = new NativeReadBoundaryTracker(bufferCapacity: 1024 * 1024);

        var packetA = tracker.ObserveCompletion(
            availableByteCount: 11,
            completedTimestamp: 100,
            usesNativeIdleTimeout: true);
        var packetB = tracker.ObserveCompletion(
            availableByteCount: 22,
            completedTimestamp: 200,
            usesNativeIdleTimeout: true);
        var packetC = tracker.ObserveCompletion(
            availableByteCount: 33,
            completedTimestamp: 300,
            usesNativeIdleTimeout: true);

        Assert.Equal(11, packetA.ByteCount);
        Assert.Equal(11, packetB.ByteCount);
        Assert.Equal(11, packetC.ByteCount);
        Assert.Equal(100, packetA.CompletedTimestamp);
        Assert.Equal(200, packetB.CompletedTimestamp);
        Assert.Equal(300, packetC.CompletedTimestamp);
        Assert.True(packetA.EndsAtNativeIdleBoundary);
        Assert.True(packetB.EndsAtNativeIdleBoundary);
        Assert.True(packetC.EndsAtNativeIdleBoundary);

        tracker.RecordConsumed(packetA.ByteCount);
        tracker.RecordConsumed(packetB.ByteCount);
        tracker.RecordConsumed(packetC.ByteCount);
    }

    [Fact]
    public void CompletionAfterConsumerDrain_UsesCurrentAvailableBytesAsNewBoundary()
    {
        var tracker = new NativeReadBoundaryTracker(bufferCapacity: 1024);
        var first = tracker.ObserveCompletion(13, 100, usesNativeIdleTimeout: true);
        tracker.RecordConsumed(first.ByteCount);

        var second = tracker.ObserveCompletion(7, 200, usesNativeIdleTimeout: true);

        Assert.Equal(7, second.ByteCount);
        Assert.Equal(200, second.CompletedTimestamp);
    }

    [Fact]
    public void CircularBufferEnd_RemainsConservativeInsteadOfClaimingIdle()
    {
        var tracker = new NativeReadBoundaryTracker(bufferCapacity: 32);

        var first = tracker.ObserveCompletion(11, 100, usesNativeIdleTimeout: true);
        var bufferEnd = tracker.ObserveCompletion(32, 200, usesNativeIdleTimeout: true);

        Assert.True(first.EndsAtNativeIdleBoundary);
        Assert.Equal(21, bufferEnd.ByteCount);
        Assert.False(bufferEnd.EndsAtNativeIdleBoundary);
    }

    [Fact]
    public void WrappedBufferFillingToReadStart_RemainsConservative()
    {
        var tracker = new NativeReadBoundaryTracker(bufferCapacity: 32);
        var first = tracker.ObserveCompletion(20, 100, usesNativeIdleTimeout: true);
        tracker.RecordConsumed(10);

        var arrayEnd = tracker.ObserveCompletion(22, 200, usesNativeIdleTimeout: true);
        tracker.RecordConsumed(15);
        var wrappedReadStart = tracker.ObserveCompletion(32, 300, usesNativeIdleTimeout: true);

        Assert.True(first.EndsAtNativeIdleBoundary);
        Assert.Equal(12, arrayEnd.ByteCount);
        Assert.False(arrayEnd.EndsAtNativeIdleBoundary);
        Assert.Equal(25, wrappedReadStart.ByteCount);
        Assert.False(wrappedReadStart.EndsAtNativeIdleBoundary);
    }

    [Fact]
    public void ImmediateTransportCompletion_NeverClaimsNativeIdle()
    {
        var tracker = new NativeReadBoundaryTracker(bufferCapacity: 1024);

        var completion = tracker.ObserveCompletion(17, 100, usesNativeIdleTimeout: false);

        Assert.False(completion.EndsAtNativeIdleBoundary);
    }

    [Fact]
    public void CompletionWithDriverLineError_DoesNotClaimIdleBoundary()
    {
        var tracker = new NativeReadBoundaryTracker(bufferCapacity: 1024);

        var completion = tracker.ObserveCompletion(
            availableByteCount: 11,
            completedTimestamp: 100,
            usesNativeIdleTimeout: true,
            lineErrorObserved: true);

        Assert.Equal(11, completion.ByteCount);
        Assert.False(completion.EndsAtNativeIdleBoundary);
        Assert.True(completion.BoundarySuppressedByLineError);
    }
}
