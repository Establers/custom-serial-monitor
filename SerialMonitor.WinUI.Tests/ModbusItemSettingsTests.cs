using System.Text.Json;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusItemSettingsTests
{
    [Fact]
    public void SparseMixedDefinitionsValidateWithoutChangingLegacyWireTarget()
    {
        var settings = Settings();
        settings.Items =
        [
            Item(10, ModbusValueType.Int16),
            Item(12, ModbusValueType.Float32),
            Item(15, ModbusValueType.UInt32)
        ];

        settings.Validate();

        Assert.Equal(new ModbusReadRequest(1, 3, 10, 8), settings.Request);
        Assert.Equal(3, settings.Items.Count);
    }

    [Theory]
    [InlineData(9, ModbusValueType.UInt16)]
    [InlineData(18, ModbusValueType.UInt16)]
    [InlineData(17, ModbusValueType.Float32)]
    [InlineData(65535, ModbusValueType.UInt32)]
    public void AnItemCannotBorrowDataOutsideItsReadArea(int address, ModbusValueType type)
    {
        var settings = Settings();
        settings.Items = [Item(address, type)];
        Assert.ThrowsAny<ArgumentException>(settings.Validate);
    }

    [Fact]
    public void UnsortedNonoverlappingItemsAreAcceptedButOverlappingItemsAreRejected()
    {
        var settings = Settings();
        settings.Items = [Item(14, ModbusValueType.UInt16), Item(10, ModbusValueType.Float32)];
        settings.Validate();
        settings.Items.Add(Item(11, ModbusValueType.UInt16));
        Assert.Throws<ArgumentException>(settings.Validate);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void InputAreaCannotDeclareWritableItem(byte function)
    {
        var settings = Settings();
        settings.Function = function;
        settings.Items = [Item(10, ModbusValueType.UInt16)];
        Assert.Throws<ArgumentException>(settings.Validate);
        settings.Items[0].Access = ModbusItemAccess.ReadOnly;
        settings.Validate();
        Assert.Throws<InvalidOperationException>(() => settings.ValidateWrite(10, 1));
    }

    [Fact]
    public void WritesCanCoverUndefinedAddressesButCannotTouchProtectedItems()
    {
        var settings = Settings();
        var protectedItem = Item(12, ModbusValueType.UInt16);
        protectedItem.Access = ModbusItemAccess.ReadOnly;
        settings.Items = [protectedItem];

        settings.ValidateWrite(10, 2);
        settings.ValidateWrite(13, 5);
        Assert.Throws<InvalidOperationException>(() => settings.ValidateWrite(12, 1));
        Assert.Throws<InvalidOperationException>(() => settings.ValidateWrite(10, 8));
    }

    [Theory]
    [InlineData(12, 1)]
    [InlineData(13, 1)]
    [InlineData(10, 3)]
    [InlineData(13, 5)]
    public void RawWritesCannotSplitADefinedMultiwordItem(int address, int count)
    {
        var settings = Settings();
        settings.Items = [Item(12, ModbusValueType.Float32)];
        Assert.Throws<ArgumentException>(() => settings.ValidateWrite(address, count));
        settings.ValidateWrite(12, 2);
        settings.ValidateWrite(10, 8);
    }

    [Theory]
    [InlineData(9, 1)]
    [InlineData(10, 0)]
    [InlineData(17, 2)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void InvalidWriteRangeCannotOverflowIntoTheMonitoredArea(int address, int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Settings().ValidateWrite(address, count));
    }

    [Fact]
    public void CloningSeparatesItemsAndValueNameMaps()
    {
        var settings = Settings();
        settings.Items = [Item(10, ModbusValueType.UInt16)];
        settings.Items[0].ValueNames = [new() { Value = 1, Name = "Automatic" }];

        var copy = settings.Clone();
        copy.Items[0].Name = "Other";
        copy.Items[0].ValueNames[0].Name = "Manual";
        copy.Items.Add(Item(11, ModbusValueType.UInt16));

        Assert.NotSame(settings.Items, copy.Items);
        Assert.Single(settings.Items);
        Assert.Equal("Item 10", settings.Items[0].Name);
        Assert.Equal("Automatic", settings.Items[0].ValueNames[0].Name);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("{}")]
    [InlineData("[\"bad\"]")]
    [InlineData("[{\"Address\":10,\"ValueType\":\"FutureType\"}]")]
    [InlineData("[{\"Address\":10,\"Access\":\"FutureAccess\"}]")]
    [InlineData("[{\"Address\":10,\"SwapBytes\":\"false\"}]")]
    [InlineData("[{\"Name\":\"Missing address\"}]")]
    [InlineData("[{\"Address\":10,\"ValueNames\":[{\"Name\":\"Missing value\"}]}]")]
    public void MalformedMetadataStaysInvalidAcrossCloneAndSerialization(string itemsJson)
    {
        var settings = JsonSerializer.Deserialize<ModbusMonitorSettings>("{\"Address\":10,\"Quantity\":8,\"Items\":" + itemsJson + "}")!;
        Assert.ThrowsAny<ArgumentException>(settings.Validate);
        var clone = settings.Clone();
        Assert.ThrowsAny<ArgumentException>(clone.Validate);
        var reloaded = JsonSerializer.Deserialize<ModbusMonitorSettings>(JsonSerializer.Serialize(clone))!;
        Assert.ThrowsAny<ArgumentException>(reloaded.Validate);
    }

    [Theory]
    [InlineData("{\"Name\":\"Missing value\"}")]
    [InlineData("{\"Value\":\"wrong\",\"Name\":\"Malformed value\"}")]
    [InlineData("{\"Value\":1.5,\"Name\":\"Fractional value\"}")]
    [InlineData("{\"Value\":9223372036854775808,\"Name\":\"Overflow value\"}")]
    public void CorrectingItemTypeCannotTurnAnInvalidValueNameKeyIntoZero(string entryJson)
    {
        var settings = JsonSerializer.Deserialize<ModbusMonitorSettings>(
            "{\"Address\":10,\"Quantity\":8,\"Items\":[{\"Address\":10,\"ValueNames\":[" + entryJson + "]}]}")!;
        var item = Assert.Single(settings.Items);
        Assert.Null(Assert.Single(item.ValueNames));
        item.ValueType = ModbusValueType.UInt16;
        Assert.ThrowsAny<ArgumentException>(settings.Validate);

        var reloaded = JsonSerializer.Deserialize<ModbusMonitorSettings>(JsonSerializer.Serialize(settings.Clone()))!;
        Assert.Null(Assert.Single(Assert.Single(reloaded.Items).ValueNames));
        Assert.ThrowsAny<ArgumentException>(reloaded.Validate);
    }

    [Fact]
    public async Task ItemConfigurationSurvivesActualProfileSaveLoadAndPersistenceCopy()
    {
        using var temporary = new TemporaryProfile();
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.ModbusSettings = Settings();
        var item = Item(12, ModbusValueType.Int32);
        item.Name = "Motor reference";
        item.Scale = 0.1;
        item.Unit = "rpm";
        item.SwapBytes = true;
        item.SwapWords = true;
        item.ValueNames = [new() { Value = -1, Name = "Reverse" }];
        profile.ModbusSettings.Items = [item];
        profile.CommandSequences = [new() { Name = "Check", Steps = [new() { CommandText = "status" }] }];

        await service.SaveAsync(temporary.Path, profile, default);
        var loaded = await service.LoadAsync(temporary.Path, default);
        loaded.CommandSequences[0].Steps[0].CommandText = "version";
        await service.SaveSequenceChangeAsync(temporary.Path, loaded, "Check", default);
        var reloaded = await service.LoadAsync(temporary.Path, default);

        reloaded.ModbusSettings.Validate();
        var actual = Assert.Single(reloaded.ModbusSettings.Items);
        Assert.Equal(item.Address, actual.Address);
        Assert.Equal(item.Name, actual.Name);
        Assert.Equal(item.ValueType, actual.ValueType);
        Assert.Equal(item.Scale, actual.Scale);
        Assert.Equal(item.Unit, actual.Unit);
        Assert.Equal(item.SwapBytes, actual.SwapBytes);
        Assert.Equal(item.SwapWords, actual.SwapWords);
        Assert.Equal(item.Access, actual.Access);
        Assert.Equal(-1, Assert.Single(actual.ValueNames).Value);
        Assert.Equal("Reverse", actual.ValueNames[0].Name);
        Assert.Null(service.LastError);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(temporary.Path));
        var stored = json.RootElement.GetProperty("ModbusSettings").GetProperty("Items")[0];
        Assert.False(stored.TryGetProperty("RegisterCount", out _));
    }

    [Theory]
    [InlineData("\"FutureType\"", "\"ReadOnly\"", "1")]
    [InlineData("\"UInt16\"", "\"FutureAccess\"", "1")]
    [InlineData("\"UInt16\"", "\"ReadOnly\"", "1e999")]
    [InlineData("\"UInt16\"", "\"ReadOnly\"", "-1e999")]
    public async Task InvalidItemMetadataNeverDiscardsUnrelatedProfileAndCanBeSavedAgain(string valueType, string access, string scale)
    {
        using var temporary = new TemporaryProfile();
        await File.WriteAllTextAsync(temporary.Path, $$"""
            {
              "Name":"Keep this profile",
              "ModbusSettings": {
                "UnitId":37,"Address":10,"Quantity":8,
                "Items":[{"Address":12,"Name":"Protected","ValueType":{{valueType}},"Access":{{access}},"Scale":{{scale}}}]
              },
              "SavedCommands":[{"Name":"Keep this command","CommandText":"status"}]
            }
            """);
        var service = new ProfileService();

        var loaded = await service.LoadAsync(temporary.Path, default);

        Assert.Equal("Keep this profile", loaded.Name);
        Assert.Equal(37, loaded.ModbusSettings.UnitId);
        Assert.Equal(12, Assert.Single(loaded.ModbusSettings.Items).Address);
        Assert.ThrowsAny<ArgumentException>(loaded.ModbusSettings.Validate);
        Assert.Contains("Modbus settings are invalid", service.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Keep this command", Assert.Single(loaded.SavedCommands).Name);
        await service.SaveAsync(temporary.Path, loaded, default);
        var reloaded = await service.LoadAsync(temporary.Path, default);
        Assert.Equal("Keep this profile", reloaded.Name);
        Assert.Equal(37, reloaded.ModbusSettings.UnitId);
        Assert.Equal(12, Assert.Single(reloaded.ModbusSettings.Items).Address);
        Assert.ThrowsAny<ArgumentException>(reloaded.ModbusSettings.Validate);
    }

    private static ModbusMonitorSettings Settings() => new() { Address = 10, Quantity = 8 };
    private static ModbusItemDefinition Item(int address, ModbusValueType type) => new()
    {
        Address = address, Name = $"Item {address}", ValueType = type, Access = ModbusItemAccess.ReadWrite
    };

    private sealed class TemporaryProfile : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SerialMonitor.Tests", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "profile.json");
        public TemporaryProfile() => Directory.CreateDirectory(_directory);
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
