namespace SerialMonitor.WinUI.Models;

public sealed class EventContextSettings
{
    public int BeforeContextLines { get; set; } = 5;

    public int AfterContextLines { get; set; } = 5;

    public EventContextSettings Clone()
    {
        return new EventContextSettings
        {
            BeforeContextLines = BeforeContextLines,
            AfterContextLines = AfterContextLines
        };
    }
}
