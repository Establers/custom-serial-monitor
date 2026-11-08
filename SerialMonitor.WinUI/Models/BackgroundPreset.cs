namespace SerialMonitor.WinUI.Models;

public enum BackgroundFitMode { Cover, Contain }

// Row-major order matches the 3 x 3 position picker.
public enum BackgroundAnchor { TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight }

public sealed class BackgroundPreset
{
    public const double DefaultXtermOpacity = 0.15;

    public string ImagePath { get; set; } = string.Empty;

    public double Opacity { get; set; } = 0.25;

    public double XtermOpacity { get; set; } = DefaultXtermOpacity;

    public BackgroundFitMode FitMode { get; set; } = BackgroundFitMode.Cover;

    public BackgroundAnchor Anchor { get; set; } = BackgroundAnchor.Center;

    public BackgroundPreset Clone() => new()
    {
        ImagePath = ImagePath, Opacity = Opacity, XtermOpacity = XtermOpacity,
        FitMode = FitMode, Anchor = Anchor
    };
}
