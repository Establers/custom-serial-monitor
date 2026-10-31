namespace SerialMonitor.WinUI.Models;

// Context references never keep the original payload alive. UI code reads them
// through the background evidence reader, rather than enumerating on the UI.
public interface IArchivedLogLine
{
    LogLine Read();
    Task<LogLine> ReadAsync(CancellationToken cancellationToken);
}
