namespace SerialMonitor.WinUI.Infrastructure;

internal enum InspectorDockPosition { Bottom, Left, Right }

// Geometry only: no view creation, serial work, or profile writes during a drag.
internal static class InspectorDockLayout
{
    public const double SplitterSize = 6;
    public const double MinimumLogWidth = 280;
    public const double MinimumSideWidth = 320;

    public static InspectorDockPosition? HitTest(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) ||
            !double.IsFinite(height) || width <= 0 || height <= 0 ||
            x < 0 || x > width || y < 0 || y > height) return null;

        var sideZone = Math.Min(160, width * 0.22);
        var bottomZone = Math.Min(120, height * 0.22);
        var sideDistance = Math.Min(x, width - x) / sideZone;
        var bottomDistance = (height - y) / bottomZone;
        // At corners, choose the closest edge relative to its target zone.
        if (bottomDistance <= 1 && bottomDistance <= sideDistance) return InspectorDockPosition.Bottom;
        if (sideDistance <= 1) return x < width / 2 ? InspectorDockPosition.Left : InspectorDockPosition.Right;
        return null;
    }

    public static double MaximumExtent(InspectorDockPosition position, double width, double height, double commandsHeight)
        => Math.Max(0, position == InspectorDockPosition.Bottom
            ? height - commandsHeight - SplitterSize - 180
            : width - SplitterSize - MinimumLogWidth);

    public static double ResizeExtent(InspectorDockPosition position, double start, double dx, double dy, double maximum)
    {
        var extent = Math.Clamp(start + (position switch
        {
            InspectorDockPosition.Left => dx,
            InspectorDockPosition.Right => -dx,
            _ => -dy
        }), 0, maximum);
        return position != InspectorDockPosition.Bottom && extent > 56
            ? Math.Min(maximum, Math.Max(MinimumSideWidth, extent)) : extent;
    }
}
