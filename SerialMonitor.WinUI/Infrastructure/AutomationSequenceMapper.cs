using System.Collections.ObjectModel;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

// The GUI keeps its existing profile format. Automation snapshots resolve all
// inherited TX settings once, before execution, so a later selection cannot
// change the bytes in a running sequence.
internal static class AutomationSequenceMapper
{
    public static AutomationSequenceDefinition Snapshot(CommandSequence sequence, TxSendMode mode, TxLineEndingMode ending) =>
        new(sequence.Name, sequence.RepeatCount, sequence.Steps.Where(step => step.IsEnabled).Select(step => new AutomationSequenceStep(
            step.Name, step.CommandText, (step.TxMode ?? mode) == TxSendMode.Hex ? "hex" : "text",
            (step.LineEndingMode ?? ending).ToString().ToLowerInvariant(), step.CaptureMs, step.DelayAfterMs,
            step.Expect)).ToArray());

    public static CommandSequence Create(AutomationSequenceDefinition definition) => new()
    {
        Name = definition.Name,
        RepeatCount = definition.RepeatCount,
        Steps = new ObservableCollection<CommandSequenceStep>(definition.Steps.Select((step, index) => new CommandSequenceStep
        {
            Name = step.Name,
            CommandText = step.Command,
            TxMode = step.TxMode == "hex" ? TxSendMode.Hex : TxSendMode.Terminal,
            LineEndingMode = Enum.Parse<TxLineEndingMode>(step.LineEnding, ignoreCase: true),
            CaptureMs = step.CaptureMs,
            DelayAfterMs = step.DelayAfterMs,
            Expect = step.Expect,
            StepNumber = index + 1
        }))
    };
}
