using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Tests;

public sealed class NucleoModbusCommandPresetTests
{
    private const string IdPrefix = "nucleo-f103rb-";
    private static readonly string[] CommandIds =
    [
        "led-on", "led-off", "led-cycle", "ram-uint16", "ram-int16", "ram-scaled-int16",
        "ram-uint32", "ram-float32", "ram-int32", "ram-sequence"
    ];

    // These expected words come from the firmware's big-endian register layout,
    // not the production item codec or the JSON being checked.
    private static readonly RamExpectation[] RamCases =
    [
        new("ram-uint16", 0x1010, ModbusValueType.UInt16, 1, "123", [0x007B]),
        new("ram-int16", 0x1011, ModbusValueType.Int16, 1, "-123", [0xFF85]),
        new("ram-scaled-int16", 0x1012, ModbusValueType.Int16, 0.1, "25.3", [0x00FD]),
        new("ram-uint32", 0x1014, ModbusValueType.UInt32, 1, "305419896", [0x1234, 0x5678]),
        new("ram-float32", 0x1016, ModbusValueType.Float32, 1, "1.5", [0x3FC0, 0x0000]),
        new("ram-int32", 0x1018, ModbusValueType.Int32, 1, "-123456789", [0xF8A4, 0x32EB])
    ];

    [Fact]
    public void StandalonePresetLoadsThroughProductionConverterWithStableSelection()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(PresetPath()));
        Assert.Equal(new[] { "Commands", "SelectedCommandId" }, document.RootElement.EnumerateObject().Select(property => property.Name));
        foreach (var command in document.RootElement.GetProperty("Commands").EnumerateArray())
        foreach (var step in command.GetProperty("Steps").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, step.GetProperty("Item").GetProperty("ValueType").ValueKind);
            Assert.Equal(JsonValueKind.String, step.GetProperty("Item").GetProperty("Access").ValueKind);
            Assert.Equal(JsonValueKind.String, step.GetProperty("Verification").GetProperty("Mode").ValueKind);
        }
        var settings = Load();

        settings.Validate();
        Assert.Equal(CommandIds.Select(id => IdPrefix + id), settings.Commands.Select(command => command.Id));
        Assert.Equal(settings.Commands[0].Id, settings.SelectedCommandId);
        Assert.All(settings.Commands, command => Assert.StartsWith("NUCLEO · ", command.Name));

        // Match profile enum serialization too: a preset must remain valid when
        // placed in ModbusCommands and saved/reloaded as an ordinary profile.
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        var profile = new AppProfile { ModbusCommands = settings.Clone() };
        var restored = JsonSerializer.Deserialize<AppProfile>(JsonSerializer.Serialize(profile, options), options)!.ModbusCommands!;
        restored.Validate();
        Assert.Equal(settings.SelectedCommandId, restored.SelectedCommandId);
        Assert.Equal(JsonSerializer.Serialize(settings, options), JsonSerializer.Serialize(restored, options));
    }

    [Fact]
    public void EveryStepTargetsOnlyReviewedRamOrLedWithExplicitVerification()
    {
        var settings = Load();
        settings.Validate();
        Assert.Equal(13, settings.Commands.Sum(command => command.Steps.Count));
        var allowedRamStarts = new[] { 0x1010, 0x1011, 0x1012, 0x1014, 0x1016, 0x1018 };

        foreach (var command in settings.Commands)
        foreach (var step in command.Steps)
        {
            Assert.Equal(1, step.UnitId);
            Assert.Equal(1000, step.TimeoutMs);
            Assert.Equal(ModbusItemAccess.ReadWrite, step.Item.Access);
            Assert.False(step.Item.SwapBytes);
            Assert.False(step.Item.SwapWords);
            Assert.Empty(step.Item.ValueNames);
            Assert.Equal(3000, step.Verification.TimeoutMs);
            Assert.Equal(100, step.Verification.IntervalMs);

            if (step.Function == 1)
            {
                Assert.StartsWith(IdPrefix + "led-", command.Id);
                Assert.Equal(0x1000, step.Item.Address);
                Assert.Equal(ModbusValueType.UInt16, step.Item.ValueType);
                Assert.Equal(1, step.Item.Scale);
                Assert.Equal(ModbusCommandVerificationMode.Status, step.Verification.Mode);
                Assert.Equal((ushort)1, step.Verification.Mask);
                if (step.Value == "1")
                {
                    Assert.Equal((byte)4, step.Verification.Function);
                    Assert.Equal(0x1007, step.Verification.Address);
                    Assert.Equal((ushort)1, step.Verification.ExpectedValue);
                }
                else
                {
                    Assert.Equal("0", step.Value);
                    Assert.Equal((byte)2, step.Verification.Function);
                    Assert.Equal(0x1001, step.Verification.Address);
                    Assert.Equal((ushort)0, step.Verification.ExpectedValue);
                }
            }
            else
            {
                Assert.StartsWith(IdPrefix + "ram-", command.Id);
                Assert.Equal((byte)3, step.Function);
                Assert.Contains(step.Item.Address, allowedRamStarts);
                Assert.InRange(step.Item.Address + step.Item.RegisterCount - 1, 0x1010, 0x101F);
                Assert.Equal(ModbusCommandVerificationMode.ReadBack, step.Verification.Mode);
                Assert.Equal((byte)3, step.Verification.Function);
                Assert.Equal(step.Item.Address, step.Verification.Address);
                Assert.Equal(ushort.MaxValue, step.Verification.Mask);
                Assert.Equal((ushort)0, step.Verification.ExpectedValue);
            }
        }
    }

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    public void TypedRamValuesEncodeExpectedWordsAndIndependentWriteFrames(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var settings = Load();
            settings.Validate();
            foreach (var expected in RamCases)
            {
                var step = Assert.Single(Command(settings, expected.Id).Steps);
                Assert.Equal(expected.Address, step.Item.Address);
                Assert.Equal(expected.Type, step.Item.ValueType);
                Assert.Equal(expected.Scale, step.Item.Scale);
                Assert.Equal(expected.Value, step.Value);
                Assert.Equal(expected.Words, step.EncodeValues());
                Assert.Equal(WriteFrame(expected.Address, expected.Words), step.BuildPacket());
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void LedCommandsAndCycleUseExactlyTheReviewedOnOffOrder()
    {
        var settings = Load();
        var on = Assert.Single(Command(settings, "led-on").Steps);
        var off = Assert.Single(Command(settings, "led-off").Steps);
        Assert.Equal(Frame(1, 5, 0x10, 0x00, 0xFF, 0x00), on.BuildPacket());
        Assert.Equal(Frame(1, 5, 0x10, 0x00, 0x00, 0x00), off.BuildPacket());
        var cycle = Command(settings, "led-cycle").Steps;
        Assert.Equal(2, cycle.Count);
        Assert.Equal(new[] { "1", "0" }, cycle.Select(step => step.Value));
        Assert.Equal(on.BuildPacket(), cycle[0].BuildPacket());
        Assert.Equal(off.BuildPacket(), cycle[1].BuildPacket());
        Assert.Equal((byte)4, cycle[0].Verification.Function);
        Assert.Equal(0x1007, cycle[0].Verification.Address);
        Assert.Equal((ushort)1, cycle[0].Verification.ExpectedValue);
        Assert.Equal((byte)2, cycle[1].Verification.Function);
        Assert.Equal(0x1001, cycle[1].Verification.Address);
        Assert.Equal((ushort)0, cycle[1].Verification.ExpectedValue);
    }

    [Fact]
    public void RamSequenceUsesSameScratchWordInSixSevenFifteenOrder()
    {
        var steps = Command(Load(), "ram-sequence").Steps;
        Assert.Equal(new[] { "6", "7", "15" }, steps.Select(step => step.Value));
        var expected = new ushort[] { 6, 7, 15 };
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(0x1010, steps[index].Item.Address);
            Assert.Equal(ModbusValueType.UInt16, steps[index].Item.ValueType);
            Assert.Equal(1, steps[index].Item.Scale);
            Assert.Equal(ModbusCommandVerificationMode.ReadBack, steps[index].Verification.Mode);
            Assert.Equal(new[] { expected[index] }, steps[index].EncodeValues());
            Assert.Equal(WriteFrame(0x1010, [expected[index]]), steps[index].BuildPacket());
        }
    }

    private static ModbusCommandDefinition Command(ModbusCommandSettings settings, string suffix) =>
        Assert.Single(settings.Commands, command => command.Id == IdPrefix + suffix);

    private static ModbusCommandSettings Load() =>
        JsonSerializer.Deserialize<ModbusCommandSettings>(File.ReadAllText(PresetPath()))
        ?? throw new InvalidDataException("The NUCLEO command preset is missing.");

    private static string PresetPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "firmware", "nucleo-f103rb-serial-lab", "presets", "modbus-commands.json");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("The NUCLEO command preset source file was not found.");
    }

    private static byte[] WriteFrame(int address, ushort[] words)
    {
        var bytes = new List<byte> { 1, words.Length == 1 ? (byte)6 : (byte)16, (byte)(address >> 8), (byte)address };
        if (words.Length > 1)
            bytes.AddRange([0, (byte)words.Length, (byte)(words.Length * 2)]);
        foreach (var word in words) bytes.AddRange([(byte)(word >> 8), (byte)word]);
        return Frame(bytes.ToArray());
    }

    // Independent CRC oracle; no production request builder or decoder is used.
    private static byte[] Frame(params byte[] body)
    {
        ushort crc = 0xFFFF;
        foreach (var value in body)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
        }
        return [.. body, (byte)crc, (byte)(crc >> 8)];
    }

    private sealed record RamExpectation(string Id, int Address, ModbusValueType Type, double Scale, string Value, ushort[] Words);
}
