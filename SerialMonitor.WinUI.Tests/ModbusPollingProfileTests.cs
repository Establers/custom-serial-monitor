using System.Text.Json;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusPollingProfileTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ModbusPolling\":null}")]
    [InlineData("{\"Name\":\"Legacy\",\"ModbusSettings\":{\"UnitId\":37,\"Address\":520,\"Quantity\":8}}")]
    public async Task LegacyProfilesRetainANullWorkspaceInsteadOfCreatingAnEnabledGroup(string json)
    {
        using var temporary = new TemporaryProfile();
        await File.WriteAllTextAsync(temporary.Path, json);
        var service = new ProfileService();

        var loaded = await service.LoadAsync(temporary.Path, default);

        Assert.Null(loaded.ModbusPolling);
        await service.SaveAsync(temporary.Path, loaded, default);
        Assert.Null((await service.LoadAsync(temporary.Path, default)).ModbusPolling);
    }

    [Fact]
    public async Task ExplicitEmptyWorkspaceDoesNotFallBackToTheLegacyTarget()
    {
        using var temporary = new TemporaryProfile();
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.ModbusSettings.UnitId = 37;
        profile.ModbusSettings.Address = 520;
        profile.ModbusPolling = new();

        await service.SaveAsync(temporary.Path, profile, default);
        var loaded = await service.LoadAsync(temporary.Path, default);

        Assert.NotNull(loaded.ModbusPolling);
        Assert.Empty(loaded.ModbusPolling.Groups);
        loaded.ModbusPolling.Validate();
        Assert.Throws<ArgumentException>(() => loaded.ModbusPolling.Validate(requireEnabled: true));
        Assert.Equal(37, loaded.ModbusSettings.UnitId);
        Assert.Equal(520, loaded.ModbusSettings.Address);
    }

    [Fact]
    public async Task CompleteGroupsAndSelectionSurviveActualSaveLoadAndSequencePersistenceCopy()
    {
        using var temporary = new TemporaryProfile();
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        var first = Group("motor", 1, 3, 10, 8);
        first.Name = "Motor 1";
        first.Settings.IntervalMs = 500;
        first.Settings.Items =
        [
            new() { Address = 10, Name = "Reference", ValueType = ModbusValueType.Int32, Scale = 0.1, Unit = "rpm", SwapBytes = true, SwapWords = true, Access = ModbusItemAccess.ReadWrite },
            new() { Address = 12, Name = "Mode", Access = ModbusItemAccess.ReadWrite, ValueNames = [new() { Value = 1, Name = "Automatic" }] }
        ];
        var second = Group("meter", 2, 4, 100, 6);
        second.Enabled = false;
        second.Settings.TimeoutMs = 4000;
        second.Settings.ValueType = ModbusValueType.Float32;
        profile.ModbusPolling = new() { Groups = [first, second], SelectedGroupId = second.Id };
        profile.CommandSequences = [new() { Name = "Check", Steps = [new() { CommandText = "status" }] }];

        await service.SaveAsync(temporary.Path, profile, default);
        var loaded = await service.LoadAsync(temporary.Path, default);
        loaded.CommandSequences[0].Steps[0].CommandText = "version";
        await service.SaveSequenceChangeAsync(temporary.Path, loaded, "Check", default);
        var reloaded = await service.LoadAsync(temporary.Path, default);

        var settings = Assert.IsType<ModbusPollingSettings>(reloaded.ModbusPolling);
        settings.Validate(requireEnabled: true);
        Assert.Equal("meter", settings.SelectedGroupId);
        Assert.Equal(2, settings.Groups.Count);
        Assert.Equal("motor", settings.Groups[0].Id);
        Assert.Equal("Motor 1", settings.Groups[0].Name);
        Assert.Equal(500, settings.Groups[0].Settings.IntervalMs);
        var item = settings.Groups[0].Settings.Items[0];
        Assert.Equal(ModbusValueType.Int32, item.ValueType);
        Assert.Equal(0.1, item.Scale);
        Assert.Equal("rpm", item.Unit);
        Assert.True(item.SwapBytes);
        Assert.True(item.SwapWords);
        Assert.Equal("Automatic", Assert.Single(settings.Groups[0].Settings.Items[1].ValueNames).Name);
        Assert.False(settings.Groups[1].Enabled);
        Assert.Equal(4000, settings.Groups[1].Settings.TimeoutMs);
        Assert.Equal(ModbusValueType.Float32, settings.Groups[1].Settings.ValueType);
        Assert.Null(service.LastError);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(temporary.Path));
        var stored = document.RootElement.GetProperty("ModbusPolling");
        Assert.Equal(new[] { "Groups", "SelectedGroupId" }, stored.EnumerateObject().Select(property => property.Name).Order());
        var group = stored.GetProperty("Groups")[0];
        Assert.Equal(new[] { "Enabled", "Id", "Name", "Settings" }, group.EnumerateObject().Select(property => property.Name).Order());
        foreach (var runtime in new[] { "IsPolling", "IsBusy", "Values", "State", "Generation", "UpdatedAt" })
            Assert.False(stored.TryGetProperty(runtime, out _));
    }

    [Fact]
    public void CloneSeparatesEveryGroupSettingsItemAndLabel()
    {
        var group = Group("motor", 1, 3, 10, 8);
        group.Settings.Items = [new() { Address = 10, Name = "Mode", ValueNames = [new() { Value = 1, Name = "Automatic" }] }];
        var settings = new ModbusPollingSettings { Groups = [group], SelectedGroupId = group.Id };

        var clone = settings.Clone();
        clone.Groups[0].Name = "Other";
        clone.Groups[0].Settings.UnitId = 2;
        clone.Groups[0].Settings.Items[0].ValueNames[0].Name = "Manual";
        clone.Groups.Add(Group("extra", 3, 3, 0, 1));

        Assert.Single(settings.Groups);
        Assert.Equal("", settings.Groups[0].Name);
        Assert.Equal(1, settings.Groups[0].Settings.UnitId);
        Assert.Equal("Automatic", settings.Groups[0].Settings.Items[0].ValueNames[0].Name);
        Assert.Equal("motor", clone.SelectedGroupId);
    }

    [Fact]
    public void IdenticalWireAreasAreAllowedButIdentifiersMustBeUnique()
    {
        var settings = new ModbusPollingSettings { Groups = [Group("one"), Group("two")] };
        settings.Validate(requireEnabled: true);
        settings.Groups[1].Id = "one";
        Assert.Throws<ArgumentException>(() => settings.Validate());
        settings.Groups[1].Id = "ONE";
        settings.Validate();
    }

    [Fact]
    public void DisabledGroupsAreStillValidatedAndNeedAnEnabledPeerToRun()
    {
        var group = Group("disabled");
        group.Enabled = false;
        var settings = new ModbusPollingSettings { Groups = [group] };
        settings.Validate();
        Assert.Throws<ArgumentException>(() => settings.Validate(requireEnabled: true));
        group.Settings.Address = 65536;
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
    }

    [Fact]
    public void GroupsAndTotalCellsHaveIndependentBounds()
    {
        var settings = new ModbusPollingSettings
        {
            Groups = Enumerable.Range(0, ModbusPollingSettings.MaximumGroups).Select(index => Group($"g{index}")).ToList()
        };
        settings.Validate();
        settings.Groups.Add(Group("overflow"));
        Assert.Throws<ArgumentException>(() => settings.Validate());

        settings.Groups = Enumerable.Range(0, 4).Select(index => Group($"coils{index}", 1, 1, 0, 2000)).ToList();
        settings.Groups.Add(Group("remainder", 1, 1, 0, 192));
        settings.Validate();
        settings.Groups[^1].Enabled = false;
        settings.Groups[^1].Settings.Quantity = 193;
        Assert.Throws<ArgumentException>(() => settings.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" with-space")]
    [InlineData("with-space ")]
    [InlineData("line\nbreak")]
    public void InvalidIdentifierCannotBecomeANewGeneratedIdentity(string id)
    {
        var settings = new ModbusPollingSettings { Groups = [Group(id)] };
        Assert.Throws<ArgumentException>(() => settings.Validate());
        var clone = settings.Clone();
        Assert.Equal(id, clone.Groups[0].Id);
        Assert.Throws<ArgumentException>(() => clone.Validate());
    }

    [Fact]
    public void TextLimitsAndOrphanViewSelectionAreHandledWithoutChoosingAnotherTarget()
    {
        var group = Group(new string('a', ModbusPollingGroup.MaximumIdLength));
        group.Name = new string('n', ModbusPollingGroup.MaximumNameLength);
        var settings = new ModbusPollingSettings { Groups = [group], SelectedGroupId = "not-present" };
        settings.Validate();
        Assert.Equal("not-present", settings.Clone().SelectedGroupId);
        group.Name += "x";
        Assert.Throws<ArgumentException>(() => settings.Validate());
        group.Name = "";
        group.Id += "x";
        Assert.Throws<ArgumentException>(() => settings.Validate());
        group.Id = "group";
        settings.SelectedGroupId = new string('s', ModbusPollingGroup.MaximumIdLength + 1);
        Assert.Throws<ArgumentException>(() => settings.Validate());
    }

    [Theory]
    [InlineData("42")]
    [InlineData("{\"Groups\":null}")]
    [InlineData("{\"Groups\":{}}")]
    [InlineData("{\"Groups\":[null]}")]
    [InlineData("{\"Groups\":[42]}")]
    [InlineData("{\"Groups\":[{}]}")]
    [InlineData("{\"Groups\":[{\"Id\":\"one\",\"Settings\":null}]}")]
    [InlineData("{\"Groups\":[{\"Id\":\"one\",\"Settings\":{}}]}")]
    [InlineData("{\"Groups\":[],\"SelectedGroupId\":42}")]
    public async Task MalformedWorkspaceDoesNotDiscardUnrelatedProfileOrBecomeLegacy(string workspaceJson)
    {
        using var temporary = new TemporaryProfile();
        await WriteProfileAsync(temporary.Path, workspaceJson);
        var service = new ProfileService();
        var loaded = await service.LoadAsync(temporary.Path, default);

        AssertUnrelatedProfile(loaded);
        var settings = Assert.IsType<ModbusPollingSettings>(loaded.ModbusPolling);
        Assert.ThrowsAny<ArgumentException>(() => settings.Validate());
        Assert.ThrowsAny<ArgumentException>(() => settings.Clone().Validate());
        Assert.Contains("Modbus polling settings are invalid", service.LastError, StringComparison.OrdinalIgnoreCase);
        await service.SaveAsync(temporary.Path, loaded, default);
        var reloaded = await service.LoadAsync(temporary.Path, default);
        AssertUnrelatedProfile(reloaded);
        Assert.NotNull(reloaded.ModbusPolling);
        Assert.ThrowsAny<ArgumentException>(() => reloaded.ModbusPolling.Validate());
    }

    [Fact]
    public void MissingWireFieldsStayExplicitlyInvalidInsteadOfCreatingADefaultDevice()
    {
        var settings = JsonSerializer.Deserialize<ModbusPollingSettings>("{\"Groups\":[{\"Id\":\"missing-target\",\"Settings\":{}}]}")!;
        var group = Assert.Single(settings.Groups);
        Assert.Equal(int.MinValue, group.Settings.UnitId);
        Assert.Equal((byte)0, group.Settings.Function);
        Assert.Equal(int.MinValue, group.Settings.Address);
        Assert.Equal(int.MinValue, group.Settings.Quantity);
        // Explicitly fixing an unrelated metadata field cannot enable a target
        // that the imported profile never supplied.
        group.HasInvalidMetadata = false;
        group.Name = "Renamed";
        Assert.ThrowsAny<ArgumentException>(() => settings.Validate());
        var reloaded = JsonSerializer.Deserialize<ModbusPollingSettings>(JsonSerializer.Serialize(settings.Clone()))!;
        Assert.Equal(int.MinValue, Assert.Single(reloaded.Groups).Settings.UnitId);
        Assert.ThrowsAny<ArgumentException>(() => reloaded.Validate());
    }

    [Theory]
    [InlineData("\"Enabled\":\"yes\",", "")]
    [InlineData("", ",\"ValueType\":\"FutureType\"")]
    [InlineData("", ",\"Scale\":1e999")]
    [InlineData("", ",\"Scale\":-1e999")]
    [InlineData("", ",\"SwapWords\":\"true\"")]
    [InlineData("", ",\"Items\":[{\"Address\":520,\"Access\":\"FutureAccess\"}]")]
    public async Task MalformedGroupMetadataKeepsItsIdentityAndTargetAndCanBeSavedAgain(string groupFields, string settingsFields)
    {
        using var temporary = new TemporaryProfile();
        var workspace = "{\"Groups\":[{\"Id\":\"original-group\",\"Name\":\"Keep this group\"," + groupFields +
            "\"Settings\":{\"UnitId\":37,\"Function\":3,\"Address\":520,\"Quantity\":8" + settingsFields +
            "}}],\"SelectedGroupId\":\"original-group\"}";
        await WriteProfileAsync(temporary.Path, workspace);
        var service = new ProfileService();

        var loaded = await service.LoadAsync(temporary.Path, default);
        var settings = Assert.IsType<ModbusPollingSettings>(loaded.ModbusPolling);
        var group = Assert.Single(settings.Groups);
        Assert.Equal("original-group", group.Id);
        Assert.Equal("Keep this group", group.Name);
        Assert.Equal(37, group.Settings.UnitId);
        Assert.Equal(520, group.Settings.Address);
        Assert.ThrowsAny<ArgumentException>(() => settings.Validate());
        await service.SaveAsync(temporary.Path, loaded, default);
        var reloaded = await service.LoadAsync(temporary.Path, default);
        AssertUnrelatedProfile(reloaded);
        var again = Assert.Single(Assert.IsType<ModbusPollingSettings>(reloaded.ModbusPolling).Groups);
        Assert.Equal("original-group", again.Id);
        Assert.Equal(37, again.Settings.UnitId);
        Assert.Equal(520, again.Settings.Address);
        Assert.ThrowsAny<ArgumentException>(() => reloaded.ModbusPolling!.Validate());
    }

    [Theory]
    [InlineData(0, 520)]
    [InlineData(248, 520)]
    [InlineData(37, -1)]
    [InlineData(37, 65536)]
    public async Task InvalidNumericTargetsAreNotReplacedWithValidDefaults(int unit, int address)
    {
        using var temporary = new TemporaryProfile();
        var profile = new AppProfile { ModbusPolling = new() { Groups = [Group("invalid-target", unit, 3, address, 1)] } };
        var service = new ProfileService();

        await service.SaveAsync(temporary.Path, profile, default);
        var loaded = await service.LoadAsync(temporary.Path, default);

        var group = Assert.Single(Assert.IsType<ModbusPollingSettings>(loaded.ModbusPolling).Groups);
        Assert.Equal(unit, group.Settings.UnitId);
        Assert.Equal(address, group.Settings.Address);
        Assert.ThrowsAny<ArgumentException>(() => loaded.ModbusPolling!.Validate());
    }

    private static ModbusPollingGroup Group(string id, int unit = 1, byte function = 3, int address = 0, int quantity = 1) => new()
    {
        Id = id, Settings = new() { UnitId = unit, Function = function, Address = address, Quantity = quantity }
    };

    private static Task WriteProfileAsync(string path, string workspaceJson) => File.WriteAllTextAsync(path,
        "{\"Name\":\"Keep profile\",\"ModbusSettings\":{\"UnitId\":19,\"Address\":800,\"Quantity\":2},\"ModbusPolling\":" + workspaceJson +
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
