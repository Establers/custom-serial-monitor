namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class ModbusRtuViewModel
{
    public string MonitorDetailsLabel => T("Details", "Communication details");
    public string MoreActionsLabel => T("MoreActions", "More actions");
    public string UsageDetailsLabel => T("UsageDetails", "Usage details and precautions");
    public string TargetSettingsSummary => _pollingSettings is not null && SelectedPollingGroup is null
        ? P("SelectGroup", "Select a group")
        : $"ID {UnitText} · FC {FunctionIndex + 1:00} · {AddressText} + {QuantityText} · {IntervalText} ms";
    public string CommandsMenuLabel => RecentCommandSummary.Length == 0
        ? CommandsLabel : C("ActionsWithResult", "Commands / result…");

    // Keep actionable failures visible in the single toolbar. Full, selectable
    // details stay in its flyout, without taking rows away from the register list.
    public string MonitorSummary => Error.Length > 0 ? Error
        : AvailabilityText.Length > 0 ? AvailabilityText
        : PollingHealthSummary.Length > 0 ? PollingHealthSummary
        : Statistics.Length > 0 ? Statistics : MonitorDetailsLabel;

    private void NotifyMonitorSummary() => OnPropertyChanged(nameof(MonitorSummary));
}
