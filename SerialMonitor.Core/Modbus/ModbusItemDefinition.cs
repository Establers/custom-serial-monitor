using System.Text.Json.Serialization;
using SerialMonitor.Core.Protocols;

namespace SerialMonitor.Core.Modbus;

public enum ModbusItemAccess { ReadOnly, ReadWrite }

public sealed class ModbusValueName
{
    public long Value { get; set; }
    public string Name { get; set; } = "";
    public ModbusValueName Clone() => new() { Value = Value, Name = Name };
}

// Configuration only. Address is the zero-based wire address inside its read area.
public sealed class ModbusItemDefinition
{
    public const int MaximumNameLength = 128;
    public const int MaximumUnitLength = 32;
    public const int MaximumValueNames = 128;

    public int Address { get; set; }
    public string Name { get; set; } = "";
    public ModbusValueType ValueType { get; set; } = ModbusValueType.UInt16;
    public double Scale { get; set; } = 1;
    public string Unit { get; set; } = "";
    public bool SwapBytes { get; set; }
    public bool SwapWords { get; set; }
    public ModbusItemAccess Access { get; set; } = ModbusItemAccess.ReadOnly;
    public List<ModbusValueName> ValueNames { get; set; } = [];

    [JsonIgnore]
    public int RegisterCount => ValueType is ModbusValueType.UInt16 or ModbusValueType.Int16 ? 1 : 2;

    public ModbusItemDefinition Clone() => new()
    {
        Address = Address, Name = Name, ValueType = ValueType, Scale = Scale, Unit = Unit,
        SwapBytes = SwapBytes, SwapWords = SwapWords, Access = Access,
        ValueNames = ValueNames?.Select(value => value?.Clone()!).ToList()!
    };

    public void Validate(bool isCoil = false)
    {
        if (!Enum.IsDefined(ValueType)) throw new ArgumentOutOfRangeException(nameof(ValueType));
        if (!Enum.IsDefined(Access)) throw new ArgumentOutOfRangeException(nameof(Access));
        if (Address < 0 || (long)Address + RegisterCount > 65536)
            throw new ArgumentOutOfRangeException(nameof(Address), "The complete item must fit in the address space.");
        if (!double.IsFinite(Scale)) throw new ArgumentOutOfRangeException(nameof(Scale));
        ValidateText(Name, MaximumNameLength, nameof(Name));
        ValidateText(Unit, MaximumUnitLength, nameof(Unit));
        if (isCoil && (ValueType != ModbusValueType.UInt16 || Scale != 1 || SwapBytes || SwapWords))
            throw new ArgumentException("Coil items use UInt16, multiplier 1 and the original byte order.");
        if (ValueNames is null || ValueNames.Count > MaximumValueNames)
            throw new ArgumentException($"At most {MaximumValueNames} value names are allowed.", nameof(ValueNames));
        var keys = new HashSet<long>();
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in ValueNames)
        {
            if (value is null) throw new ArgumentException("A value name cannot be null.", nameof(ValueNames));
            ValidateText(value.Name, MaximumNameLength, nameof(ValueNames));
            if (string.IsNullOrWhiteSpace(value.Name) || !keys.Add(value.Value) || !labels.Add(value.Name.Trim()))
                throw new ArgumentException("Value names must be nonempty and have unique values and labels.", nameof(ValueNames));
            if (isCoil ? value.Value is not (0 or 1) : !FitsValueType(value.Value))
                throw new ArgumentException("A named value is outside the item's numeric type.", nameof(ValueNames));
        }
    }

    private bool FitsValueType(long value) => ValueType switch
    {
        ModbusValueType.UInt16 => value is >= ushort.MinValue and <= ushort.MaxValue,
        ModbusValueType.Int16 => value is >= short.MinValue and <= short.MaxValue,
        ModbusValueType.UInt32 => value is >= uint.MinValue and <= uint.MaxValue,
        ModbusValueType.Int32 => value is >= int.MinValue and <= int.MaxValue,
        ModbusValueType.Float32 => FitsFloat(value),
        _ => false
    };

    private static bool FitsFloat(long value)
    {
        var converted = (double)(float)value;
        return converted >= -9223372036854775808d && converted < 9223372036854775808d && (long)converted == value;
    }

    private static void ValidateText(string? text, int maximumLength, string parameter)
    {
        if (text is null || text.Length > maximumLength || text.Any(char.IsControl))
            throw new ArgumentException($"Text must contain at most {maximumLength} printable characters.", parameter);
    }
}
