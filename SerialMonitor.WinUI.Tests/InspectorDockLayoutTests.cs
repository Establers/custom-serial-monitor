using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class InspectorDockLayoutTests
{
    [Theory]
    [InlineData(10, 300, "Left")]
    [InlineData(990, 300, "Right")]
    [InlineData(500, 590, "Bottom")]
    [InlineData(20, 599, "Bottom")]
    [InlineData(999, 560, "Right")]
    public void EdgesAndCornersChooseTheClosestDock(double x, double y, string expected)
        => Assert.Equal(expected, InspectorDockLayout.HitTest(x, y, 1000, 600)?.ToString());

    [Theory]
    [InlineData(500, 300, 1000, 600)]
    [InlineData(500, 10, 1000, 600)]
    [InlineData(-1, 300, 1000, 600)]
    [InlineData(1001, 300, 1000, 600)]
    [InlineData(500, 601, 1000, 600)]
    [InlineData(double.NaN, 300, 1000, 600)]
    [InlineData(0, 0, 0, 0)]
    public void CenterOutsideAndInvalidCoordinatesCancel(double x, double y, double width, double height)
        => Assert.Null(InspectorDockLayout.HitTest(x, y, width, height));

    [Fact]
    public void SideResizeRespectsOppositeEdgesAndKeepsLogSpace()
    {
        var maximum = InspectorDockLayout.MaximumExtent(InspectorDockPosition.Left, 800, 600, 80);
        Assert.Equal(514, maximum);
        Assert.Equal(450, InspectorDockLayout.ResizeExtent(InspectorDockPosition.Left, 400, 50, 0, maximum));
        Assert.Equal(350, InspectorDockLayout.ResizeExtent(InspectorDockPosition.Right, 400, 50, 0, maximum));
        Assert.Equal(maximum, InspectorDockLayout.ResizeExtent(InspectorDockPosition.Left, 400, 5000, 0, maximum));
        Assert.Equal(0, InspectorDockLayout.ResizeExtent(InspectorDockPosition.Right, 400, 5000, 0, maximum));
    }

    [Fact]
    public void SidePanelCanCollapseAndRestoreWithoutBecomingAnUnusableStrip()
    {
        Assert.Equal(40, InspectorDockLayout.ResizeExtent(InspectorDockPosition.Left, 400, -360, 0, 514));
        Assert.Equal(320, InspectorDockLayout.ResizeExtent(InspectorDockPosition.Left, 0, 60, 0, 514));
        Assert.Equal(114, InspectorDockLayout.ResizeExtent(InspectorDockPosition.Left, 0, 60, 0, 114));
    }

    [Fact]
    public void BottomResizeLeavesCommandsAndTerminalVisible()
    {
        var maximum = InspectorDockLayout.MaximumExtent(InspectorDockPosition.Bottom, 800, 600, 80);
        Assert.Equal(334, maximum);
        Assert.Equal(250, InspectorDockLayout.ResizeExtent(InspectorDockPosition.Bottom, 200, 100, -50, maximum));
        Assert.Equal(0, InspectorDockLayout.MaximumExtent(InspectorDockPosition.Bottom, 800, 100, 80));
    }
}
