using System.Text.Json;
using System.Text.Json.Serialization;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;

namespace SerialMonitor.WinUI.Models;

// Corrupt item metadata must disable Modbus, not discard the surrounding app
// profile or turn a protected item back into an undefined writable address.
public sealed class ModbusItemDefinitionsJsonConverter : JsonConverter<List<ModbusItemDefinition>>
{
    public override bool HandleNull => true;

    public override List<ModbusItemDefinition> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Null) return null!;
        if (root.ValueKind != JsonValueKind.Array) return [null!];
        // At most 2,000 bits can be monitored. Keep an invalid marker rather
        // than allocating an arbitrary number of imported definitions.
        if (root.GetArrayLength() > 2000) return [null!];
        return root.EnumerateArray().Select(ReadItem).ToList();
    }

    private static ModbusItemDefinition ReadItem(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object) return null!;
        var item = new ModbusItemDefinition { Address = int.MinValue };
        var invalid = false;
        foreach (var property in source.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name.ToUpperInvariant())
            {
                case "ADDRESS":
                    item.Address = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var address) ? address : int.MinValue;
                    break;
                case "NAME": item.Name = ReadText(value); break;
                case "UNIT": item.Unit = ReadText(value); break;
                case "VALUETYPE": item.ValueType = ReadEnum<ModbusValueType>(value); break;
                case "ACCESS": item.Access = ReadEnum<ModbusItemAccess>(value); break;
                case "SCALE":
                    item.Scale = ReadScale(value);
                    break;
                case "SWAPBYTES":
                    if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) item.SwapBytes = value.GetBoolean();
                    else invalid = true;
                    break;
                case "SWAPWORDS":
                    if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) item.SwapWords = value.GetBoolean();
                    else invalid = true;
                    break;
                case "VALUENAMES": item.ValueNames = ReadValueNames(value, ref invalid); break;
            }
        }
        if (invalid) item.ValueType = (ModbusValueType)(-1);
        return item;
    }

    private static string ReadText(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : null!;

    private static T ReadEnum<T>(JsonElement value) where T : struct, Enum
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return (T)Enum.ToObject(typeof(T), number);
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (text?.Contains(',') != true && Enum.TryParse<T>(text, ignoreCase: true, out var named)) return named;
        }
        return (T)Enum.ToObject(typeof(T), -1);
    }

    private static double ReadScale(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var scale)) return scale;
        // JSON strings allow an invalid imported scale to survive an ordinary
        // profile save without throwing or silently changing its interpretation.
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString() switch { "Infinity" => double.PositiveInfinity, "-Infinity" => double.NegativeInfinity, _ => double.NaN };
        return double.NaN;
    }

    private static List<ModbusValueName> ReadValueNames(JsonElement value, ref bool invalid)
    {
        if (value.ValueKind == JsonValueKind.Null) return null!;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 128)
        {
            invalid = true;
            return [];
        }
        var names = new List<ModbusValueName>();
        foreach (var source in value.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object) { names.Add(null!); continue; }
            var name = new ModbusValueName();
            var hasValue = false;
            foreach (var property in source.EnumerateObject())
            {
                if (property.Name.Equals("Value", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var number)) { name.Value = number; hasValue = true; }
                    else invalid = true;
                }
                else if (property.Name.Equals("Name", StringComparison.OrdinalIgnoreCase)) name.Name = ReadText(property.Value);
            }
            if (!hasValue)
            {
                // A default key of zero would become a different valid command
                // if the user corrected only the item's display type later.
                invalid = true;
                names.Add(null!);
            }
            else names.Add(name);
        }
        return names;
    }

    public override void Write(Utf8JsonWriter writer, List<ModbusItemDefinition> value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        foreach (var item in value)
        {
            if (item is null) { writer.WriteNullValue(); continue; }
            writer.WriteStartObject();
            writer.WriteNumber(nameof(item.Address), item.Address);
            writer.WriteString(nameof(item.Name), item.Name);
            WriteEnum(writer, nameof(item.ValueType), item.ValueType);
            if (double.IsFinite(item.Scale)) writer.WriteNumber(nameof(item.Scale), item.Scale);
            else writer.WriteString(nameof(item.Scale), double.IsNaN(item.Scale) ? "NaN" : item.Scale > 0 ? "Infinity" : "-Infinity");
            writer.WriteString(nameof(item.Unit), item.Unit);
            writer.WriteBoolean(nameof(item.SwapBytes), item.SwapBytes);
            writer.WriteBoolean(nameof(item.SwapWords), item.SwapWords);
            WriteEnum(writer, nameof(item.Access), item.Access);
            writer.WritePropertyName(nameof(item.ValueNames));
            if (item.ValueNames is null) writer.WriteNullValue();
            else
            {
                writer.WriteStartArray();
                foreach (var name in item.ValueNames)
                {
                    if (name is null) { writer.WriteNullValue(); continue; }
                    writer.WriteStartObject();
                    writer.WriteNumber(nameof(name.Value), name.Value);
                    writer.WriteString(nameof(name.Name), name.Name);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteEnum<T>(Utf8JsonWriter writer, string name, T value) where T : struct, Enum
    {
        if (Enum.IsDefined(value)) writer.WriteString(name, value.ToString());
        else writer.WriteNumber(name, Convert.ToInt32(value));
    }
}
