namespace SerialMonitor.WinUI.Models;

public sealed class InstanceInputHistory
{
    public List<CommandHistoryEntry> CommandHistory { get; set; } = [];

    // Oldest to newest, matching search Up/Down navigation.
    public List<string> SearchHistory { get; set; } = [];

    public string LastSearchText { get; set; } = string.Empty;
}
