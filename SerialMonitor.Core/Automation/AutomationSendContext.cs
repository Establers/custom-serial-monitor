using System.Text.Json;

namespace SerialMonitor.Core.Automation;

// Internal device calls retain the session reserved by the public operation.
// A queued UI send must never silently follow an automatic reconnect.
public static class AutomationSendContext
{
    public static JsonElement Bind(JsonElement arguments, long generation)
    {
        var bound = arguments.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        bound["expectedSessionGeneration"] = JsonSerializer.SerializeToElement(generation);
        return JsonSerializer.SerializeToElement(bound, AutomationJson.Options);
    }

    public static void Verify(JsonElement arguments, CaptureConnection connection)
    {
        if (!arguments.TryGetProperty("expectedSessionGeneration", out var expected) || !expected.TryGetInt64(out var generation))
            throw new AutomationException("invalid_argument", "TX is missing its reserved serial session.");
        if (!connection.Connected || connection.Generation != generation)
            throw new AutomationException("disconnected", "The reserved serial session changed before TX. No command was sent to the replacement session.");
    }
}
