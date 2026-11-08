namespace SerialMonitor.WinUI.Models;

// Values are retained for profiles written with the original 3 x 3 picker.
public enum BackgroundAnchor { TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight }

public sealed class BackgroundPreset
{
    public const double DefaultXtermOpacity = 0.15;

    public string ImagePath { get; set; } = string.Empty;

    public double Opacity { get; set; } = 0.25;

    public double XtermOpacity { get; set; } = DefaultXtermOpacity;

    public BackgroundAnchor Anchor { get; set; } = BackgroundAnchor.Center;

    // Keep old corner values readable while the picker offers five positions.
    // Preserve their horizontal preference for narrow windows.
    internal static BackgroundAnchor NormalizeAnchor(BackgroundAnchor anchor) => anchor switch
    {
        BackgroundAnchor.TopLeft or BackgroundAnchor.BottomLeft => BackgroundAnchor.Left,
        BackgroundAnchor.TopRight or BackgroundAnchor.BottomRight => BackgroundAnchor.Right,
        BackgroundAnchor.Top or BackgroundAnchor.Left or BackgroundAnchor.Center or
            BackgroundAnchor.Right or BackgroundAnchor.Bottom => anchor,
        _ => BackgroundAnchor.Center
    };

    public BackgroundPreset Clone() => new()
    {
        ImagePath = ImagePath, Opacity = Opacity, XtermOpacity = XtermOpacity,
        Anchor = Anchor
    };
}
