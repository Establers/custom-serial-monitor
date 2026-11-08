using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Tests;

public sealed class BackgroundImageLayoutTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Cover_KeepsChosenEdgeAndAspectRatioWhenWindowChangesShape(int anchor)
    {
        var portrait = BackgroundImageLayout.Calculate(600, 1080, 1920, 1080,
            BackgroundFitMode.Cover, (BackgroundAnchor)anchor)!.Value;
        Assert.Equal(1920, portrait.Width);
        Assert.Equal(1080, portrait.Height);
        Assert.Equal(-1320 * (anchor % 3) / 2.0, portrait.Left);
        Assert.Equal(0, portrait.Top);

        var landscape = BackgroundImageLayout.Calculate(1920, 600, 1920, 1080,
            BackgroundFitMode.Cover, (BackgroundAnchor)anchor)!.Value;
        Assert.Equal(1920, landscape.Width);
        Assert.Equal(1080, landscape.Height);
        Assert.Equal(0, landscape.Left);
        Assert.Equal(-480 * (anchor / 3) / 2.0, landscape.Top);
    }

    [Theory]
    [InlineData(600, 1080, 600, 337.5, 0, 371.25)]
    [InlineData(1920, 600, 1066.6666666667, 600, 426.6666666667, 0)]
    public void Contain_ShowsEntireImageCenteredRegardlessOfSavedAnchor(double viewportWidth, double viewportHeight,
        double width, double height, double left, double top)
    {
        foreach (var anchor in Enum.GetValues<BackgroundAnchor>())
        {
            var layout = BackgroundImageLayout.Calculate(viewportWidth, viewportHeight, 1920, 1080,
                BackgroundFitMode.Contain, anchor)!.Value;
            Assert.Equal(width, layout.Width, 6);
            Assert.Equal(height, layout.Height, 6);
            Assert.Equal(left, layout.Left, 6);
            Assert.Equal(top, layout.Top, 6);
        }
    }

    [Theory]
    [InlineData(0, 500, 1920, 1080)]
    [InlineData(500, 0, 1920, 1080)]
    [InlineData(500, 500, 0, 1080)]
    [InlineData(500, 500, 1920, 0)]
    [InlineData(double.NaN, 500, 1920, 1080)]
    [InlineData(500, double.PositiveInfinity, 1920, 1080)]
    public void UnavailableImageOrViewport_WaitsForValidDimensions(double width, double height, double imageWidth, double imageHeight)
    {
        Assert.Null(BackgroundImageLayout.Calculate(width, height, imageWidth, imageHeight,
            BackgroundFitMode.Cover, BackgroundAnchor.Center));
    }
}
