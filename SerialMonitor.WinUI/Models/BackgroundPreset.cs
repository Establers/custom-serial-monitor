namespace SerialMonitor.WinUI.Models;

public sealed class BackgroundPreset
{
    public string ImagePath { get; set; } = string.Empty;

    public double Opacity { get; set; } = 0.25;

    public BackgroundPreset Clone() => new() { ImagePath = ImagePath, Opacity = Opacity };
}
