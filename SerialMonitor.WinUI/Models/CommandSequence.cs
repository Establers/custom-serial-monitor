using System.Collections.ObjectModel;

namespace SerialMonitor.WinUI.Models;

public sealed class CommandSequence
{
    public const int MinRepeatCount = 1;

    public const int MaxRepeatCount = 9_999;

    public string Name { get; set; } = string.Empty;

    public int RepeatCount { get; set; } = 1;

    public ObservableCollection<CommandSequenceStep> Steps { get; set; } = new();
}
