using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class InspectorResizeInteractionPolicyTests
{
    [Theory]
    [InlineData(0, 0, 3, 3, false)]
    [InlineData(0, 0, 3.1, 0, true)]
    [InlineData(0, 0, 0, -4, true)]
    public void HasMoved_UsesConfiguredThreshold(
        double startX,
        double startY,
        double currentX,
        double currentY,
        bool expected)
    {
        Assert.Equal(
            expected,
            InspectorResizeInteractionPolicy.HasMoved(
                startX,
                startY,
                currentX,
                currentY,
                threshold: 3));
    }

    [Fact]
    public void IsDoubleClick_AcceptsSamePointerNearPreviousClickWithinSystemInterval()
    {
        Assert.True(InspectorResizeInteractionPolicy.IsDoubleClick(
            elapsed: TimeSpan.FromMilliseconds(300),
            maximumInterval: TimeSpan.FromMilliseconds(500),
            previousPointerId: 1,
            currentPointerId: 1,
            previousX: 100,
            previousY: 200,
            currentX: 104,
            currentY: 204,
            maximumDistance: 8));
    }

    [Theory]
    [InlineData(600, 1, 1, 100, 200, 104, 204)]
    [InlineData(300, 1, 2, 100, 200, 104, 204)]
    [InlineData(300, 1, 1, 100, 200, 109, 200)]
    public void IsDoubleClick_RejectsLateDifferentPointerOrDistantClick(
        double elapsedMilliseconds,
        uint previousPointerId,
        uint currentPointerId,
        double previousX,
        double previousY,
        double currentX,
        double currentY)
    {
        Assert.False(InspectorResizeInteractionPolicy.IsDoubleClick(
            elapsed: TimeSpan.FromMilliseconds(elapsedMilliseconds),
            maximumInterval: TimeSpan.FromMilliseconds(500),
            previousPointerId,
            currentPointerId,
            previousX,
            previousY,
            currentX,
            currentY,
            maximumDistance: 8));
    }
}
