using System.Collections.ObjectModel;

namespace SerialMonitor.WinUI.Models;

public sealed class CommandSequence
{
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public ObservableCollection<CommandSequenceStep> Steps { get; set; } = new();
}

