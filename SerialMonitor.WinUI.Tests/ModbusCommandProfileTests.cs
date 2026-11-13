using System.Text.Json;
using System.Text.Json.Nodes;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusCommandProfileTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ModbusCommands\":null}")]
    public async Task LegacyProfilesDoNotCreateCommandsOrRunningState(string json)
    {
        using var temporary = new TemporaryProfile();
        await File.WriteAllTextAsync(temporary.Path, json);
        var service = new ProfileService();
        var loaded = await service.LoadAsync(temporary.Path, default);
        Assert.Null(loaded.ModbusCommands);
        await service.SaveAsync(temporary.Path, loaded, default);
        Assert.Null((await service.LoadAsync(temporary.Path, default)).ModbusCommands);
    }

    [Fact]
    public async Task FullCommandConfigurationSurvivesActualSaveLoadAndSequencePersistenceCopy()
    {
        using var temporary = new TemporaryProfile();
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        var floatStep = Step();
        floatStep.Name = "Set reference";
        floatStep.UnitId = 37;
        floatStep.Item = new()
        {
            Address = 520, Name = "Reference", ValueType = ModbusValueType.Float32,
            Scale = 0.1, Unit = "rpm", SwapBytes = true, SwapWords = true, Access = ModbusItemAccess.ReadWrite,
            ValueNames = [new() { Value = 1, Name = "Preset" }]
        };
        floatStep.Value = "Preset";
        var statusStep = Step();
        statusStep.Name = "Start";
        statusStep.UnitId = 2;
        statusStep.Verification = new()
        {
            Mode = ModbusCommandVerificationMode.Status, Function = 2, Address = 100,
            Mask = 1, ExpectedValue = 1, TimeoutMs = 5000, IntervalMs = 250
        };
        var ackStep = Step();
        ackStep.Verification.Mode = ModbusCommandVerificationMode.AcknowledgementOnly;
        profile.ModbusCommands = new()
        {
            SelectedCommandId = "run", Commands = [new() { Id = "run", Name = "Product startup", Steps = [floatStep, statusStep, ackStep] }]
        };
        profile.CommandSequences = [new() { Name = "Check", Steps = [new() { CommandText = "status" }] }];

        await service.SaveAsync(temporary.Path, profile, default);
        var loaded = await service.LoadAsync(temporary.Path, default);
        loaded.CommandSequences[0].Steps[0].CommandText = "version";
        await service.SaveSequenceChangeAsync(temporary.Path, loaded, "Check", default);
        var reloaded = await service.LoadAsync(temporary.Path, default);

        var settings = Assert.IsType<ModbusCommandSettings>(reloaded.ModbusCommands);
        settings.Validate();
        Assert.Equal("run", settings.SelectedCommandId);
        var command = Assert.Single(settings.Commands);
        Assert.Equal("Product startup", command.Name);
        Assert.Equal(3, command.Steps.Count);
        var actual = command.Steps[0];
        Assert.Equal(37, actual.UnitId);
        Assert.Equal(520, actual.Item.Address);
        Assert.Equal(ModbusValueType.Float32, actual.Item.ValueType);
        Assert.Equal(0.1, actual.Item.Scale);
        Assert.True(actual.Item.SwapBytes);
        Assert.True(actual.Item.SwapWords);
        Assert.Equal("rpm", actual.Item.Unit);
        Assert.Equal("Preset", Assert.Single(actual.Item.ValueNames).Name);
        Assert.Equal("Preset", actual.Value);
        Assert.Equal(ModbusCommandVerificationMode.ReadBack, actual.Verification.Mode);
        Assert.Equal(ModbusCommandVerificationMode.Status, command.Steps[1].Verification.Mode);
        Assert.Equal((byte)2, command.Steps[1].Verification.Function);
        Assert.Equal(100, command.Steps[1].Verification.Address);
        Assert.Equal((ushort)1, command.Steps[1].Verification.Mask);
        Assert.Equal((ushort)1, command.Steps[1].Verification.ExpectedValue);
        Assert.Equal(5000, command.Steps[1].Verification.TimeoutMs);
        Assert.Equal(250, command.Steps[1].Verification.IntervalMs);
        Assert.Equal(ModbusCommandVerificationMode.AcknowledgementOnly, command.Steps[2].Verification.Mode);
        Assert.Null(service.LastError);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(temporary.Path));
        var stored = document.RootElement.GetProperty("ModbusCommands");
        Assert.Equal(new[] { "Commands", "SelectedCommandId" }, stored.EnumerateObject().Select(property => property.Name).Order());
        foreach (var runtime in new[] { "IsRunning", "State", "CurrentStep", "Generation", "Result", "UpdatedAt" })
            Assert.False(stored.TryGetProperty(runtime, out _));
    }

    [Fact]
    public void CloneSeparatesCommandsStepsItemsLabelsAndVerification()
    {
        var settings = Settings();
        settings.Commands[0].Steps[0].Item.ValueNames = [new() { Value = 1, Name = "Preset" }];
        var clone = settings.Clone();
        var copy = clone.Commands[0].Steps[0];
        copy.Item.Address = 42;
        copy.Item.ValueNames[0].Name = "Other";
        copy.Verification.Address = 800;
        clone.Commands[0].Name = "Other command";
        clone.Commands.Add(new() { Id = "extra", Steps = [Step()] });

        Assert.Single(settings.Commands);
        Assert.Equal("Command", settings.Commands[0].Name);
        Assert.Equal(10, settings.Commands[0].Steps[0].Item.Address);
        Assert.Equal("Preset", settings.Commands[0].Steps[0].Item.ValueNames[0].Name);
        Assert.Equal(0, settings.Commands[0].Steps[0].Verification.Address);
    }

    [Fact]
    public void TypedWritesBuildStandardPacketsWithoutAMonitoredReadArea()
    {
        var step = Step();
        step.UnitId = 37;
        step.Item = new() { Address = 65534, ValueType = ModbusValueType.Int32, Access = ModbusItemAccess.ReadWrite, Scale = 0.1, SwapWords = true };
        step.Value = "-12.3";
        Assert.Equal(new ushort[] { 0xFF85, 0xFFFF }, step.EncodeValues());
        Assert.Equal(ModbusRtuRequestBuilder.WriteMultiple(37, 16, 65534, [0xFF85, 0xFFFF]), step.BuildPacket());
        step.Function = 1;
        step.Item = new() { Address = 12, Access = ModbusItemAccess.ReadWrite };
        step.Value = "1";
        Assert.Equal(ModbusRtuRequestBuilder.WriteSingle(37, 5, 12, 1), step.BuildPacket());
        step.Function = 3;
        step.Item.ValueType = ModbusValueType.Int16;
        step.Value = "-1";
        Assert.Equal(ModbusRtuRequestBuilder.WriteSingle(37, 6, 12, 65535), step.BuildPacket());
    }

    [Fact]
    public void EveryStepIsPreflightedIncludingLateEncodingErrors()
    {
        var settings = Settings();
        var invalid = Step();
        invalid.Value = "1.5";
        settings.Commands[0].Steps.Add(invalid);
        Assert.NotEmpty(settings.Commands[0].Steps[0].BuildPacket());
        Assert.ThrowsAny<ArgumentException>(settings.Validate);
    }

    [Fact]
    public void CommandAndStepCountAndNominalDurationAreBounded()
    {
        var settings = new ModbusCommandSettings
        {
            Commands = Enumerable.Range(0, ModbusCommandSettings.MaximumCommands).Select(index => new ModbusCommandDefinition { Id = $"c{index}", Steps = [Step()] }).ToList()
        };
        settings.Validate();
        settings.Commands.Add(new() { Id = "overflow", Steps = [Step()] });
        Assert.Throws<ArgumentException>(settings.Validate);
        var command = Settings().Commands[0];
        command.Steps = Enumerable.Range(0, 32).Select(_ => Step()).ToList();
        command.Validate();
        command.Steps.Add(Step());
        Assert.Throws<ArgumentException>(command.Validate);
        command.Steps = Enumerable.Range(0, 15).Select(_ => { var step = Step(); step.TimeoutMs = 10000; return step; }).ToList();
        command.Validate();
        command.Steps.Add(Step());
        Assert.Throws<ArgumentException>(command.Validate);
    }

    [Theory]
    [InlineData(0, 3, 10)]
    [InlineData(248, 3, 10)]
    [InlineData(1, 2, 10)]
    [InlineData(1, 4, 10)]
    [InlineData(1, 3, -1)]
    [InlineData(1, 3, 65536)]
    public void InvalidWriteTargetsAreRejected(int unit, byte function, int address)
    {
        var step = Step();
        step.UnitId = unit;
        step.Function = function;
        step.Item.Address = address;
        Assert.ThrowsAny<ArgumentException>(step.Validate);
    }

    [Fact]
    public void ItemMustExplicitlyBeWritableAndWholeTypedValueMustFit()
    {
        var step = Step();
        step.Item.Access = ModbusItemAccess.ReadOnly;
        Assert.Throws<ArgumentException>(step.Validate);
        step.Item.Access = ModbusItemAccess.ReadWrite;
        step.Item.ValueType = ModbusValueType.Float32;
        step.Item.Address = 65535;
        Assert.ThrowsAny<ArgumentException>(step.Validate);
        step.Item.Address = 10;
        step.Value = "NaN";
        Assert.ThrowsAny<ArgumentException>(step.Validate);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(0x000F, 0x0010)]
    public void StatusExpectedBitsMustBeContainedInANonzeroMask(ushort mask, ushort expected)
    {
        var verification = new ModbusCommandVerification { Mode = ModbusCommandVerificationMode.Status, Mask = mask, ExpectedValue = expected };
        Assert.ThrowsAny<ArgumentException>(() => verification.Validate());
    }

    [Fact]
    public void CoilStatusRequiresOneBitAndTimingMustRemainBounded()
    {
        var verification = new ModbusCommandVerification { Mode = ModbusCommandVerificationMode.Status, Function = 2, Mask = 1, ExpectedValue = 1 };
        verification.Validate();
        verification.Mask = 3;
        Assert.ThrowsAny<ArgumentException>(() => verification.Validate());
        verification.Mask = 1;
        verification.TimeoutMs = 100;
        verification.IntervalMs = 200;
        Assert.ThrowsAny<ArgumentException>(() => verification.Validate());
        verification.IntervalMs = 100;
        verification.Validate();
    }

    [Theory]
    [InlineData("42")]
    [InlineData("{\"Commands\":null}")]
    [InlineData("{\"Commands\":{}}")]
    [InlineData("{\"Commands\":[null]}")]
    [InlineData("{\"Commands\":[{}]}")]
    [InlineData("{\"Commands\":[],\"SelectedCommandId\":42}")]
    public async Task MalformedCommandCollectionsDoNotDiscardOtherProfileDataOrBecomeValid(string commandsJson)
    {
        using var temporary = new TemporaryProfile();
        await WriteProfileAsync(temporary.Path, commandsJson);
        var service = new ProfileService();

        var loaded = await service.LoadAsync(temporary.Path, default);
        AssertUnrelatedProfile(loaded);
        var settings = Assert.IsType<ModbusCommandSettings>(loaded.ModbusCommands);
        Assert.ThrowsAny<ArgumentException>(settings.Validate);
        Assert.ThrowsAny<ArgumentException>(settings.Clone().Validate);
        await service.SaveAsync(temporary.Path, loaded, default);
        var reloaded = await service.LoadAsync(temporary.Path, default);
        AssertUnrelatedProfile(reloaded);
        Assert.NotNull(reloaded.ModbusCommands);
        Assert.ThrowsAny<ArgumentException>(reloaded.ModbusCommands.Validate);
    }

    [Theory]
    [InlineData("UnitId")]
    [InlineData("Function")]
    [InlineData("Item")]
    [InlineData("Value")]
    [InlineData("Verification")]
    public void MissingStepWireFieldsNeverBecomeDefaultDeviceOrValue(string field)
    {
        var document = JsonNode.Parse(JsonSerializer.Serialize(Settings()))!;
        document["Commands"]![0]!["Steps"]![0]!.AsObject().Remove(field);
        AssertInvalidAcrossCloneAndSave(document.ToJsonString());
    }

    [Theory]
    [InlineData("Address")]
    [InlineData("ValueType")]
    [InlineData("Access")]
    public void MissingTypedItemFieldsNeverEnableARawDefaultWrite(string field)
    {
        var document = JsonNode.Parse(JsonSerializer.Serialize(Settings()))!;
        document["Commands"]![0]!["Steps"]![0]!["Item"]!.AsObject().Remove(field);
        AssertInvalidAcrossCloneAndSave(document.ToJsonString());
    }

    [Theory]
    [InlineData("Mode")]
    [InlineData("Function")]
    [InlineData("Address")]
    [InlineData("Mask")]
    [InlineData("ExpectedValue")]
    public void MissingStatusConditionsNeverDefaultToAcknowledgementOrZeroAddress(string field)
    {
        var settings = Settings();
        settings.Commands[0].Steps[0].Verification.Mode = ModbusCommandVerificationMode.Status;
        var document = JsonNode.Parse(JsonSerializer.Serialize(settings))!;
        document["Commands"]![0]!["Steps"]![0]!["Verification"]!.AsObject().Remove(field);
        AssertInvalidAcrossCloneAndSave(document.ToJsonString());
    }

    [Theory]
    [InlineData("Mode", "\"FutureMode\"")]
    [InlineData("Mode", "null")]
    [InlineData("ExpectedValue", "65536")]
    [InlineData("ExpectedValue", "\"zero\"")]
    [InlineData("Mask", "null")]
    public async Task InvalidResultMetadataPreservesCommandTargetAndUnrelatedProfile(string field, string json)
    {
        using var temporary = new TemporaryProfile();
        var document = JsonNode.Parse(JsonSerializer.Serialize(Settings()))!;
        document["Commands"]![0]!["Steps"]![0]!["Verification"]![field] = JsonNode.Parse(json);
        await WriteProfileAsync(temporary.Path, document.ToJsonString());
        var service = new ProfileService();
        var loaded = await service.LoadAsync(temporary.Path, default);
        AssertUnrelatedProfile(loaded);
        var command = Assert.Single(Assert.IsType<ModbusCommandSettings>(loaded.ModbusCommands).Commands);
        Assert.Equal("command", command.Id);
        Assert.Equal(1, Assert.Single(command.Steps).UnitId);
        Assert.Equal(10, command.Steps[0].Item.Address);
        Assert.ThrowsAny<ArgumentException>(loaded.ModbusCommands.Validate);
        await service.SaveAsync(temporary.Path, loaded, default);
        var reloaded = await service.LoadAsync(temporary.Path, default);
        AssertUnrelatedProfile(reloaded);
        Assert.ThrowsAny<ArgumentException>(reloaded.ModbusCommands!.Validate);
    }

    [Theory]
    [InlineData("1e999")]
    [InlineData("-1e999")]
    public async Task NonfiniteItemScaleRemainsInvalidAndSerializable(string scaleJson)
    {
        using var temporary = new TemporaryProfile();
        var json = JsonSerializer.Serialize(Settings()).Replace("\"Scale\":1", "\"Scale\":" + scaleJson, StringComparison.Ordinal);
        await WriteProfileAsync(temporary.Path, json);
        var service = new ProfileService();
        var loaded = await service.LoadAsync(temporary.Path, default);
        AssertUnrelatedProfile(loaded);
        Assert.ThrowsAny<ArgumentException>(loaded.ModbusCommands!.Validate);
        await service.SaveAsync(temporary.Path, loaded, default);
        var reloaded = await service.LoadAsync(temporary.Path, default);
        Assert.ThrowsAny<ArgumentException>(reloaded.ModbusCommands!.Validate);
    }

    [Fact]
    public void DuplicateIdsAreRejectedButOrphanViewSelectionIsPreserved()
    {
        var settings = Settings();
        settings.SelectedCommandId = "unknown-selection";
        settings.Validate();
        var copy = settings.Commands[0].Clone();
        settings.Commands.Add(copy);
        Assert.Throws<ArgumentException>(settings.Validate);
        copy.Id = "COMMAND";
        settings.Validate();
        Assert.Equal("unknown-selection", settings.Clone().SelectedCommandId);
    }

    private static void AssertInvalidAcrossCloneAndSave(string json)
    {
        var settings = JsonSerializer.Deserialize<ModbusCommandSettings>(json)!;
        Assert.ThrowsAny<ArgumentException>(settings.Validate);
        Assert.ThrowsAny<ArgumentException>(settings.Clone().Validate);
        var reloaded = JsonSerializer.Deserialize<ModbusCommandSettings>(JsonSerializer.Serialize(settings.Clone()))!;
        Assert.ThrowsAny<ArgumentException>(reloaded.Validate);
    }

    private static ModbusCommandSettings Settings() => new()
    {
        Commands = [new() { Id = "command", Name = "Command", Steps = [Step()] }], SelectedCommandId = "command"
    };

    private static ModbusCommandStep Step() => new()
    {
        Name = "Write", UnitId = 1, Function = 3,
        Item = new() { Address = 10, Name = "Value", Access = ModbusItemAccess.ReadWrite },
        Value = "1", Verification = new()
    };

    private static Task WriteProfileAsync(string path, string commandsJson) => File.WriteAllTextAsync(path,
        "{\"Name\":\"Keep profile\",\"ModbusSettings\":{\"UnitId\":19,\"Address\":800,\"Quantity\":2},\"ModbusCommands\":" + commandsJson +
        ",\"SavedCommands\":[{\"Name\":\"Keep command\",\"CommandText\":\"status\"}]}");

    private static void AssertUnrelatedProfile(AppProfile profile)
    {
        Assert.Equal("Keep profile", profile.Name);
        Assert.Equal(19, profile.ModbusSettings.UnitId);
        Assert.Equal(800, profile.ModbusSettings.Address);
        Assert.Equal("Keep command", Assert.Single(profile.SavedCommands).Name);
    }

    private sealed class TemporaryProfile : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SerialMonitor.Tests", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "profile.json");
        public TemporaryProfile() => Directory.CreateDirectory(_directory);
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
