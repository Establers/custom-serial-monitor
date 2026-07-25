namespace SerialMonitor.WinUI.Infrastructure;

internal static class InspectorResizeInteractionPolicy
{
    public static bool HasMoved(
        double startX,
        double startY,
        double currentX,
        double currentY,
        double threshold)
    {
        return Math.Abs(currentX - startX) > threshold ||
               Math.Abs(currentY - startY) > threshold;
    }

    public static bool IsDoubleClick(
        TimeSpan elapsed,
        TimeSpan maximumInterval,
        uint previousPointerId,
        uint currentPointerId,
        double previousX,
        double previousY,
        double currentX,
        double currentY,
        double maximumDistance)
    {
        if (elapsed > maximumInterval || previousPointerId != currentPointerId)
        {
            return false;
        }

        var deltaX = currentX - previousX;
        var deltaY = currentY - previousY;
        return (deltaX * deltaX) + (deltaY * deltaY) <= maximumDistance * maximumDistance;
    }
}
