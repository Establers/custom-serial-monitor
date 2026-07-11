using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

public sealed class LogFormatter
{
    public string Format(LogLine line)
    {
        return line.Formatted;
    }
}
