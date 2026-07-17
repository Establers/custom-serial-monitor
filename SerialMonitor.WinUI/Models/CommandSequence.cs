using System.Collections.ObjectModel;

namespace SerialMonitor.WinUI.Models;

public sealed class CommandSequence
{
    public string Name { get; set; } = string.Empty;

    public ObservableCollection<CommandSequenceStep> Steps { get; set; } = new();
}
