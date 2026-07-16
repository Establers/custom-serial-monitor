namespace SerialMonitor.WinUI.Models;

public sealed class EventContextSettings
{
    public const int FixedLineCount = 5;

    public int BeforeContextLines { get; set; } = FixedLineCount;

    public int AfterContextLines { get; set; } = FixedLineCount;

    public EventContextSettings Clone()
    {
        return new EventContextSettings
        {
            BeforeContextLines = BeforeContextLines,
            AfterContextLines = AfterContextLines
        };
    }
}
