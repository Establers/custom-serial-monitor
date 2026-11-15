using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Models;

internal sealed record InspectorLayoutState(
    InspectorDockPosition Position = InspectorDockPosition.Bottom,
    double? BottomHeight = null,
    double? SideWidth = null,
    bool IsCollapsed = false)
{
    public InspectorLayoutState Normalize() => this with
    {
        Position = Enum.IsDefined(Position) ? Position : InspectorDockPosition.Bottom,
        BottomHeight = ValidExtent(BottomHeight, 57),
        SideWidth = ValidExtent(SideWidth, InspectorDockLayout.MinimumSideWidth)
    };

    private static double? ValidExtent(double? value, double minimum) =>
        value is { } size && double.IsFinite(size) && size >= minimum && size <= 10_000 ? size : null;
}
