using System.Text.Json;
using System.Text.Json.Serialization;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;

namespace SerialMonitor.WinUI.Models;

// A malformed group must not discard unrelated profile data or fall back to a
// different valid device. Invalid metadata survives ordinary clone/save/load.
public sealed class ModbusPollingSettingsJsonConverter : JsonConverter<ModbusPollingSettings>
{
    private static readonly JsonSerializerOptions NestedOptions = CreateNestedOptions();
    public override bool HandleNull => true;

    private static JsonSerializerOptions CreateNestedOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = true, PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        options.Converters.Add(new ModbusItemDefinitionsJsonConverter());
        return options;
    }

    public override ModbusPollingSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var source = document.RootElement;
        if (source.ValueKind == JsonValueKind.Null) return null!;
        if (source.ValueKind != JsonValueKind.Object) return new() { Groups = null!, HasInvalidMetadata = true };
        var settings = new ModbusPollingSettings();
        foreach (var property in source.EnumerateObject())
        {
            switch (property.Name.ToUpperInvariant())
            {
                case "GROUPS": settings.Groups = ReadGroups(property.Value); break;
                case "SELECTEDGROUPID":
                    if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null)
                        settings.SelectedGroupId = property.Value.GetString();
                    else settings.HasInvalidMetadata = true;
                    break;
                case "HASINVALIDMETADATA":
                    settings.HasInvalidMetadata |= property.Value.ValueKind != JsonValueKind.False;
                    break;
            }
        }
        return settings;
    }

    private static List<ModbusPollingGroup> ReadGroups(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Array || source.GetArrayLength() > ModbusPollingSettings.MaximumGroups)
            return null!;
        return source.EnumerateArray().Select(ReadGroup).ToList();
    }

    private static ModbusPollingGroup ReadGroup(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object) return null!;
        // Missing identity or target is invalid, rather than a generated group
        // identity attached to the constructor's default device and address.
        var group = new ModbusPollingGroup { Id = null!, Settings = null! };
        foreach (var property in source.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name.ToUpperInvariant())
            {
                case "ID": group.Id = ReadText(value); break;
                case "NAME": group.Name = ReadText(value); break;
                case "ENABLED":
                    if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) group.Enabled = value.GetBoolean();
                    else { group.Enabled = false; group.HasInvalidMetadata = true; }
                    break;
                case "SETTINGS": group.Settings = ReadTarget(value, group); break;
                case "HASINVALIDMETADATA": group.HasInvalidMetadata |= value.ValueKind != JsonValueKind.False; break;
            }
        }
        return group;
    }

    private static ModbusMonitorSettings ReadTarget(JsonElement source, ModbusPollingGroup group)
    {
        if (source.ValueKind != JsonValueKind.Object) return null!;
        var settings = new ModbusMonitorSettings
        {
            UnitId = int.MinValue, Function = 0, Address = int.MinValue, Quantity = int.MinValue
        };
        var targetFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in source.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name.ToUpperInvariant())
            {
                case "UNITID": settings.UnitId = ReadInteger(value); targetFields.Add("UnitId"); break;
                case "FUNCTION":
                    settings.Function = value.ValueKind == JsonValueKind.Number && value.TryGetByte(out var function) ? function : (byte)0;
                    targetFields.Add("Function");
                    break;
                case "ADDRESS": settings.Address = ReadInteger(value); targetFields.Add("Address"); break;
                case "QUANTITY": settings.Quantity = ReadInteger(value); targetFields.Add("Quantity"); break;
                case "INTERVALMS": settings.IntervalMs = ReadInteger(value); break;
                case "TIMEOUTMS": settings.TimeoutMs = ReadInteger(value); break;
                case "VALUETYPE": settings.ValueType = ReadValueType(value); break;
                case "SCALE": settings.Scale = ReadScale(value); break;
                case "SWAPBYTES":
                    if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.SwapBytes = value.GetBoolean();
                    else group.HasInvalidMetadata = true;
                    break;
                case "SWAPWORDS":
                    if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.SwapWords = value.GetBoolean();
                    else group.HasInvalidMetadata = true;
                    break;
                case "ITEMS": settings.Items = value.Deserialize<List<ModbusItemDefinition>>(NestedOptions)!; break;
            }
        }
        if (targetFields.Count != 4) group.HasInvalidMetadata = true;
        return settings;
    }

    private static string ReadText(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : null!;
    private static int ReadInteger(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : int.MinValue;

    private static ModbusValueType ReadValueType(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return (ModbusValueType)number;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (text?.Contains(',') != true && Enum.TryParse<ModbusValueType>(text, true, out var named)) return named;
        }
        return (ModbusValueType)(-1);
    }

    private static double ReadScale(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString() switch { "Infinity" => double.PositiveInfinity, "-Infinity" => double.NegativeInfinity, _ => double.NaN };
        return double.NaN;
    }

    public override void Write(Utf8JsonWriter writer, ModbusPollingSettings value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WritePropertyName(nameof(value.Groups));
        if (value.Groups is null) writer.WriteNullValue();
        else
        {
            writer.WriteStartArray();
            foreach (var group in value.Groups)
            {
                if (group is null) { writer.WriteNullValue(); continue; }
                writer.WriteStartObject();
                writer.WriteString(nameof(group.Id), group.Id);
                writer.WriteString(nameof(group.Name), group.Name);
                writer.WriteBoolean(nameof(group.Enabled), group.Enabled);
                writer.WritePropertyName(nameof(group.Settings));
                JsonSerializer.Serialize(writer, group.Settings, NestedOptions);
                if (group.HasInvalidMetadata) writer.WriteBoolean(nameof(group.HasInvalidMetadata), true);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteString(nameof(value.SelectedGroupId), value.SelectedGroupId);
        if (value.HasInvalidMetadata) writer.WriteBoolean(nameof(value.HasInvalidMetadata), true);
        writer.WriteEndObject();
    }
}
