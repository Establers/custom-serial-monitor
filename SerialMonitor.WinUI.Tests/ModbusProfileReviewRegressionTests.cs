using System.Text.Json;
using System.Text.Json.Nodes;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusProfileReviewRegressionTests
{
    [Theory]
    [InlineData("\"Float64\"")]
    [InlineData("\"not-a-format\"")]
    [InlineData("\"Int16, UInt32\"")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("1.5")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"format\":\"Float64\"}")]
    public async Task UnsupportedModbusDisplayTypeDoesNotResetOtherProfileSettingsOrWireTarget(string token)
    {
        using var temporary = new TemporaryProfile();
        var profiles = new ProfileService();
        var original = profiles.CreateDefaultProfile();
        original.Name = "Preserve existing product";
        original.SerialSettings.BaudRate = 19200;
        original.SavedCommands = [new TxCommand { Name = "Product status", CommandText = "status" }];
        original.ModbusSettings = new ModbusMonitorSettings { UnitId = 42, Address = 800, Quantity = 4 };
        var json = JsonNode.Parse(JsonSerializer.Serialize(original))!;
        json[nameof(AppProfile.ModbusSettings)]![nameof(ModbusMonitorSettings.ValueType)] = JsonNode.Parse(token);
        await File.WriteAllTextAsync(temporary.Path, json.ToJsonString());

        var loaded = await profiles.LoadAsync(temporary.Path, default);

        Assert.Equal(original.Name, loaded.Name);
        Assert.Equal(19200, loaded.SerialSettings.BaudRate);
        Assert.Equal("status", Assert.Single(loaded.SavedCommands).CommandText);
        Assert.Equal(42, loaded.ModbusSettings.UnitId);
        Assert.Equal(800, loaded.ModbusSettings.Address);
        Assert.Equal(4, loaded.ModbusSettings.Quantity);
        Assert.Equal((ModbusValueType)(-1), loaded.ModbusSettings.ValueType);
        Assert.Throws<ArgumentOutOfRangeException>(loaded.ModbusSettings.Validate);
        Assert.Contains("Modbus settings are invalid", profiles.LastError, StringComparison.OrdinalIgnoreCase);

        await profiles.SaveAsync(temporary.Path, loaded, default);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(temporary.Path));
        Assert.Equal(-1, saved.RootElement.GetProperty(nameof(AppProfile.ModbusSettings)).GetProperty(nameof(ModbusMonitorSettings.ValueType)).GetInt32());
        var reloaded = await profiles.LoadAsync(temporary.Path, default);
        Assert.Equal(original.Name, reloaded.Name);
        Assert.Equal((ModbusValueType)(-1), reloaded.ModbusSettings.ValueType);
    }

    [Theory]
    [InlineData("\"UInt16\"", ModbusValueType.UInt16)]
    [InlineData("\"float32\"", ModbusValueType.Float32)]
    [InlineData("\"2\"", ModbusValueType.UInt32)]
    [InlineData("3", ModbusValueType.Int32)]
    [InlineData("99", (ModbusValueType)99)]
    public async Task ExistingNamedAndIntegerDisplayTypesKeepTheirMeaning(string token, ModbusValueType expected)
    {
        using var temporary = new TemporaryProfile();
        await File.WriteAllTextAsync(temporary.Path, "{\"ModbusSettings\":{\"ValueType\":" + token + "}}");
        var loaded = await new ProfileService().LoadAsync(temporary.Path, default);
        Assert.Equal(expected, loaded.ModbusSettings.ValueType);
    }

    private sealed class TemporaryProfile : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "serial-modbus-profile-review-" + Guid.NewGuid().ToString("N"));
        public TemporaryProfile() => Directory.CreateDirectory(_directory);
        public string Path => System.IO.Path.Combine(_directory, "profile.json");
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }
}
