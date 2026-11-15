using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class MainViewModel
{
    internal void ReportRuleTransferError(Exception error) => RecordRuleEditError(error.Message);

    internal bool ImportLogRules(IReadOnlyList<LogRule> rules)
    {
        // Validate and normalize the entire batch before mutating the active rules.
        var normalized = new List<LogRule>();
        foreach (var rule in rules)
        {
            if (!TryNormalizeLogRule(rule, out var item, out var error))
            {
                RecordRuleEditError(error);
                return false;
            }
            normalized.Add(item);
        }
        var plan = LogRuleTransfer.PrepareImport(LogRules, normalized);
        foreach (var rule in plan.Added) LogRules.Add(rule);
        var status = UiText.Format("RulesImportedStatus", "Imported {0} rules; skipped {1} duplicates; {2} sequence-trigger rules kept disabled.", plan.Added.Count, plan.Duplicates, plan.DisabledTriggers);
        if (plan.Added.Count > 0)
        {
            SelectedLogRule = plan.Added[0];
            ApplyLogRuleChanges(status);
        }
        else RecordRuleEditStatus(status);
        return true;
    }
}
