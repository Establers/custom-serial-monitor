using System.Globalization;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;

namespace SerialMonitor.Core.Tests;

public sealed class ModbusItemCodecTests
{
    [Theory]
    [InlineData(ModbusValueType.UInt16, "65535", 65535d, "FFFF")]
    [InlineData(ModbusValueType.Int16, "-32768", -32768d, "8000")]
    [InlineData(ModbusValueType.UInt32, "4294967295", 4294967295d, "FFFFFFFF")]
    [InlineData(ModbusValueType.Int32, "-2147483648", -2147483648d, "80000000")]
    [InlineData(ModbusValueType.Int32, "2147483647", 2147483647d, "7FFFFFFF")]
    [InlineData(ModbusValueType.Float32, "1.5", 1.5d, "3FC00000")]
    public void AllSupportedTypesEncodeAndDecodeWithoutLosingIntegerBits(ModbusValueType type, string text, double expected, string wire)
    {
        var definition = new ModbusItemDefinition { ValueType = type };
        var registers = ModbusItemCodec.Encode(text, definition);
        Assert.Equal(wire, string.Concat(registers.Select(value => value.ToString("X4", CultureInfo.InvariantCulture))));
        var result = ModbusItemCodec.Decode(registers, definition);
        Assert.Equal(expected, result.RawValue);
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData(false, false, "41C80000")]
    [InlineData(true, false, "C8410000")]
    [InlineData(false, true, "000041C8")]
    [InlineData(true, true, "0000C841")]
    public void EncodeReversesEachByteAndWordOrder(bool bytes, bool words, string expected)
    {
        var definition = new ModbusItemDefinition { ValueType = ModbusValueType.Float32, SwapBytes = bytes, SwapWords = words };
        var registers = ModbusItemCodec.Encode("25", definition);
        Assert.Equal(expected, string.Concat(registers.Select(value => value.ToString("X4", CultureInfo.InvariantCulture))));
        Assert.Equal(25, ModbusItemCodec.Decode(registers, definition).Value);
    }

    [Theory]
    [InlineData("25.3", 0.1, 253)]
    [InlineData("0.3", 0.1, 3)]
    [InlineData("2.53e1", 0.1, 253)]
    [InlineData("-25.3", -0.1, 253)]
    [InlineData("0xFF", 0.5, 510)]
    public void EngineeringValuesUseInverseScaleWithoutBinaryRoundoff(string text, double scale, ushort expected)
    {
        Assert.Equal(new[] { expected }, ModbusItemCodec.Encode(text, new() { Scale = scale }));
    }

    [Theory]
    [InlineData("25.35", 0.1)]
    [InlineData("1", 3)]
    [InlineData("65535.00000000000000000001", 1)]
    [InlineData("0.000000000000000000000000000000001", 1)]
    [InlineData("1e-400", 1)]
    public void IntegerWritesNeverSilentlyTruncateFractionalRawValues(string value, double scale)
    {
        Assert.Throws<ArgumentException>(() => ModbusItemCodec.Encode(value, new() { Scale = scale }));
    }

    [Theory]
    [InlineData(ModbusValueType.UInt16, "-1")]
    [InlineData(ModbusValueType.UInt16, "65536")]
    [InlineData(ModbusValueType.Int16, "32768")]
    [InlineData(ModbusValueType.Int16, "-32769")]
    [InlineData(ModbusValueType.UInt32, "4294967296")]
    [InlineData(ModbusValueType.Int32, "2147483648")]
    public void IntegerWritesRejectOverflow(ModbusValueType type, string value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusItemCodec.Encode(value, new() { ValueType = type }));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e40")]
    [InlineData("1e-50")]
    [InlineData("1e-400")]
    [InlineData("0x")]
    [InlineData("0xFG")]
    [InlineData("1,000")]
    public void FloatWritesRejectInvalidNonfiniteOverflowAndUnderflow(string value)
    {
        using var culture = new CultureScope("en-US");
        Assert.ThrowsAny<ArgumentException>(() => ModbusItemCodec.Encode(value, new() { ValueType = ModbusValueType.Float32 }));
    }

    [Fact]
    public void FloatWritesPermitOrdinaryIeeeRoundingAndPreserveNegativeZero()
    {
        var definition = new ModbusItemDefinition { ValueType = ModbusValueType.Float32 };
        var rounded = ModbusItemCodec.Decode(ModbusItemCodec.Encode("0.1", definition), definition);
        Assert.Equal((double)0.1f, rounded.RawValue);
        Assert.Equal(new ushort[] { 0x8000, 0 }, ModbusItemCodec.Encode("-0", definition));
        Assert.Throws<ArgumentException>(() => ModbusItemCodec.Encode("1e-300", new() { ValueType = ModbusValueType.Float32, Scale = 1e300 }));
    }

    [Fact]
    public void CurrentDecimalSeparatorAndInvariantInputBothWorkWithoutThousands()
    {
        using var culture = new CultureScope("de-DE");
        var definition = new ModbusItemDefinition { Scale = 0.1 };
        Assert.Equal(new ushort[] { 253 }, ModbusItemCodec.Encode("25,3", definition));
        Assert.Equal(new ushort[] { 253 }, ModbusItemCodec.Encode("25.3", definition));
        Assert.Throws<ArgumentException>(() => ModbusItemCodec.Encode("1.000,0", definition));
    }

    [Fact]
    public void NamedValuesRepresentUnscaledDecodedNumbers()
    {
        var definition = new ModbusItemDefinition { Scale = 10, ValueNames = [new() { Value = 2, Name = "Automatic" }] };
        Assert.Equal(new ushort[] { 2 }, ModbusItemCodec.Encode("automatic", definition));
        Assert.Equal(new ushort[] { 2 }, ModbusItemCodec.Encode("20", definition));
        Assert.Equal(20, ModbusItemCodec.Decode([2], definition).Value);
    }

    [Fact]
    public void NumericInputTakesPrecedenceOverNumericDisplayLabels()
    {
        var definition = new ModbusItemDefinition { ValueNames = [new() { Value = 2, Name = "1" }, new() { Value = 3, Name = "Automatic" }] };
        definition.Validate();
        Assert.Equal(new ushort[] { 1 }, ModbusItemCodec.Encode("1", definition));
        Assert.Equal(new ushort[] { 3 }, ModbusItemCodec.Encode("Automatic", definition));
    }

    [Theory]
    [InlineData(ModbusValueType.UInt16, 0.1, "253", "25.3")]
    [InlineData(ModbusValueType.Int16, 0.1, "-253", "-25.3")]
    [InlineData(ModbusValueType.UInt16, -0.1, "253", "-25.3")]
    [InlineData(ModbusValueType.UInt16, 0.3333333333333333, "3", "0.9999999999999999")]
    public void IntegerEngineeringTextUsesExactDecimalScaleAndRoundtrips(ModbusValueType type, double scale, string raw, string expected)
    {
        using var culture = new CultureScope("en-US");
        var definition = new ModbusItemDefinition { ValueType = type, Scale = scale };
        var registers = ModbusItemCodec.Encode(raw, new() { ValueType = type });
        var text = ModbusItemCodec.FormatEngineeringValue(registers, definition);
        Assert.Equal(expected, text);
        Assert.Equal(registers, ModbusItemCodec.Encode(text, definition));
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("sv-SE")]
    public void EngineeringTextAndInputRoundtripInCurrentCulture(string cultureName)
    {
        using var culture = new CultureScope(cultureName);
        var definition = new ModbusItemDefinition { ValueType = ModbusValueType.Int16, Scale = 0.1 };
        var registers = new ushort[] { unchecked((ushort)-253) };
        var text = ModbusItemCodec.FormatEngineeringValue(registers, definition);
        Assert.Contains(CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator, text);
        Assert.Equal(registers, ModbusItemCodec.Encode(text, definition));
    }

    [Theory]
    [InlineData(double.Epsilon)]
    [InlineData(1e-300)]
    [InlineData(double.MaxValue)]
    [InlineData(-double.MaxValue)]
    public void ExtremeScaleEngineeringTextStaysBoundedAndRoundtrips(double scale)
    {
        var definition = new ModbusItemDefinition { ValueType = ModbusValueType.UInt32, Scale = scale };
        var registers = new ushort[] { ushort.MaxValue, ushort.MaxValue };
        var text = ModbusItemCodec.FormatEngineeringValue(registers, definition);
        Assert.True(text.Length < 80);
        Assert.Equal(registers, ModbusItemCodec.Encode(text, definition));
    }

    [Fact]
    public void FloatEngineeringTextUsesFloatPrecisionAndDoesNotThrowOnScaledOverflow()
    {
        using var culture = new CultureScope("en-US");
        var definition = new ModbusItemDefinition { ValueType = ModbusValueType.Float32 };
        var registers = ModbusItemCodec.Encode("0.1", definition);
        Assert.Equal("0.100000001", ModbusItemCodec.FormatEngineeringValue(registers, definition));
        Assert.Equal(registers, ModbusItemCodec.Encode(ModbusItemCodec.FormatEngineeringValue(registers, definition), definition));
        definition.Scale = double.MaxValue;
        Assert.NotEmpty(ModbusItemCodec.FormatEngineeringValue([0x7F7F, 0xFFFF], definition));
    }

    [Fact]
    public void ZeroMultiplierCanDisplayButCannotBeWritten()
    {
        var definition = new ModbusItemDefinition { Scale = 0 };
        Assert.Equal(new ModbusItemValue(123, 0), ModbusItemCodec.Decode([123], definition));
        Assert.Throws<ArgumentException>(() => ModbusItemCodec.Encode("0", definition));
    }

    [Fact]
    public void CoilInputsAreBoundedToOneBitEvenWithValueNames()
    {
        var definition = new ModbusItemDefinition { ValueNames = [new() { Value = 1, Name = "Enabled" }] };
        Assert.Equal(new ushort[] { 1 }, ModbusItemCodec.Encode("Enabled", definition, isCoil: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusItemCodec.Encode("2", definition, isCoil: true));
        definition.SwapBytes = true;
        Assert.Throws<ArgumentException>(() => ModbusItemCodec.Encode("1", definition, isCoil: true));
    }

    [Fact]
    public void DefinitionsValidateCompleteRangeAndDeepCopyLabels()
    {
        var original = new ModbusItemDefinition { Address = 65534, ValueType = ModbusValueType.UInt32, ValueNames = [new() { Value = uint.MaxValue, Name = "Maximum" }] };
        original.Validate();
        var copy = original.Clone();
        copy.ValueNames[0].Name = "Changed";
        copy.ValueNames.Add(new() { Value = 1, Name = "One" });
        Assert.Equal("Maximum", Assert.Single(original.ValueNames).Name);
        original.Address = 65535;
        Assert.Throws<ArgumentOutOfRangeException>(() => original.Validate());
        Assert.Throws<ArgumentException>(() => ModbusItemCodec.Decode([0], copy));
    }

    [Fact]
    public void InvalidImportedDefinitionsArePreservedUntilExplicitValidation()
    {
        var definition = new ModbusItemDefinition { Name = null!, ValueNames = null! };
        var copy = definition.Clone();
        Assert.Null(copy.Name);
        Assert.Null(copy.ValueNames);
        Assert.Throws<ArgumentException>(() => copy.Validate());
        definition.Name = "";
        definition.ValueNames = [null!];
        copy = definition.Clone();
        Assert.Null(Assert.Single(copy.ValueNames));
        Assert.Throws<ArgumentException>(() => copy.Validate());
    }

    [Fact]
    public void InvalidOrAmbiguousValueNamesCannotBeUsedForWrites()
    {
        foreach (var names in new List<ModbusValueName>[]
        {
            [new() { Value = 0, Name = "Off" }, new() { Value = 1, Name = " OFF " }],
            [new() { Value = 0, Name = "Off" }, new() { Value = 0, Name = "Zero" }],
            [new() { Value = -1, Name = "Negative" }],
            [new() { Value = 1, Name = "" }]
        })
            Assert.Throws<ArgumentException>(() => new ModbusItemDefinition { ValueNames = names }.Validate());
        Assert.Throws<ArgumentException>(() => new ModbusItemDefinition { ValueType = ModbusValueType.Float32, ValueNames = [new() { Value = 16777217, Name = "Inexact" }] }.Validate());
    }

    [Fact]
    public void DefinitionsBoundTextAndMapMemoryAndRejectUnknownEnums()
    {
        Assert.Throws<ArgumentException>(() => new ModbusItemDefinition { Name = new('a', 129) }.Validate());
        Assert.Throws<ArgumentException>(() => new ModbusItemDefinition { Unit = new('a', 33) }.Validate());
        Assert.Throws<ArgumentException>(() => new ModbusItemDefinition { ValueNames = Enumerable.Range(0, 129).Select(value => new ModbusValueName { Value = value, Name = "Mode " + value }).ToList() }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModbusItemDefinition { ValueType = (ModbusValueType)99 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModbusItemDefinition { Access = (ModbusItemAccess)99 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModbusItemDefinition { Scale = double.PositiveInfinity }.Validate());
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;
        public CultureScope(string culture) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}
