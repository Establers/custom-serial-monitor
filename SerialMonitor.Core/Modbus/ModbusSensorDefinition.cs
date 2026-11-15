using System.Text.Json;
using System.Text.Json.Serialization;
using SerialMonitor.Core.Protocols;

namespace SerialMonitor.Core.Modbus;

// Declarative data only: imports never run scripts, open ports or transmit.
public sealed class ModbusSensorDefinition
{
    [JsonRequired] public int SchemaVersion { get; set; }
    [JsonRequired] public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    [JsonRequired] public int UnitId { get; set; }
    public int ResponseDelayMs { get; set; }
    [JsonRequired] public List<ModbusSensorPoint> Points { get; set; } = [];
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }, MaxDepth = 16, WriteIndented = true
    };
    public static ModbusSensorDefinition Parse(string json)
    {
        if (json.Length > 262144) throw new ArgumentException("JSON must be at most 256 Ki characters.");
        // Reject duplicate properties instead of silently using the last value from AI output.
        using var document = JsonDocument.Parse(json, new() { MaxDepth = 16 });
        CheckKeys(document.RootElement);
        var definition = JsonSerializer.Deserialize<ModbusSensorDefinition>(json, JsonOptions)
            ?? throw new ArgumentException("A sensor object is required.");
        definition.Validate();
        return definition;
    }
    private static void CheckKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new ArgumentException($"Duplicate property: {property.Name}");
                CheckKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) CheckKeys(child);
    }
    public void Validate()
    {
        if (SchemaVersion != 1) throw new ArgumentException("schemaVersion must be 1.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 128 || Source is null || Source.Length > 1024)
            throw new ArgumentException("Enter a name (1–128 characters) and source (up to 1024 characters).");
        if (UnitId is < 1 or > 247 || ResponseDelayMs is < 0 or > 1000)
            throw new ArgumentException("unitId: 1–247; responseDelayMs: 0–1000.");
        if (Points is null || Points.Count is < 1 or > 1024) throw new ArgumentException("Define 1–1024 points.");
        var occupied = new HashSet<(int, int)>();
        foreach (var point in Points)
        {
            if (point is null || point.Area is < 1 or > 4) throw new ArgumentException("area must be a read function: 1, 2, 3 or 4.");
            var item = point.Item();
            item.Validate(point.Area <= 2);
            if (string.IsNullOrWhiteSpace(item.Name)) throw new ArgumentException("Each point needs a name.");
            if (point.Area is 2 or 4 && point.Writable) throw new ArgumentException("Input areas cannot be writable.");
            if ((long)point.Address + item.RegisterCount > 65536) throw new ArgumentException("Address range exceeds 65535.");
            _ = ModbusItemCodec.Encode(point.InitialValue, item, point.Area <= 2);
            for (var n = 0; n < item.RegisterCount; n++)
                if (!occupied.Add((point.Area, point.Address + n))) throw new ArgumentException("Point addresses overlap within an area.");
        }
    }
}

public sealed class ModbusSensorPoint
{
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public int Area { get; set; }
    [JsonRequired] public int Address { get; set; }
    public ModbusValueType ValueType { get; set; } = ModbusValueType.UInt16;
    public double Scale { get; set; } = 1;
    public string Unit { get; set; } = "";
    public bool SwapBytes { get; set; }
    public bool SwapWords { get; set; }
    public bool Writable { get; set; }
    [JsonRequired] public string InitialValue { get; set; } = "";
    public ModbusItemDefinition Item() => new()
    {
        Name = Name, Address = Address, ValueType = ValueType, Scale = Scale, Unit = Unit,
        SwapBytes = SwapBytes, SwapWords = SwapWords, Access = Writable ? ModbusItemAccess.ReadWrite : ModbusItemAccess.ReadOnly
    };
}
