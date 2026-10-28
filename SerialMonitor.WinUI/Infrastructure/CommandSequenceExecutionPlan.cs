using System.Collections.ObjectModel;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

// Freeze only executable steps and retain the editor's original row numbers.
internal static class CommandSequenceExecutionPlan
{
    public static CommandSequence Create(CommandSequence source)
    {
        var steps = source.Steps.Select((step, index) => (step, number: index + 1))
            .Where(item => item.step.IsEnabled)
            .Select(item =>
            {
                var copy = CloneStep(item.step);
                copy.StepNumber = item.number;
                return copy;
            });
        return new CommandSequence
        {
            Name = source.Name,
            RepeatCount = source.RepeatCount,
            Steps = new ObservableCollection<CommandSequenceStep>(steps)
        };
    }

    public static CommandSequenceStep CloneStep(CommandSequenceStep step) => new()
    {
        IsEnabled = step.IsEnabled,
        Name = step.Name,
        CommandText = step.CommandText,
        LineEndingMode = step.LineEndingMode,
        DelayAfterMs = step.DelayAfterMs,
        Comment = step.Comment,
        TxMode = step.TxMode,
        CaptureMs = step.CaptureMs,
        Expect = step.Expect is null ? null : step.Expect with { },
        StepNumber = step.StepNumber
    };
}
