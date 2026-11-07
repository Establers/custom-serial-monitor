using System.Text.Json;
using System.Text.Json.Serialization;

namespace SerialMonitor.Core.Protocols.VirtualModbus;

public sealed record VirtualModbusDefinition
{
    public required int SchemaVersion { get; init; }
    public required List<VirtualModbusDeviceDefinition> Devices { get; init; }
}

public sealed record VirtualModbusDeviceDefinition
{
    public required string Name { get; init; }
    public required int UnitId { get; init; }
    public required List<VirtualModbusRegisterDefinition> Registers { get; init; }
}

public sealed record VirtualModbusRegisterDefinition
{
    public required string Name { get; init; }
    public required string Area { get; init; }
    // Always the zero-based address carried on the wire, never 30001/40001 notation.
    public required int Address { get; init; }
    public required string DataType { get; init; }
    public JsonElement? Value { get; init; }
    public ushort[]? RawWords { get; init; }
    public double Scale { get; init; } = 1;
    public double Offset { get; init; }
    public string ByteOrder { get; init; } = "bigEndian";
    public string WordOrder { get; init; } = "highFirst";
    public bool Writable { get; init; } = true;
    public string Unit { get; init; } = "";
    public string Description { get; init; } = "";
}

public static class VirtualModbusJson
{
    public const int MaximumJsonBytes = 16 * 1024 * 1024;
    public const int MaximumDevices = 32;
    public const int MaximumRegisters = 8192;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static VirtualModbusDefinition Parse(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new FormatException("JSON exceeds the 16 MiB limit.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateProperties(document.RootElement);
            var definition = JsonSerializer.Deserialize<VirtualModbusDefinition>(json, Options)
                ?? throw new FormatException("A JSON object is required.");
            // Validation constructs a temporary store; no partial import is possible.
            new VirtualModbusDeviceStore().Add(definition);
            return definition;
        }
        catch (JsonException ex) { throw new FormatException($"Invalid device JSON: {ex.Message}", ex); }
    }

    public static string Serialize(VirtualModbusDefinition definition) => JsonSerializer.Serialize(definition, Options);

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new FormatException($"Duplicate JSON property: {property.Name}.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray()) RejectDuplicateProperties(value);
    }
}
