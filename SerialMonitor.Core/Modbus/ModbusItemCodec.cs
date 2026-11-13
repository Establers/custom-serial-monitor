using System.Globalization;
using System.Numerics;
using SerialMonitor.Core.Protocols;

namespace SerialMonitor.Core.Modbus;

public readonly record struct ModbusItemValue(double RawValue, double Value);

// Converts one named item; serial ownership, permissions and UI remain with callers.
public static class ModbusItemCodec
{
    public static string FormatEngineeringValue(ReadOnlySpan<ushort> registers, ModbusItemDefinition definition)
    {
        var decoded = Decode(registers, definition);
        if (definition.ValueType == ModbusValueType.Float32)
            return decoded.Value.ToString(definition.Scale == 1 ? "G9" : "G17", CultureInfo.CurrentCulture);
        var scale = ParseExact(definition.Scale.ToString("R", CultureInfo.InvariantCulture));
        return FormatExact(new(new BigInteger(decoded.RawValue) * scale.Numerator, scale.Denominator));
    }

    public static ModbusItemValue Decode(ReadOnlySpan<ushort> registers, ModbusItemDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        if (registers.Length < definition.RegisterCount)
            throw new ArgumentException("The item is missing one or more registers.", nameof(registers));
        var first = Swap(registers[0], definition.SwapBytes);
        double raw;
        if (definition.RegisterCount == 1)
            raw = definition.ValueType == ModbusValueType.Int16 ? unchecked((short)first) : first;
        else
        {
            var second = Swap(registers[1], definition.SwapBytes);
            var bits = definition.SwapWords ? ((uint)second << 16) | first : ((uint)first << 16) | second;
            raw = definition.ValueType switch
            {
                ModbusValueType.UInt32 => (double)bits,
                ModbusValueType.Int32 => (double)unchecked((int)bits),
                _ => (double)BitConverter.Int32BitsToSingle(unchecked((int)bits))
            };
        }
        return new(raw, raw * definition.Scale);
    }

    public static ushort[] Encode(string value, ModbusItemDefinition definition, bool isCoil = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate(isCoil);
        if (definition.Scale == 0) throw new ArgumentException("An item with multiplier zero cannot be written.");
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
            throw new ArgumentException("Enter a numeric value or a defined value name (at most 256 characters).", nameof(value));
        var text = value.Trim();
        // Numeric input always means an engineering value, even if a display label is numeric.
        var numericInput = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out _) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        var named = numericInput ? null : definition.ValueNames.FirstOrDefault(candidate => string.Equals(candidate.Name.Trim(), text, StringComparison.OrdinalIgnoreCase));
        uint bits;
        if (definition.ValueType == ModbusValueType.Float32)
        {
            double raw;
            if (named is not null) raw = named.Value;
            else
            {
                var number = ParseNumber(text);
                if (!double.IsFinite(number.Value))
                    throw new ArgumentException("The numeric value must be finite.", nameof(value));
                if (number.Value == 0 && !number.Exact.Numerator.IsZero)
                    throw new ArgumentException("The value is too small to represent.", nameof(value));
                raw = number.Value / definition.Scale;
                if (raw == 0 && !number.Exact.Numerator.IsZero)
                    throw new ArgumentException("The scaled value is too small to represent.", nameof(value));
            }
            var encoded = (float)raw;
            if (!double.IsFinite(raw) || !float.IsFinite(encoded) || (raw != 0 && encoded == 0))
                throw new ArgumentException("The value cannot be represented as a finite Float32.", nameof(value));
            bits = unchecked((uint)BitConverter.SingleToInt32Bits(encoded));
        }
        else
        {
            BigInteger integer;
            if (named is not null) integer = named.Value;
            else
            {
                var number = ParseNumber(text).Exact;
                var multiplier = ParseExact(definition.Scale.ToString("R", CultureInfo.InvariantCulture));
                var numerator = number.Numerator * multiplier.Denominator;
                var denominator = number.Denominator * multiplier.Numerator;
                integer = BigInteger.DivRem(numerator, denominator, out var remainder);
                if (!remainder.IsZero)
                    throw new ArgumentException("The value cannot be represented without rounding the integer register value.", nameof(value));
            }
            var (minimum, maximum) = definition.ValueType switch
            {
                ModbusValueType.UInt16 => ((long)ushort.MinValue, (long)ushort.MaxValue),
                ModbusValueType.Int16 => ((long)short.MinValue, (long)short.MaxValue),
                ModbusValueType.UInt32 => ((long)uint.MinValue, (long)uint.MaxValue),
                _ => ((long)int.MinValue, (long)int.MaxValue)
            };
            if (integer < minimum || integer > maximum || (isCoil && integer != 0 && integer != 1))
                throw new ArgumentOutOfRangeException(nameof(value), "The value is outside the item's numeric range.");
            bits = unchecked((uint)(long)integer);
        }
        if (definition.RegisterCount == 1) return [Swap((ushort)bits, definition.SwapBytes)];
        var high = Swap((ushort)(bits >> 16), definition.SwapBytes);
        var low = Swap((ushort)bits, definition.SwapBytes);
        return definition.SwapWords ? [low, high] : [high, low];
    }

    private static ushort Swap(ushort value, bool swap) => swap ? (ushort)((value >> 8) | (value << 8)) : value;

    private static (double Value, ExactNumber Exact) ParseNumber(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Length == 2 || !text.AsSpan(2).ContainsAnyExcept("0123456789abcdefABCDEF"))
            {
                if (text.Length == 2) throw new ArgumentException("Enter a complete hexadecimal value.");
                var integer = BigInteger.Parse("0" + text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                return ((double)integer, new(integer, BigInteger.One));
            }
            throw new ArgumentException("Enter a valid hexadecimal value.");
        }
        var culture = CultureInfo.CurrentCulture;
        if (!double.TryParse(text, NumberStyles.Float, culture, out var number))
        {
            culture = CultureInfo.InvariantCulture;
            if (!double.TryParse(text, NumberStyles.Float, culture, out number))
                throw new ArgumentException("Enter a numeric value or one of the defined value names.");
        }
        var normalized = text.Replace(culture.NumberFormat.NumberDecimalSeparator, ".", StringComparison.Ordinal);
        var negativeSign = culture.NumberFormat.NegativeSign;
        var positiveSign = culture.NumberFormat.PositiveSign;
        if (negativeSign.Length > 0 && negativeSign != "-") normalized = normalized.Replace(negativeSign, "-", StringComparison.Ordinal);
        if (positiveSign.Length > 0 && positiveSign != "+") normalized = normalized.Replace(positiveSign, "+", StringComparison.Ordinal);
        return (number, ParseExact(normalized));
    }

    // Exact decimal fractions avoid silently truncating values such as 25.35 / 0.1.
    private static ExactNumber ParseExact(string text)
    {
        var exponentAt = text.IndexOfAny(['e', 'E']);
        var exponent = 0;
        if (exponentAt >= 0)
        {
            if (!int.TryParse(text.AsSpan(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent) || Math.Abs((long)exponent) > 1024)
                throw new ArgumentException("The numeric exponent is outside the supported range.");
            text = text[..exponentAt];
        }
        var negative = text.StartsWith('-');
        if (negative || text.StartsWith('+')) text = text[1..];
        var point = text.IndexOf('.');
        var decimals = point < 0 ? 0 : text.Length - point - 1;
        var digits = text.Replace(".", "", StringComparison.Ordinal);
        if (digits.Length == 0 || digits.Any(character => character is < '0' or > '9'))
            throw new ArgumentException("Enter a valid decimal number.");
        var numerator = BigInteger.Parse(digits, CultureInfo.InvariantCulture);
        if (negative) numerator = -numerator;
        var power = exponent - decimals;
        return power >= 0 ? new(numerator * BigInteger.Pow(10, power), BigInteger.One)
            : new(numerator, BigInteger.Pow(10, -power));
    }

    private static string FormatExact(ExactNumber number)
    {
        if (number.Numerator.IsZero) return "0";
        var digits = BigInteger.Abs(number.Numerator).ToString(CultureInfo.InvariantCulture);
        // ParseExact only creates denominators which are integral powers of ten.
        var power = -(number.Denominator.ToString(CultureInfo.InvariantCulture).Length - 1);
        var trimmed = digits.TrimEnd('0');
        power += digits.Length - trimmed.Length;
        digits = trimmed;
        var exponent = power + digits.Length - 1;
        string result;
        if (exponent is >= -6 and <= 20)
        {
            var point = digits.Length + power;
            result = point <= 0 ? "0." + new string('0', -point) + digits
                : point >= digits.Length ? digits + new string('0', point - digits.Length)
                : digits.Insert(point, ".");
        }
        else
            result = digits[..1] + (digits.Length > 1 ? "." + digits[1..] : "") + "E" +
                (exponent >= 0 ? "+" : "") + exponent.ToString(CultureInfo.InvariantCulture);
        var format = CultureInfo.CurrentCulture.NumberFormat;
        result = result.Replace(".", format.NumberDecimalSeparator, StringComparison.Ordinal);
        return number.Numerator.Sign < 0 ? format.NegativeSign + result : result;
    }

    private readonly record struct ExactNumber(BigInteger Numerator, BigInteger Denominator);
}
