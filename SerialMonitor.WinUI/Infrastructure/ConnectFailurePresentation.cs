using System.Text;
using SerialMonitor.Core.Diagnostics;

namespace SerialMonitor.WinUI.Infrastructure;

internal static class ConnectFailurePresentation
{
    public static string Summary(SerialConnectFailureReport report)
    {
        if (report.OwnerProbe.Owners.FirstOrDefault() is { } owner)
            return UiText.Format("ConnectDiagOwnerSummary", "{0}: connection failed; open in {1} (PID {2}).", report.PortName, Name(owner), owner.ProcessId);
        return UiText.Format("ConnectDiagSummary", "{0}: connection failed. {1}", report.PortName, Reason(report.Failure.Kind));
    }

    public static string Describe(SerialConnectFailureReport report)
    {
        var text = new StringBuilder(Reason(report.Failure.Kind));
        text.AppendLine().AppendLine();
        var probe = report.OwnerProbe;
        if (probe.Owners.Count > 0)
        {
            text.AppendLine(UiText.Get("ConnectDiagOwners", "Programs confirmed to have this port open:"));
            foreach (var owner in probe.Owners)
            {
                text.Append("• ").Append(Name(owner)).Append(" (PID ").Append(owner.ProcessId).Append(')');
                if (owner.ProcessId == Environment.ProcessId) text.Append(' ').Append(UiText.Get("ConnectDiagThisApp", "[this app]"));
                text.AppendLine();
            }
            text.AppendLine(UiText.Get("ConnectDiagCloseOwner", "Close the port connection in that program, then retry Connect."));
        }
        else if (probe.Status != PortOwnerProbeStatus.NotNeeded)
        {
            text.AppendLine(probe.Status switch
            {
                PortOwnerProbeStatus.TimedOut => UiText.Get("ConnectDiagProbeTimeout", "Owner lookup reached its time limit; no program was identified."),
                PortOwnerProbeStatus.Limited => UiText.Get("ConnectDiagProbeLimited", "No owner was identified. Some processes could not be inspected because of permissions or scan limits."),
                PortOwnerProbeStatus.PortNotPresent => UiText.Get("ConnectDiagProbeAbsent", "The port mapping was unavailable when checking its owner. Refresh the port list."),
                PortOwnerProbeStatus.Unavailable => UiText.Get("ConnectDiagProbeUnavailable", "Owner lookup is unavailable in this environment."),
                _ => UiText.Get("ConnectDiagProbeUnknown", "No owner was identified. This does not prove that the port is free.")
            });
        }
        text.AppendLine();
        if (report.Failure.NativeErrorCode is { } code)
            text.AppendLine(UiText.Format("ConnectDiagNativeCode", "Windows error: {0} (0x{0:X8})", code));
        text.AppendLine(UiText.Get("ConnectDiagOriginalError", "Original error:"));
        text.Append(report.Failure.TechnicalMessage);
        return text.ToString().Trim();
    }

    private static string Name(PortOwnerProcess process) => string.IsNullOrWhiteSpace(process.ProcessName)
        ? UiText.Get("ConnectDiagUnnamedProcess", "Process name unavailable") : process.ProcessName[..Math.Min(128, process.ProcessName.Length)];

    private static string Reason(SerialConnectFailureKind kind) => kind switch
    {
        SerialConnectFailureKind.AccessDenied => UiText.Get("ConnectDiagAccessDenied", "Access to the port was denied. Another connection, permissions, or the device driver may be responsible."),
        SerialConnectFailureKind.PortInUse => UiText.Get("ConnectDiagPortBusy", "The port could not be opened because it is reported as in use or sharing is restricted."),
        SerialConnectFailureKind.PortNotFound => UiText.Get("ConnectDiagPortMissing", "The selected port was not found. Refresh ports and check the USB connection and driver."),
        SerialConnectFailureKind.DeviceUnavailable => UiText.Get("ConnectDiagDeviceUnavailable", "The device is disconnected, unavailable, or its driver reported an error. Check the device and USB connection."),
        SerialConnectFailureKind.TimedOut => UiText.Get("ConnectDiagOpenTimeout", "Opening the port timed out. Check the device and driver; wait for the previous connection to finish closing."),
        SerialConnectFailureKind.InvalidSettings => UiText.Get("ConnectDiagSettings", "The requested port settings are invalid or unsupported by this device or driver."),
        SerialConnectFailureKind.SessionStartupFailed => UiText.Get("ConnectDiagSessionStartup", "The connection session could not be started. Check the original error below; port ownership was not queried for a session startup failure."),
        _ => UiText.Get("ConnectDiagOpenFailed", "The port could not be opened. Check the device, cable, driver, and other serial programs.")
    };
}
