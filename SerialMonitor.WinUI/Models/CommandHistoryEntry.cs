using System.Globalization;

namespace SerialMonitor.WinUI.Models;

public sealed class CommandHistoryEntry
{
    public string CommandText { get; set; } = string.Empty;

    public DateTimeOffset LastSentTime { get; set; } = DateTimeOffset.Now;

    public string LastSentTimeText => LastSentTime.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

}
