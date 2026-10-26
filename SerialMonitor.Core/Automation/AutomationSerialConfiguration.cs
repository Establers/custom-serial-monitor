using System.Text.Json;

namespace SerialMonitor.Core.Automation;

// ByteTimeoutMs controls host-side HEX grouping. It is deliberately independent
// of baud rate: driver buffering and Windows scheduling need measured slack.
public sealed record AutomationSerialConfiguration(int? BaudRate, int? ByteTimeoutMs)
{
    public const int MinBaudRate = 1;
    public const int MaxBaudRate = 10_000_000;
    public const int MinByteTimeoutMs = 1;
    public const int MaxByteTimeoutMs = 5000;

    public static AutomationSerialConfiguration Parse(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw Invalid("configure requires an object containing baudRate and/or byteTimeoutMs.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (property.Name is not ("baudRate" or "byteTimeoutMs" or "port" or "instanceId") || !names.Add(property.Name))
                throw Invalid("Unknown, misspelled or duplicate configure property: " + property.Name);
            if (property.Name is "port" or "instanceId" &&
                (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString())))
                throw Invalid(property.Name + " must be a nonempty string.");
        }

        var baud = ReadInteger(arguments, "baudRate", MinBaudRate, MaxBaudRate);
        var timeout = ReadInteger(arguments, "byteTimeoutMs", MinByteTimeoutMs, MaxByteTimeoutMs);
        if (baud is null && timeout is null)
            throw Invalid("Specify baudRate and/or byteTimeoutMs.");
        return new(baud, timeout);
    }

    private static int? ReadInteger(JsonElement arguments, string name, int minimum, int maximum)
    {
        if (!arguments.TryGetProperty(name, out var element)) return null;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value) || value < minimum || value > maximum)
            throw Invalid($"{name} must be an integer between {minimum} and {maximum}.");
        return value;
    }

    private static AutomationException Invalid(string message) => new("invalid_argument", message);
}
