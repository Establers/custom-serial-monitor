using System.Text.Json;
using System.Text.Json.Serialization;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;

namespace SerialMonitor.WinUI.Models;

// Unknown or malformed command metadata remains visibly invalid through
// clone/save/load. It must never become a default device, value or ACK policy.
public sealed class ModbusCommandSettingsJsonConverter : JsonConverter<ModbusCommandSettings>
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
        options.Converters.Add(new ModbusValueTypeJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public override ModbusCommandSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var source = document.RootElement;
        if (source.ValueKind == JsonValueKind.Null) return null!;
        if (source.ValueKind != JsonValueKind.Object) return new() { Commands = null!, HasInvalidMetadata = true };
        var settings = new ModbusCommandSettings();
        foreach (var property in source.EnumerateObject())
        {
            switch (property.Name.ToUpperInvariant())
            {
                case "COMMANDS":
                    settings.Commands = property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() <= ModbusCommandSettings.MaximumCommands
                        ? property.Value.EnumerateArray().Select(ReadCommand).ToList() : null!;
                    break;
                case "SELECTEDCOMMANDID":
                    if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null) settings.SelectedCommandId = property.Value.GetString();
                    else settings.HasInvalidMetadata = true;
                    break;
                case "HASINVALIDMETADATA": settings.HasInvalidMetadata |= property.Value.ValueKind != JsonValueKind.False; break;
            }
        }
        return settings;
    }

    private static ModbusCommandDefinition ReadCommand(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object) return null!;
        var command = new ModbusCommandDefinition { Id = null!, Steps = null! };
        foreach (var property in source.EnumerateObject())
        {
            switch (property.Name.ToUpperInvariant())
            {
                case "ID": command.Id = ReadText(property.Value); break;
                case "NAME": command.Name = ReadText(property.Value); break;
                case "STEPS":
                    command.Steps = property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() <= ModbusCommandDefinition.MaximumSteps
                        ? property.Value.EnumerateArray().Select(ReadStep).ToList() : null!;
                    break;
                case "HASINVALIDMETADATA": command.HasInvalidMetadata |= property.Value.ValueKind != JsonValueKind.False; break;
            }
        }
        return command;
    }

    private static ModbusCommandStep ReadStep(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object) return null!;
        var step = new ModbusCommandStep { UnitId = int.MinValue, Function = 0, Item = null!, Value = null!, Verification = null! };
        foreach (var property in source.EnumerateObject())
        {
            switch (property.Name.ToUpperInvariant())
            {
                case "NAME": step.Name = ReadText(property.Value); break;
                case "UNITID": step.UnitId = ReadInteger(property.Value); break;
                case "FUNCTION": step.Function = ReadFunction(property.Value); break;
                case "ITEM": step.Item = ReadItem(property.Value); break;
                case "VALUE": step.Value = ReadText(property.Value); break;
                case "TIMEOUTMS": step.TimeoutMs = ReadInteger(property.Value); break;
                case "VERIFICATION": step.Verification = ReadVerification(property.Value); break;
                case "HASINVALIDMETADATA": step.HasInvalidMetadata |= property.Value.ValueKind != JsonValueKind.False; break;
            }
        }
        return step;
    }

    private static ModbusItemDefinition ReadItem(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object) return null!;
        // Reuse the item converter so invalid address/type/access/value-name
        // handling stays identical to the monitor's existing definitions.
        var items = JsonSerializer.Deserialize<List<ModbusItemDefinition>>("[" + source.GetRawText() + "]", NestedOptions);
        var item = items?.SingleOrDefault();
        if (item is null) return null!;
        if (!source.EnumerateObject().Any(property => property.Name.Equals("ValueType", StringComparison.OrdinalIgnoreCase)))
            item.ValueType = (ModbusValueType)(-1);
        return item;
    }

    private static ModbusCommandVerification ReadVerification(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object) return null!;
        var verification = new ModbusCommandVerification { Mode = (ModbusCommandVerificationMode)(-1) };
        var statusFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in source.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name.ToUpperInvariant())
            {
                case "MODE": verification.Mode = ReadMode(value); break;
                case "FUNCTION": verification.Function = ReadFunction(value); statusFields.Add("Function"); break;
                case "ADDRESS": verification.Address = ReadInteger(value); statusFields.Add("Address"); break;
                case "MASK":
                    verification.Mask = value.ValueKind == JsonValueKind.Number && value.TryGetUInt16(out var mask) ? mask : (ushort)0;
                    statusFields.Add("Mask");
                    break;
                case "EXPECTEDVALUE":
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt16(out var expected)) verification.ExpectedValue = expected;
                    else verification.HasInvalidMetadata = true;
                    statusFields.Add("ExpectedValue");
                    break;
                case "TIMEOUTMS": verification.TimeoutMs = ReadInteger(value); break;
                case "INTERVALMS": verification.IntervalMs = ReadInteger(value); break;
                case "HASINVALIDMETADATA": verification.HasInvalidMetadata |= value.ValueKind != JsonValueKind.False; break;
            }
        }
        if (verification.Mode == ModbusCommandVerificationMode.Status && statusFields.Count != 4)
        {
            verification.HasInvalidMetadata = true;
            if (!statusFields.Contains("Function")) verification.Function = 0;
            if (!statusFields.Contains("Address")) verification.Address = int.MinValue;
            if (!statusFields.Contains("Mask")) verification.Mask = 0;
        }
        return verification;
    }

    private static string ReadText(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : null!;
    private static int ReadInteger(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : int.MinValue;
    private static byte ReadFunction(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetByte(out var number) ? number : (byte)0;

    private static ModbusCommandVerificationMode ReadMode(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return (ModbusCommandVerificationMode)number;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (text?.Contains(',') != true && Enum.TryParse<ModbusCommandVerificationMode>(text, true, out var named)) return named;
        }
        return (ModbusCommandVerificationMode)(-1);
    }

    public override void Write(Utf8JsonWriter writer, ModbusCommandSettings value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WritePropertyName(nameof(value.Commands));
        if (value.Commands is null) writer.WriteNullValue();
        else
        {
            writer.WriteStartArray();
            foreach (var command in value.Commands) WriteCommand(writer, command);
            writer.WriteEndArray();
        }
        writer.WriteString(nameof(value.SelectedCommandId), value.SelectedCommandId);
        if (value.HasInvalidMetadata) writer.WriteBoolean(nameof(value.HasInvalidMetadata), true);
        writer.WriteEndObject();
    }

    private static void WriteCommand(Utf8JsonWriter writer, ModbusCommandDefinition? command)
    {
        if (command is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString(nameof(command.Id), command.Id);
        writer.WriteString(nameof(command.Name), command.Name);
        writer.WritePropertyName(nameof(command.Steps));
        if (command.Steps is null) writer.WriteNullValue();
        else
        {
            writer.WriteStartArray();
            foreach (var step in command.Steps) WriteStep(writer, step);
            writer.WriteEndArray();
        }
        if (command.HasInvalidMetadata) writer.WriteBoolean(nameof(command.HasInvalidMetadata), true);
        writer.WriteEndObject();
    }

    private static void WriteStep(Utf8JsonWriter writer, ModbusCommandStep? step)
    {
        if (step is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString(nameof(step.Name), step.Name);
        writer.WriteNumber(nameof(step.UnitId), step.UnitId);
        writer.WriteNumber(nameof(step.Function), step.Function);
        writer.WritePropertyName(nameof(step.Item));
        JsonSerializer.Serialize(writer, step.Item, NestedOptions);
        writer.WriteString(nameof(step.Value), step.Value);
        writer.WriteNumber(nameof(step.TimeoutMs), step.TimeoutMs);
        writer.WritePropertyName(nameof(step.Verification));
        WriteVerification(writer, step.Verification);
        if (step.HasInvalidMetadata) writer.WriteBoolean(nameof(step.HasInvalidMetadata), true);
        writer.WriteEndObject();
    }

    private static void WriteVerification(Utf8JsonWriter writer, ModbusCommandVerification? verification)
    {
        if (verification is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        if (Enum.IsDefined(verification.Mode)) writer.WriteString(nameof(verification.Mode), verification.Mode.ToString());
        else writer.WriteNumber(nameof(verification.Mode), (int)verification.Mode);
        writer.WriteNumber(nameof(verification.Function), verification.Function);
        writer.WriteNumber(nameof(verification.Address), verification.Address);
        writer.WriteNumber(nameof(verification.Mask), verification.Mask);
        writer.WriteNumber(nameof(verification.ExpectedValue), verification.ExpectedValue);
        writer.WriteNumber(nameof(verification.TimeoutMs), verification.TimeoutMs);
        writer.WriteNumber(nameof(verification.IntervalMs), verification.IntervalMs);
        if (verification.HasInvalidMetadata) writer.WriteBoolean(nameof(verification.HasInvalidMetadata), true);
        writer.WriteEndObject();
    }
}
