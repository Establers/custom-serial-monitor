using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class ModbusRtuViewModel
{
    public string MonitorDetailsLabel => T("Details", "Communication status");
    public string MoreActionsLabel => T("MoreActions", "More actions");
    public string UsageDetailsLabel => T("UsageDetails", "Usage help");
    public string TargetSettingsSummary => NeedsPollingGroup ? P("SelectGroup", "Select a read group")
        : TryNumber(UnitText, out var unit) && unit is >= 1 and <= 247 &&
          TryNumber(AddressText, out var address) && TryNumber(QuantityText, out var quantity) &&
          address <= 65535 && quantity > 0 && (long)address + quantity <= 65536 && FunctionIndex is >= 0 and <= 3
            ? UiText.Format("ModbusMonitorReadRange", "ID {0} · FC{1:00} · Addresses {2}–{3} ({4})", unit, FunctionIndex + 1, address, address + quantity - 1, quantity)
            : T("CheckReadSettings", "Check the device ID and address range in Read settings");
    public string ReadOnceHelp => UiText.Format("ModbusMonitorReadOnceHelp", "Read the current values once: {0}", TargetSettingsSummary);
    public string StartHelp => _pollingSettings is null
        ? UiText.Format("ModbusMonitorStartHelp", "Read {0} repeatedly, waiting at least {1} ms after each response.", TargetSettingsSummary, IntervalText)
        : EnabledGroupCount == 0 ? P("NoEnabledGroups", "Include at least one read group in automatic reading.")
        : P("StartHelp", "Read these groups in turn. The dropdown selects which results to show:") + "\n" +
          string.Join("\n", PollingGroups.Where(row => row.IsEnabled).Select(row => row.DisplayName));
    public string DisplaySettingsLabel => T("DisplaySettings", "Value format and byte order");
    public string ReadSetupHelp => UiText.Format("ModbusMonitorReadSetupHelp", "Match the settings to the device's register map, then choose {0}.", ReadOnceLabel);
    public string AddressHelp => T("AddressHelp", "Use protocol addresses starting at 0. Example: holding register 40001 → address 0. Decimal and 0x HEX are accepted.");
    public string IntervalHelp => T("IntervalHelp", "1000 ms = 1 second. Automatic reading waits this long after each response.");
    public string IncludeGroupLabel => P("Enabled", "Include in automatic reading");
    public string FirstReadHelp => SelectedPollingGroup is { IsEnabled: false }
        ? P("ExcludedHelp", "This group is excluded from automatic reading. Use Read selected group to read it once, or include it in Read settings.")
        : UiText.Format("ModbusMonitorFirstReadHelp", "Use {0} to fill the table. Use automatic reading to watch the values change.", ReadOnceLabel);
    public Microsoft.UI.Xaml.Visibility FirstReadVisibility => Rows.Count > 0 && GetSelectedSnapshot().Values is null && !IsBusy && Error.Length == 0
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string CommandsMenuLabel => RecentCommandSummary.Length == 0
        ? CommandsLabel : C("ActionsWithResult", "Commands / result…");

    // Keep actionable failures visible in the single toolbar. Full, selectable
    // details stay in its flyout, without taking rows away from the register list.
    public string ErrorHelp => GetSelectedSnapshot() is { ReadRetryDelayMs: > 0 } snapshot && snapshot.Error == Error
        ? DescribeSnapshotError(snapshot) : DescribeCommunicationError(Error);
    internal static string DescribeSnapshotError(SerialMonitor.WinUI.Services.ModbusMonitorSnapshot snapshot) =>
        snapshot.ReadRetryDelayMs > 0
            ? UiText.Format("ModbusMonitorReadRetry", "Read failed · Retry interval {0} ms (after receive recovery). {1}",
                snapshot.ReadRetryDelayMs, snapshot.Error)
            : DescribeCommunicationError(snapshot.Error ?? "");
    internal static string DescribeCommunicationError(string error) => error switch
    {
        "Modbus exception 0x01." => T("ExceptionFunction", "Modbus 01 · The device does not support this function. Check the data area."),
        "Modbus exception 0x02." => T("ExceptionAddress", "Modbus 02 · The device rejected this address range. Check the start address and count."),
        "Modbus exception 0x03." => T("ExceptionValue", "Modbus 03 · The device rejected the value or count. Check its allowed range."),
        "Modbus exception 0x04." => T("ExceptionDevice", "Modbus 04 · The device could not process the request. Check the device status."),
        "Modbus exception 0x06." => T("ExceptionBusy", "Modbus 06 · The device is busy processing another request."),
        "Modbus response timed out; reconnect before continuing." => T("ResponseTimeoutHelp", "No response within the timeout. Check the device ID and serial settings, then reconnect the port."),
        "Modbus response CRC error; reconnect before continuing." => T("ResponseCrcHelp", "Response CRC check failed. Check the connection and serial settings, then reconnect the port."),
        "Unexpected Modbus response; reconnect before continuing." => T("ResponseMismatchHelp", "The response does not match the request. Check the device ID and other senders, then reconnect the port."),
        _ => error
    };
    public string MonitorSummary => Error.Length > 0 ? ErrorHelp
        : AvailabilityText.Length > 0 ? AvailabilityText
        : PollingHealthSummary.Length > 0 ? PollingHealthSummary
        : Statistics.Length > 0 ? Statistics : MonitorDetailsLabel;

    private void NotifyMonitorSummary()
    {
        OnPropertyChanged(nameof(MonitorSummary));
        OnPropertyChanged(nameof(ErrorHelp));
        OnPropertyChanged(nameof(FirstReadVisibility));
        OnPropertyChanged(nameof(RowsVisibility));
    }

    private void NotifyReadPresentation()
    {
        foreach (var name in new[] { nameof(TargetSettingsSummary), nameof(ReadOnceLabel), nameof(ReadOnceHelp), nameof(StartLabel), nameof(StartHelp), nameof(FirstReadHelp), nameof(FirstReadVisibility), nameof(SelectedGroupIncluded), nameof(ReadSetupHelp), nameof(EmptyHelp), nameof(EnabledGroupCount) })
            OnPropertyChanged(name);
    }
}
