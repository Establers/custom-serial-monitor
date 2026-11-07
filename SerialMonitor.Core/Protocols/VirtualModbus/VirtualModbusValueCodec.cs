using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace SerialMonitor.Core.Protocols.VirtualModbus;

public static class VirtualModbusValueCodec
{
    public static int WordCount(string type) => type switch
    {
        "bool" or "uint16" or "int16" => 1,
        "uint32" or "int32" or "float32" => 2,
        "float64" => 4,
        _ => throw new FormatException($"Unsupported dataType '{type}'.")
    };

    public static ushort[] InitialWords(VirtualModbusRegisterDefinition definition)
    {
        if ((definition.Value is not null) == (definition.RawWords is not null))
            throw new FormatException($"{definition.Name}: specify exactly one of value or rawWords.");
        if (definition.RawWords is { } words)
        {
            if (words.Length != WordCount(definition.DataType) || definition.DataType == "bool" && words[0] > 1)
                throw new FormatException($"{definition.Name}: rawWords length/value does not match dataType.");
            return words.ToArray();
        }
        var value = definition.Value!.Value;
        if (definition.DataType == "bool" && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FormatException($"{definition.Name}: bool value must be true or false.");
        if (definition.DataType != "bool" && value.ValueKind != JsonValueKind.Number)
            throw new FormatException($"{definition.Name}: value must be a JSON number.");
        return Encode(definition, value.GetRawText());
    }

    public static ushort[] Encode(VirtualModbusRegisterDefinition d, string text)
    {
        if (d.DataType == "bool")
        {
            if (!bool.TryParse(text, out var enabled)) throw new FormatException("Enter true or false.");
            return [(ushort)(enabled ? 1 : 0)];
        }
        Span<byte> bytes = stackalloc byte[8];
        var count = WordCount(d.DataType);
        try
        {
            if (d.DataType is "float32" or "float64")
            {
                var engineering = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                var raw = (engineering - d.Offset) / d.Scale;
                if (!double.IsFinite(engineering) || !double.IsFinite(raw)) throw new FormatException("A finite value is required.");
                if (d.DataType == "float32")
                {
                    if (!float.IsFinite((float)raw)) throw new OverflowException();
                    BinaryPrimitives.WriteSingleBigEndian(bytes, (float)raw);
                }
                else BinaryPrimitives.WriteDoubleBigEndian(bytes, raw);
            }
            else
            {
                var raw = (decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) - (decimal)d.Offset) / (decimal)d.Scale;
                if (raw != decimal.Truncate(raw)) throw new FormatException("Value is not representable with this scale/offset.");
                switch (d.DataType)
                {
                    case "uint16": BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)raw)); break;
                    case "int16": BinaryPrimitives.WriteInt16BigEndian(bytes, checked((short)raw)); break;
                    case "uint32": BinaryPrimitives.WriteUInt32BigEndian(bytes, checked((uint)raw)); break;
                    case "int32": BinaryPrimitives.WriteInt32BigEndian(bytes, checked((int)raw)); break;
                }
            }
        }
        catch (Exception ex) when (ex is OverflowException or DivideByZeroException)
        { throw new FormatException($"Value is outside the range of {d.DataType} with this scale/offset.", ex); }
        var words = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            var word = BinaryPrimitives.ReadUInt16BigEndian(bytes[(i * 2)..]);
            if (d.ByteOrder == "littleEndian") word = BinaryPrimitives.ReverseEndianness(word);
            words[d.WordOrder == "lowFirst" ? count - 1 - i : i] = word;
        }
        return words;
    }

    public static string Decode(VirtualModbusRegisterDefinition d, ReadOnlySpan<ushort> words)
    {
        if (d.DataType == "bool") return words[0] == 0 ? "false" : "true";
        Span<byte> bytes = stackalloc byte[8];
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[d.WordOrder == "lowFirst" ? words.Length - 1 - i : i];
            if (d.ByteOrder == "littleEndian") word = BinaryPrimitives.ReverseEndianness(word);
            BinaryPrimitives.WriteUInt16BigEndian(bytes[(2 * i)..], word);
        }
        if (d.DataType is "float32" or "float64")
        {
            var raw = d.DataType == "float32" ? BinaryPrimitives.ReadSingleBigEndian(bytes) : BinaryPrimitives.ReadDoubleBigEndian(bytes);
            return (raw * d.Scale + d.Offset).ToString("G17", CultureInfo.InvariantCulture);
        }
        decimal integer = d.DataType switch
        {
            "uint16" => BinaryPrimitives.ReadUInt16BigEndian(bytes), "int16" => BinaryPrimitives.ReadInt16BigEndian(bytes),
            "uint32" => BinaryPrimitives.ReadUInt32BigEndian(bytes), _ => BinaryPrimitives.ReadInt32BigEndian(bytes)
        };
        return (integer * (decimal)d.Scale + (decimal)d.Offset).ToString(CultureInfo.InvariantCulture);
    }
}
