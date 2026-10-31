using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

// Closing the dialog discards this draft. Only a validated primary-button
// result is returned to the caller; editing never writes into its source step.
internal sealed class SequenceStepEditSession(CommandSequenceStep source)
{
    public CommandSequenceStep Draft { get; } = CommandSequenceExecutionPlan.CloneStep(source);
    public void FollowTxMode() => Draft.TxMode = null;
    public CommandSequenceStep CreateResult() => CommandSequenceExecutionPlan.CloneStep(Draft);
}
