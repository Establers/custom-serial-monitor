using System.Text.Json;

namespace SerialMonitor.Core.Automation;

// Validate at both public boundaries, before selecting a GUI or reserving TX.
// In particular, a misspelled port/txMode must never fall back to an unsafe default.
public static class AutomationArguments
{
    public static void Validate(string operation, JsonElement args)
    {
        var fields = operation switch
        {
            "capabilities" or "list_instances" => "",
            "status" or "ports" or "disconnect" or "cancel_capture" => "instanceId",
            "list_commands" or "sequence_list" => "instanceId offset limit",
            "connect" => "instanceId port baudRate",
            "configure" => "instanceId port baudRate byteTimeoutMs",
            "set_mode" => "instanceId mode",
            "sequence_get" or "sequence_delete" => "instanceId name",
            "sequence_save" => "instanceId sequence overwrite",
            "sequence_start" => "instanceId name port",
            "sequence_status" or "sequence_cancel" => "instanceId runId",
            "send" => "instanceId command txMode lineEnding port previewBytes format",
            "send_and_capture" => "instanceId command txMode lineEnding port previewBytes format durationMs",
            "capture_rx" => "instanceId port previewBytes format durationMs",
            "read_capture" => "instanceId captureId offset limit format",
            _ => throw new AutomationException("unknown_operation", "Unknown operation: " + operation)
        };
        if (args.ValueKind != JsonValueKind.Object) Invalid("arguments must be an object.");
        var allowed = fields.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in args.EnumerateObject())
        {
            if (!allowed.Contains(field.Name) || !seen.Add(field.Name))
                Invalid("Unknown or duplicate argument: " + field.Name);
            var valid = field.Name switch
            {
                "sequence" => field.Value.ValueKind == JsonValueKind.Object,
                "overwrite" => field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "baudRate" or "byteTimeoutMs" or "offset" or "limit" or "previewBytes" or "durationMs" =>
                    field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out _),
                _ => field.Value.ValueKind == JsonValueKind.String
            };
            if (!valid) Invalid("Invalid type for argument: " + field.Name);
            if (field.Value.ValueKind == JsonValueKind.String)
            {
                var value = field.Value.GetString()!;
                var max = field.Name == "command" ? 16384 : 128;
                if (value.Length == 0 || value.Length > max ||
                    (field.Name is "instanceId" or "port" or "name" or "runId" or "captureId" && string.IsNullOrWhiteSpace(value)))
                    Invalid(field.Name + " must contain 1.." + max + " characters.");
            }
        }
        string[] required = operation switch
        {
            "connect" => ["port"], "set_mode" => ["mode"],
            "sequence_get" or "sequence_delete" or "sequence_start" => ["name"],
            "sequence_save" => ["sequence"], "sequence_cancel" => ["runId"],
            "send" or "send_and_capture" => ["command"], "read_capture" => ["captureId"], _ => []
        };
        foreach (var field in required)
            if (!seen.Contains(field)) Invalid(field + " is required.");
        CheckChoice(args, "txMode", "text", "hex");
        CheckChoice(args, "format", "text", "hex");
        CheckChoice(args, "mode", "terminal", "hex");
        CheckChoice(args, "lineEnding", "none", "cr", "lf", "crlf");
        CheckRange(args, "baudRate", 1, 10000000);
        CheckRange(args, "byteTimeoutMs", 1, 5000);
        CheckRange(args, "durationMs", 0, 60000);
        CheckRange(args, "previewBytes", 4, 65536);
        CheckRange(args, "offset", 0, int.MaxValue);
        CheckRange(args, "limit", operation is "sequence_list" or "list_commands" ? 1 : 4,
            operation is "sequence_list" or "list_commands" ? 100 : 65536);
    }

    private static void CheckChoice(JsonElement args, string field, params string[] choices)
    {
        if (args.TryGetProperty(field, out var value) && !choices.Contains(value.GetString()))
            Invalid("Invalid " + field + ". Expected " + string.Join(", ", choices) + ".");
    }

    private static void CheckRange(JsonElement args, string field, int min, int max)
    {
        if (args.TryGetProperty(field, out var value) && (value.GetInt32() < min || value.GetInt32() > max))
            Invalid(field + " must be between " + min + " and " + max + ".");
    }

    private static void Invalid(string message) => throw new AutomationException("invalid_argument", message);
}
