using System.Collections.ObjectModel;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

internal static class CommandSequenceMigration
{
    // v2 attached a live condition to TX. Present it as an explicit RX step;
    // legacy CLI Expect/CaptureMs metadata remains on the original TX unchanged.
    public static void ExpandResponseChecks(CommandSequence sequence)
    {
        if (!sequence.Steps.Any(step => step.ResponseCheck is not null)) return;
        var result = new List<CommandSequenceStep>();
        foreach (var source in sequence.Steps)
        {
            var copy = CommandSequenceExecutionPlan.CloneStep(source);
            var check = copy.ResponseCheck;
            copy.ResponseCheck = null;
            result.Add(copy);
            if (check is null) continue;
            check.Validate();
            if (source.Kind != CommandSequenceStepKind.Tx) throw new ArgumentException("Only legacy TX steps can contain ResponseCheck.");
            result.Add(new CommandSequenceStep
            {
                Kind = CommandSequenceStepKind.RxCheck, IsEnabled = source.IsEnabled,
                CommandText = check.Pattern, DelayAfterMs = check.TimeoutMs,
                TxMode = check.Kind == "hex" ? TxSendMode.Hex : TxSendMode.Terminal,
                LineEndingMode = TxLineEndingMode.None, Comment = source.Comment,
                Name = source.Name is null ? null : source.Name + " RX"
            });
        }
        sequence.Steps = new ObservableCollection<CommandSequenceStep>(result);
        for (var i = 0; i < result.Count; i++) result[i].StepNumber = i + 1;
    }
}
