namespace SerialMonitor.WinUI.Models;

public readonly record struct TextMatchRange(int Start, int Length);

public sealed class EventTextSegment
{
    public EventTextSegment(
        string text,
        bool isMatch,
        string? foregroundColor = null,
        string? backgroundColor = null)
    {
        Text = text;
        IsMatch = isMatch;
        ForegroundColor = foregroundColor;
        BackgroundColor = backgroundColor;
    }

    public string Text { get; }

    public bool IsMatch { get; }

    public string? ForegroundColor { get; }

    public string? BackgroundColor { get; }
}
