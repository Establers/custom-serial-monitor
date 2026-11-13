using System.Text.Json;
using System.Text.Json.Serialization;
using SerialMonitor.Core.Protocols;

namespace SerialMonitor.WinUI.Models;

// A malformed display preference must not discard the surrounding application
// profile or silently change its device target. Keep it invalid for correction.
public sealed class ModbusValueTypeJsonConverter : JsonConverter<ModbusValueType>
{
    private const ModbusValueType Invalid = (ModbusValueType)(-1);
    public override bool HandleNull => true;

    public override ModbusValueType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            // Enum.TryParse also combines comma-separated names. Display type
            // is a single choice, so such a value must remain invalid.
            return text?.Contains(',') != true && Enum.TryParse<ModbusValueType>(text, ignoreCase: true, out var named) ? named : Invalid;
        }
        if (reader.TokenType == JsonTokenType.Number)
            return reader.TryGetInt32(out var number) ? (ModbusValueType)number : Invalid;

        // Consume complete unknown values, including nested containers, so the
        // serializer can continue loading unrelated valid profile properties.
        reader.Skip();
        return Invalid;
    }

    public override void Write(Utf8JsonWriter writer, ModbusValueType value, JsonSerializerOptions options)
    {
        if (Enum.IsDefined(value)) writer.WriteStringValue(value.ToString());
        else writer.WriteNumberValue((int)value);
    }
}
