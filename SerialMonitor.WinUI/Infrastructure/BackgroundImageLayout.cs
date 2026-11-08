using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

// Calculate the image rectangle once. Both XAML and xterm use this rectangle,
// preserving the aspect ratio and the same crop at every window size.
public readonly record struct BackgroundImageLayout(double Left, double Top, double Width, double Height)
{
    public static BackgroundImageLayout? Calculate(double viewportWidth, double viewportHeight,
        double imageWidth, double imageHeight, BackgroundAnchor anchor)
    {
        if (!double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight) ||
            !double.IsFinite(imageWidth) || !double.IsFinite(imageHeight) ||
            viewportWidth <= 0 || viewportHeight <= 0 || imageWidth <= 0 || imageHeight <= 0) return null;

        var scale = Math.Max(viewportWidth / imageWidth, viewportHeight / imageHeight);
        var width = imageWidth * scale;
        var height = imageHeight * scale;
        var position = !Enum.IsDefined(anchor) ? (int)BackgroundAnchor.Center : (int)anchor;
        return new((viewportWidth - width) * (position % 3) / 2,
            (viewportHeight - height) * (position / 3) / 2, width, height);
    }
}
