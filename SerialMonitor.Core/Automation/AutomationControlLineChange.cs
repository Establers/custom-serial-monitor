using System.Text.Json;

namespace SerialMonitor.Core.Automation;

public sealed record AutomationControlLineChange(string Line, bool Enabled, long? SessionGeneration)
{
    public static AutomationControlLineChange Parse(JsonElement arguments)
    {
        AutomationArguments.Validate("set_control_line", arguments);
        var line = arguments.String("line")!;
        if (line is not ("dtr" or "rts" or "rts_toggle"))
            throw new AutomationException("invalid_argument", "line must be dtr, rts or rts_toggle.");
        long? generation = arguments.TryGetProperty("sessionGeneration", out var value) ? value.GetInt64() : null;
        if (generation is <= 0) throw new AutomationException("invalid_argument", "sessionGeneration must be positive.");
        return new(line, arguments.GetProperty("enabled").GetBoolean(), generation);
    }
}
