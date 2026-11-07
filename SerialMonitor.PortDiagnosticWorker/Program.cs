using System.Text.Json;
using SerialMonitor.Core.Diagnostics;
using SerialMonitor.PortDiagnosticWorker;

// This process never opens the requested COM port. Native name queries can
// block inside a device driver, so both this watchdog and the caller bound it.
using var deadline = new Timer(_ => Environment.Exit(3), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
var port = args.Length == 2 ? PortOwnerProbeProtocol.NormalizePort(args[0]) : null;
if (port is null || !int.TryParse(args.ElementAtOrDefault(1), out var requester) || requester <= 0) return 2;
PortOwnerProbeReport report;
try { report = WindowsPortOwnerProbe.Find(port, requester); }
catch (Exception ex) { report = PortOwnerProbeReport.Empty(port, PortOwnerProbeStatus.Unavailable, ex.GetType().Name); }
Console.WriteLine(JsonSerializer.Serialize(report, PortOwnerProbeProtocol.JsonOptions));
return 0;
