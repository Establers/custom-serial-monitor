namespace SerialMonitor.WinUI.Models;

public sealed class EventContextSettings
{
    public int BeforeContextLines { get; set; } = 10;

    public int AfterContextLines { get; set; } = 10;

    public EventContextSettings Clone()
    {
        return new EventContextSettings
        {
            BeforeContextLines = BeforeContextLines,
            AfterContextLines = AfterContextLines
        };
    }
}
