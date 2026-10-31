using System.Text.Json;

namespace SerialMonitor.Core.Automation;

// JsonDocument accepts escaped, unpaired UTF-16 surrogates. Decode strings before
// dispatch so a later GetString cannot terminate the transport or change a TX.
public static class AutomationJsonValidation
{
    public static void ValidateStrings(JsonElement value)
    {
        try { ValidateStringsCore(value); }
        catch (InvalidOperationException ex)
        { throw new JsonException("JSON strings must contain valid Unicode.", ex); }
    }

    private static void ValidateStringsCore(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    _ = property.Name;
                    ValidateStringsCore(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) ValidateStringsCore(item);
                break;
            case JsonValueKind.String:
                _ = value.GetString();
                break;
        }
    }

    public static void CheckProperties(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("A JSON object is required.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new JsonException("Duplicate JSON property: " + property.Name);
            if (allowed.Length != 0 && !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new JsonException("Unknown JSON property: " + property.Name);
        }
    }

    public static AutomationRequest ParseRequest(string line)
    {
        using var document = JsonDocument.Parse(line);
        var request = document.RootElement;
        ValidateStrings(request);
        CheckProperties(request, "operation", "arguments");
        if (!request.TryGetProperty("operation", out var operation) || operation.ValueKind != JsonValueKind.String ||
            operation.GetString() is not { Length: > 0 and <= 128 } name || string.IsNullOrWhiteSpace(name) ||
            !request.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
            throw new JsonException("A nonempty operation and an arguments object are required.");
        return new(name, arguments.Clone());
    }
}
