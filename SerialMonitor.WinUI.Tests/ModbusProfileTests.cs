using System.Text.Json;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusProfileTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Name\":\"Legacy profile\",\"ProfileSchemaVersion\":1}")]
    [InlineData("{\"ModbusSettings\":null}")]
    public async Task LegacyProfilesWithoutModbusSettingsLoadSafeDefaults(string json)
    {
        using var temporary = new TemporaryProfile();
        await File.WriteAllTextAsync(temporary.Path, json);
        var service = new ProfileService();

        var loaded = await service.LoadAsync(temporary.Path, CancellationToken.None);

        AssertSettings(new ModbusMonitorSettings(), loaded.ModbusSettings);
        loaded.ModbusSettings.Validate();
    }

    [Fact]
    public async Task AllConfigurationFieldsSurviveTheActualProfileSaveAndLoadPath()
    {
        using var temporary = new TemporaryProfile();
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        var expected = NondefaultSettings();
        profile.ModbusSettings = expected.Clone();

        await service.SaveAsync(temporary.Path, profile, CancellationToken.None);
        var loaded = await service.LoadAsync(temporary.Path, CancellationToken.None);

        AssertSettings(expected, loaded.ModbusSettings);
        Assert.NotSame(profile.ModbusSettings, loaded.ModbusSettings);
        loaded.ModbusSettings.Validate();
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task SavedModbusJsonContainsOnlyConfigurationAndCannotRestoreRunningState()
    {
        using var temporary = new TemporaryProfile();
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.ModbusSettings = NondefaultSettings();
        await service.SaveAsync(temporary.Path, profile, CancellationToken.None);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(temporary.Path));
        var settings = document.RootElement.GetProperty(nameof(AppProfile.ModbusSettings));
        string[] expectedProperties =
        [
            nameof(ModbusMonitorSettings.UnitId), nameof(ModbusMonitorSettings.Function),
            nameof(ModbusMonitorSettings.Address), nameof(ModbusMonitorSettings.Quantity),
            nameof(ModbusMonitorSettings.IntervalMs), nameof(ModbusMonitorSettings.TimeoutMs),
            nameof(ModbusMonitorSettings.ValueType), nameof(ModbusMonitorSettings.Scale),
            nameof(ModbusMonitorSettings.SwapBytes), nameof(ModbusMonitorSettings.SwapWords)
        ];
        Assert.Equal(expectedProperties.Order(), settings.EnumerateObject().Select(property => property.Name).Order());
        Assert.False(settings.TryGetProperty(nameof(ModbusMonitorSettings.Request), out _));
        foreach (var runtimeProperty in new[] { "IsPolling", "IsBusy", "Values", "UpdatedAt", "WriteState", "State", "Generation" })
            Assert.False(settings.TryGetProperty(runtimeProperty, out _));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(65536, 1)]
    [InlineData(65535, 2)]
    [InlineData(int.MaxValue, 1)]
    public async Task InvalidSavedWireAddressesRemainVisibleAndCannotBecomeAnotherValidTarget(int address, int quantity)
    {
        using var temporary = new TemporaryProfile();
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.ModbusSettings = new ModbusMonitorSettings { Address = address, Quantity = quantity };

        await service.SaveAsync(temporary.Path, profile, CancellationToken.None);
        var loaded = await service.LoadAsync(temporary.Path, CancellationToken.None);

        Assert.Equal(address, loaded.ModbusSettings.Address);
        Assert.Equal(quantity, loaded.ModbusSettings.Quantity);
        Assert.Throws<ArgumentOutOfRangeException>(loaded.ModbusSettings.Validate);
        Assert.Contains("Modbus settings are invalid", service.LastError, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(temporary.Path));
        Assert.Equal(address, document.RootElement.GetProperty(nameof(AppProfile.ModbusSettings))
            .GetProperty(nameof(ModbusMonitorSettings.Address)).GetInt32());
    }

    [Theory]
    [InlineData(0, 3, 1)]
    [InlineData(248, 3, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 5, 1)]
    [InlineData(1, 3, 0)]
    [InlineData(1, 3, 126)]
    public async Task InvalidSavedUnitFunctionOrQuantityIsPreservedForExplicitCorrection(int unit, byte function, int quantity)
    {
        using var temporary = new TemporaryProfile();
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.ModbusSettings = new ModbusMonitorSettings { UnitId = unit, Function = function, Quantity = quantity };
        await service.SaveAsync(temporary.Path, profile, CancellationToken.None);

        var loaded = await service.LoadAsync(temporary.Path, CancellationToken.None);

        Assert.Equal(unit, loaded.ModbusSettings.UnitId);
        Assert.Equal(function, loaded.ModbusSettings.Function);
        Assert.Equal(quantity, loaded.ModbusSettings.Quantity);
        Assert.Throws<ArgumentOutOfRangeException>(loaded.ModbusSettings.Validate);
        Assert.Contains("Modbus settings are invalid", service.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SavingASequenceChangeDoesNotLoseModbusSettingsInThePersistenceCopy()
    {
        using var temporary = new TemporaryProfile();
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.ModbusSettings = NondefaultSettings();
        profile.CommandSequences =
        [
            new CommandSequence
            {
                Name = "Product check",
                Steps = [new CommandSequenceStep { CommandText = "status", DelayAfterMs = 250 }]
            }
        ];
        await service.SaveAsync(temporary.Path, profile, CancellationToken.None);
        var loaded = await service.LoadAsync(temporary.Path, CancellationToken.None);
        var expected = NondefaultSettings();
        expected.UnitId = 42;
        expected.Address = 8000;
        expected.IntervalMs = 60000;
        loaded.ModbusSettings = expected.Clone();
        loaded.CommandSequences[0].Steps[0].CommandText = "version";

        await service.SaveSequenceChangeAsync(temporary.Path, loaded, "Product check", CancellationToken.None);
        var reloaded = await service.LoadAsync(temporary.Path, CancellationToken.None);

        AssertSettings(expected, reloaded.ModbusSettings);
        Assert.Equal("version", Assert.Single(Assert.Single(reloaded.CommandSequences).Steps).CommandText);
        Assert.Null(service.LastError);
    }

    [Theory]
    [InlineData("1e999")]
    [InlineData("-1e999")]
    public async Task NonfiniteSavedDisplayScaleIsRepairedWithoutChangingTheWireTargetAndCanBeSavedAgain(string scaleJson)
    {
        using var temporary = new TemporaryProfile();
        var service = new ProfileService();
        await File.WriteAllTextAsync(temporary.Path, $$"""
            {
              "Name": "Overflowing display scale",
              "ModbusSettings": {
                "UnitId": 37,
                "Function": 3,
                "Address": 520,
                "Quantity": 8,
                "Scale": {{scaleJson}}
              }
            }
            """);

        var loaded = await service.LoadAsync(temporary.Path, CancellationToken.None);

        Assert.Equal(37, loaded.ModbusSettings.UnitId);
        Assert.Equal((byte)3, loaded.ModbusSettings.Function);
        Assert.Equal(520, loaded.ModbusSettings.Address);
        Assert.Equal(8, loaded.ModbusSettings.Quantity);
        Assert.Equal(1d, loaded.ModbusSettings.Scale);
        Assert.Contains("Modbus", service.LastError, StringComparison.OrdinalIgnoreCase);
        loaded.ModbusSettings.Validate();

        await service.SaveAsync(temporary.Path, loaded, CancellationToken.None);
        var reloaded = await service.LoadAsync(temporary.Path, CancellationToken.None);

        AssertSettings(loaded.ModbusSettings, reloaded.ModbusSettings);
        Assert.Null(service.LastError);
    }

    private static ModbusMonitorSettings NondefaultSettings() => new()
    {
        UnitId = 37, Function = 4, Address = 520, Quantity = 8,
        IntervalMs = 2500, TimeoutMs = 4000, ValueType = ModbusValueType.Float32,
        Scale = -0.125, SwapBytes = true, SwapWords = true
    };

    private static void AssertSettings(ModbusMonitorSettings expected, ModbusMonitorSettings actual)
    {
        Assert.Equal(expected.UnitId, actual.UnitId);
        Assert.Equal(expected.Function, actual.Function);
        Assert.Equal(expected.Address, actual.Address);
        Assert.Equal(expected.Quantity, actual.Quantity);
        Assert.Equal(expected.IntervalMs, actual.IntervalMs);
        Assert.Equal(expected.TimeoutMs, actual.TimeoutMs);
        Assert.Equal(expected.ValueType, actual.ValueType);
        Assert.Equal(expected.Scale, actual.Scale);
        Assert.Equal(expected.SwapBytes, actual.SwapBytes);
        Assert.Equal(expected.SwapWords, actual.SwapWords);
    }

    private sealed class TemporaryProfile : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SerialMonitor.Tests", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "profile.json");
        public TemporaryProfile() => Directory.CreateDirectory(_directory);
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
