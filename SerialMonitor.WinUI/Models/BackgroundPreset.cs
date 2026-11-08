namespace SerialMonitor.WinUI.Models;

public sealed class BackgroundPreset
{
    public const double DefaultXtermOpacity = 0.15;

    public string ImagePath { get; set; } = string.Empty;

    public double Opacity { get; set; } = 0.25;

    public double XtermOpacity { get; set; } = DefaultXtermOpacity;

    public BackgroundPreset Clone() => new() { ImagePath = ImagePath, Opacity = Opacity, XtermOpacity = XtermOpacity };
}
