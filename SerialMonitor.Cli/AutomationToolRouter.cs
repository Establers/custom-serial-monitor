using System.Text.Json;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Cli;

public static class AutomationToolRouter
{
    public static async Task<AutomationResponse> CallAsync(string operation, JsonElement args, string? defaultInstance,
        CancellationToken cancellationToken)
    {
        if (operation == "capabilities")
        {
            if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any())
                throw new AutomationException("invalid_argument", "Capabilities takes no arguments and does not select a GUI.");
            return AutomationResponse.Ok(ControlCapabilities.Describe());
        }
        var instances = AutomationDiscovery.List();
        if (operation == "list_instances") return AutomationResponse.Ok(new { instances });
        var selected = args.String("instanceId", defaultInstance);
        if (selected is null && instances.Count != 1)
            throw new AutomationException(instances.Count == 0 ? "gui_not_running" : "instance_required",
                instances.Count == 0 ? "Start the updated Serial Monitor GUI first." : "Multiple GUIs are running. Choose instanceId from serial_list_instances.");
        var instance = selected is null ? instances[0] : instances.FirstOrDefault(i => i.InstanceId == selected)
            ?? throw new AutomationException("instance_not_found", "The selected GUI instance is no longer running.");
        return await AutomationPipeClient.CallAsync(instance, operation, args, cancellationToken).ConfigureAwait(false);
    }
}
