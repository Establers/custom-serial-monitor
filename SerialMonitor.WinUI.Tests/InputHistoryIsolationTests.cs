using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class InputHistoryIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(InputHistoryIsolationTests), Guid.NewGuid().ToString("N"));
    private string ProfilePath => Path.Combine(_root, "default.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public void PortSnapshotsKeepDifferentWirePayloadsDistinct()
    {
        var payloads = new[] { "PING", " PING ", "\tPING\t", "PING ", " PING" };
        var original = ExactHistory(payloads);
        var snapshot = ProfileInputHistory.Snapshot(original);
        Assert.Equal(payloads.Reverse(), snapshot.CommandHistory.Select(entry => entry.CommandText));
        foreach (var payload in payloads)
        {
            var entry = Assert.Single(snapshot.CommandHistory, entry => entry.CommandText == payload);
            Assert.Equal(Encoding.UTF8.GetBytes(payload), TxPayloadEncoder.Encode(entry.CommandText, TxSendMode.Terminal, TxLineEndingMode.None));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WirePayloadsSurviveEitherShutdownOrderAndProductionPortNavigation(bool secondClosesFirst)
    {
        var first = new ProfileService();
        var second = new ProfileService();
        var a = await LoadForPort(first, "COM7");
        var b = await LoadForPort(second, "COM8");
        var firstPayloads = new[] { "PING", " PING ", "\tPING\t" };
        var secondPayloads = new[] { "OTHER", " OTHER\t " };
        a.CommandHistory = ExactHistory(firstPayloads).CommandHistory;
        b.CommandHistory = ExactHistory(secondPayloads).CommandHistory;
        if (secondClosesFirst)
        {
            await second.SaveAsync(ProfilePath, b, default);
            await first.SaveAsync(ProfilePath, a, default);
        }
        else
        {
            await first.SaveAsync(ProfilePath, a, default);
            await second.SaveAsync(ProfilePath, b, default);
        }
        var restored = await LoadForPort(new(), "COM7");
        var model = CreateHistoryModel();
        Invoke(model, "RestorePortInputHistory", restored);
        foreach (var payload in firstPayloads.Reverse())
        {
            Assert.True(model.NavigateCommandHistory(-1));
            Assert.Equal(Encoding.UTF8.GetBytes(payload),
                TxPayloadEncoder.Encode(model.Commands.CurrentCommandText, TxSendMode.Terminal, TxLineEndingMode.None));
        }
        Invoke(model, "SwitchPortInputHistory", "COM8");
        foreach (var payload in secondPayloads.Reverse())
        {
            Assert.True(model.NavigateCommandHistory(-1));
            Assert.Equal(payload, model.Commands.CurrentCommandText);
        }
        Invoke(model, "SwitchPortInputHistory", "COM7");
        Assert.True(model.NavigateCommandHistory(-1));
        Assert.Equal("\tPING\t", model.Commands.CurrentCommandText);
    }

    [Fact]
    public async Task ConcurrentWritersKeepAllDistinctPaddedPayloadsOnTheSamePort()
    {
        var first = new ProfileService();
        var second = new ProfileService();
        var a = await LoadForPort(first, "COM7");
        var b = await LoadForPort(second, "com007");
        a.CommandHistory = ExactHistory("PING", " PING ").CommandHistory;
        b.CommandHistory = ExactHistory("PING ", "\tPING\t").CommandHistory;
        await Task.WhenAll(first.SaveAsync(ProfilePath, a, default), second.SaveAsync(ProfilePath, b, default));
        await first.SaveAsync(ProfilePath, a, default);
        var restored = await LoadForPort(new(), "COM7");
        Assert.Equal(new[] { "PING", " PING ", "PING ", "\tPING\t" }.Order(StringComparer.Ordinal),
            restored.CommandHistory.Select(entry => entry.CommandText).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ClearingOnePaddedPayloadDoesNotRemoveAnotherOrResurrectOnAStaleSave()
    {
        var seed = new ProfileService();
        var original = await LoadForPort(seed, "COM7");
        original.CommandHistory = ExactHistory("PING", " PING ").CommandHistory;
        await seed.SaveAsync(ProfilePath, original, default);
        var first = new ProfileService();
        var second = new ProfileService();
        var a = await LoadForPort(first, "COM7");
        var b = await LoadForPort(second, "COM7");
        b.CommandHistory.Add(new CommandHistoryEntry { CommandText = "\tPING\t", LastSentTime = DateTimeOffset.UnixEpoch.AddSeconds(10) });
        await second.SaveAsync(ProfilePath, b, default);
        a.CommandHistory.RemoveAll(entry => entry.CommandText == " PING ");
        await first.SaveAsync(ProfilePath, a, default);
        await second.SaveAsync(ProfilePath, b, default);
        var restored = await LoadForPort(new(), "COM7");
        Assert.Equal(new[] { "\tPING\t", "PING" }, restored.CommandHistory.Select(entry => entry.CommandText));
    }

    [Fact]
    public async Task ResendingExactPaddedPayloadAfterAnotherWindowObservedItSurvivesThatWindowsClear()
    {
        var seed = new ProfileService();
        var original = await LoadForPort(seed, "COM7");
        original.CommandHistory = ExactHistory(" PING ").CommandHistory;
        await seed.SaveAsync(ProfilePath, original, default);
        var first = new ProfileService();
        var second = new ProfileService();
        var a = await LoadForPort(first, "COM7");
        var b = await LoadForPort(second, "COM7");
        b.CommandHistory[0].LastSentTime = DateTimeOffset.UnixEpoch.AddSeconds(10);
        await second.SaveAsync(ProfilePath, b, default);
        a.CommandHistory.Clear();
        await first.SaveAsync(ProfilePath, a, default);
        var restored = Assert.Single((await LoadForPort(new(), "COM7")).CommandHistory);
        Assert.Equal(" PING ", restored.CommandText);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(10), restored.LastSentTime);
    }

    [Fact]
    public async Task LegacyMigrationKeepsExactCommonAndSlotPayloadsAndNewestDuplicateTimestamp()
    {
        Directory.CreateDirectory(_root);
        var legacy = new ProfileService().CreateDefaultProfile();
        legacy.SerialSettings.PortName = "COM7";
        legacy.CommandHistory = ExactHistory("PING", " PING ", "\tPING\t").CommandHistory;
        // The older duplicate comes last, so normalization must sort before deduplication.
        legacy.CommandHistory.Add(new CommandHistoryEntry { CommandText = " PING ", LastSentTime = DateTimeOffset.UnixEpoch.AddSeconds(-10) });
        legacy.InstanceHistories[2] = ExactHistory("PING ");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(legacy, JsonOptions);
        await File.WriteAllBytesAsync(ProfilePath, bytes);
        var service = new ProfileService();
        var profile = await service.LoadAsync(ProfilePath, default);
        Assert.Equal(4, profile.CommandHistory.Count);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1), Assert.Single(profile.CommandHistory, entry => entry.CommandText == " PING ").LastSentTime);
        await service.SaveAsync(ProfilePath, profile, default);
        var backup = Assert.Single(Directory.GetFiles(_root, "*.before-port-history-*.json"));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(backup));
        var restored = await LoadForPort(new(), "COM7");
        Assert.Contains(restored.LegacyInputHistory!.CommandHistory, entry => entry.CommandText == " PING ");
        Assert.Equal("PING ", Assert.Single(restored.InstanceHistories[2].CommandHistory).CommandText);
        Assert.Equal(4, restored.CommandHistory.Count);
    }

    [Fact]
    public async Task ProfileRetentionKeepsOneHundredDistinctPaddedPayloadsAndIgnoresBlankEntries()
    {
        var service = new ProfileService();
        var profile = await LoadForPort(service, "COM7");
        profile.CommandHistory = ExactHistory(Enumerable.Range(0, 120)
            .Select(index => "PING" + new string(' ', index)).ToArray()).CommandHistory;
        profile.CommandHistory.AddRange([null!, new() { CommandText = null! }, new() { CommandText = " \t " }]);
        await service.SaveAsync(ProfilePath, profile, default);
        var restored = await LoadForPort(new(), "COM7");
        Assert.Equal(100, restored.CommandHistory.Count);
        Assert.Equal(Enumerable.Range(20, 100).Reverse().Select(index => "PING" + new string(' ', index)),
            restored.CommandHistory.Select(entry => entry.CommandText));
        Assert.Empty((await LoadForPort(new(), "COM8")).CommandHistory);
    }

    private static InstanceInputHistory ExactHistory(params string[] commands) => new()
    {
        CommandHistory = commands.Select((command, index) => new CommandHistoryEntry
        {
            CommandText = command, LastSentTime = DateTimeOffset.UnixEpoch.AddSeconds(index)
        }).ToList()
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EitherShutdownOrderRestoresHistoriesByPortInAnyNewWindow(bool secondClosesFirst)
    {
        var first = new ProfileService();
        var second = new ProfileService();
        var a = await LoadForPort(first, "COM7");
        var b = await LoadForPort(second, "COM8");
        SetHistory(a, "first");
        SetHistory(b, "second");
        if (secondClosesFirst)
        {
            await second.SaveAsync(ProfilePath, b, default);
            await first.SaveAsync(ProfilePath, a, default);
        }
        else
        {
            await first.SaveAsync(ProfilePath, a, default);
            await second.SaveAsync(ProfilePath, b, default);
        }
        AssertHistory(await LoadForPort(new(), "COM8"), "second");
        AssertHistory(await LoadForPort(new(), "COM7"), "first");
        var third = await LoadForPort(new(), "COM9");
        Assert.Empty(third.CommandHistory);
        Assert.Empty(third.UiSettings.SearchHistory);
        Assert.Empty(third.UiSettings.LastSearchText);
    }

    [Fact]
    public async Task ConcurrentPortWritersAndAStaleProfilePreserveAllPortsAndSharedSequences()
    {
        var services = Enumerable.Range(0, 3).Select(_ => new ProfileService()).ToArray();
        var profiles = await Task.WhenAll(services.Select((service, index) => LoadForPort(service, $"COM{index + 7}")));
        for (var index = 0; index < 3; index++)
        {
            SetHistory(profiles[index], $"port{index + 7}");
            profiles[index].CommandSequences.Add(new CommandSequence { Name = $"sequence{index + 1}" });
        }
        await Task.WhenAll(services.Select((service, index) => service.SaveAsync(ProfilePath, profiles[index], default)));
        SetHistory(profiles[0], "updated");
        await services[0].SaveAsync(ProfilePath, profiles[0], default);
        for (var index = 0; index < 3; index++)
        {
            var restored = await LoadForPort(new(), $"COM{index + 7}");
            Assert.Equal(index == 0 ? "updated tx" : $"port{index + 7} tx", restored.CommandHistory[0].CommandText);
            Assert.Equal(3, restored.CommandSequences.Count);
        }
    }

    [Fact]
    public async Task TwoWindowsUsingSamePortMergeAdditionsAndUnchangedStaleSaves()
    {
        var first = new ProfileService();
        var second = new ProfileService();
        var a = await LoadForPort(first, "COM7");
        var b = await LoadForPort(second, "com007");
        SetHistory(a, "first");
        SetHistory(b, "second");
        await Task.WhenAll(first.SaveAsync(ProfilePath, a, default), second.SaveAsync(ProfilePath, b, default));
        await first.SaveAsync(ProfilePath, a, default);
        var restored = await LoadForPort(new(), "COM7");
        Assert.Equal(new[] { "first tx", "second tx" }, restored.CommandHistory.Select(entry => entry.CommandText).Order());
        Assert.Equal(new[] { "first new", "first old", "second new", "second old" }, restored.UiSettings.SearchHistory.Order());
        Assert.Equal(1, restored.PortHistories.Count(pair => pair.Key == "COM7"));
    }

    [Fact]
    public async Task AnUnchangedWindowCannotResurrectClearedHistory()
    {
        await SeedPort("COM7", "original");
        var first = new ProfileService();
        var second = new ProfileService();
        var a = await LoadForPort(first, "COM7");
        var b = await LoadForPort(second, "COM7");
        a.CommandHistory.Clear();
        a.UiSettings.SearchHistory.Clear();
        a.UiSettings.LastSearchText = string.Empty;
        await first.SaveAsync(ProfilePath, a, default);
        await second.SaveAsync(ProfilePath, b, default);
        var restored = await LoadForPort(new(), "COM7");
        Assert.Empty(restored.CommandHistory);
        Assert.Empty(restored.UiSettings.SearchHistory);
        Assert.Empty(restored.UiSettings.LastSearchText);
    }

    [Fact]
    public async Task ClearingObservedHistoryKeepsNewCommandsAndSearchesAddedByAnotherWindow()
    {
        await SeedPort("COM7", "original");
        await SeedPort("COM8", "other port");
        var first = new ProfileService();
        var second = new ProfileService();
        var a = await LoadForPort(first, "COM7");
        var b = await LoadForPort(second, "COM7");
        b.CommandHistory.Add(new CommandHistoryEntry { CommandText = "new remote tx" });
        b.UiSettings.SearchHistory.Add("new remote search");
        await second.SaveAsync(ProfilePath, b, default);
        a.CommandHistory.Clear();
        a.UiSettings.SearchHistory.Clear();
        a.UiSettings.LastSearchText = string.Empty;
        await first.SaveAsync(ProfilePath, a, default);
        var restored = await LoadForPort(new(), "COM7");
        Assert.Equal("new remote tx", Assert.Single(restored.CommandHistory).CommandText);
        Assert.Equal("new remote search", Assert.Single(restored.UiSettings.SearchHistory));
        AssertHistory(await LoadForPort(new(), "COM8"), "other port");
    }

    [Fact]
    public async Task LegacyCommonAndSlotHistoriesAreImportedOnceAndOriginalBytesAreBackedUp()
    {
        var legacy = new ProfileService().CreateDefaultProfile();
        legacy.SerialSettings.PortName = "COM7";
        SetHistory(legacy, "common");
        legacy.UiSettings.SearchHistory.Clear();
        legacy.InstanceHistories[1] = History("slot1");
        legacy.InstanceHistories[2] = History("slot2");
        Directory.CreateDirectory(_root);
        var original = JsonSerializer.SerializeToUtf8Bytes(legacy, JsonOptions);
        await File.WriteAllBytesAsync(ProfilePath, original);
        var service = new ProfileService();
        var loaded = await service.LoadAsync(ProfilePath, default);
        Assert.Equal(new[] { "common tx", "slot1 tx", "slot2 tx" }, loaded.CommandHistory.Select(entry => entry.CommandText).Order());
        Assert.Contains("common last", loaded.UiSettings.SearchHistory);
        await service.SaveAsync(ProfilePath, loaded, default);
        var backup = Assert.Single(Directory.GetFiles(_root, "*.before-port-history-*.json"));
        Assert.Equal(original, await File.ReadAllBytesAsync(backup));
        var disk = JsonSerializer.Deserialize<AppProfile>(await File.ReadAllTextAsync(ProfilePath), JsonOptions)!;
        Assert.Equal(1, disk.PortHistoryMigrationVersion);
        Assert.Equal("common tx", Assert.Single(disk.LegacyInputHistory!.CommandHistory).CommandText);
        Assert.Equal("slot2 tx", Assert.Single(disk.InstanceHistories[2].CommandHistory).CommandText);

        loaded.CommandHistory.Clear();
        loaded.UiSettings.SearchHistory.Clear();
        loaded.UiSettings.LastSearchText = string.Empty;
        await service.SaveAsync(ProfilePath, loaded, default);
        var again = await LoadForPort(new(), "COM7");
        Assert.Empty(again.CommandHistory);
        Assert.Empty(again.UiSettings.SearchHistory);
        Assert.Single(Directory.GetFiles(_root, "*.before-port-history-*.json"));
        Assert.Equal(original, await File.ReadAllBytesAsync(backup));
    }

    [Fact]
    public async Task SaveWithoutLoadPreservesLegacyPortAndImportsNewPortIndependently()
    {
        Directory.CreateDirectory(_root);
        var legacy = new ProfileService().CreateDefaultProfile();
        legacy.SerialSettings.PortName = "COM7";
        SetHistory(legacy, "legacy");
        await File.WriteAllTextAsync(ProfilePath, JsonSerializer.Serialize(legacy, JsonOptions));
        var service = new ProfileService();
        var local = service.CreateDefaultProfile();
        local.SerialSettings.PortName = "COM9";
        SetHistory(local, "new");
        await service.SaveAsync(ProfilePath, local, default);
        AssertHistory(await LoadForPort(new(), "COM7"), "legacy");
        AssertHistory(await LoadForPort(new(), "COM9"), "new");
    }

    [Fact]
    public async Task FailedMigrationBackupLeavesTheOriginalProfileUntouched()
    {
        Directory.CreateDirectory(_root);
        var legacy = new ProfileService().CreateDefaultProfile();
        legacy.SerialSettings.PortName = "COM7";
        SetHistory(legacy, "legacy");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(legacy, JsonOptions);
        await File.WriteAllBytesAsync(ProfilePath, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        Directory.CreateDirectory(ProfilePath + ".before-port-history-" + hash[..16] + ".json");
        var service = new ProfileService();
        var profile = await service.LoadAsync(ProfilePath, default);
        await Assert.ThrowsAnyAsync<IOException>(() => service.SaveAsync(ProfilePath, profile, default));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(ProfilePath));
        Assert.NotNull(service.LastError);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task RetentionLimitsAreIndependentAndSearchWhitespaceIsPreserved()
    {
        var service = new ProfileService();
        var profile = await LoadForPort(service, "COM7");
        profile.CommandHistory = Enumerable.Range(0, 120).Select(index => new CommandHistoryEntry
        {
            CommandText = $"tx{index}", LastSentTime = DateTimeOffset.UnixEpoch.AddSeconds(index)
        }).ToList();
        profile.UiSettings.SearchHistory = Enumerable.Range(0, 70).Select(index => $"term{index}").ToList();
        profile.UiSettings.SearchHistory.AddRange(["term40", " rx_mq", "", null!]);
        profile.UiSettings.LastSearchText = " rx_mq";
        await service.SaveAsync(ProfilePath, profile, default);
        Assert.Equal(" rx_mq", profile.UiSettings.LastSearchText);
        var restored = await LoadForPort(new(), "COM7");
        Assert.Equal(100, restored.CommandHistory.Count);
        Assert.Equal("tx119", restored.CommandHistory[0].CommandText);
        Assert.Equal(50, restored.UiSettings.SearchHistory.Count);
        Assert.Equal(" rx_mq", restored.UiSettings.SearchHistory[^1]);
        var clone = restored.UiSettings.Clone();
        clone.SearchHistory.Clear();
        Assert.Equal(50, restored.UiSettings.SearchHistory.Count);
        Assert.Empty((await LoadForPort(new(), "COM8")).CommandHistory);
    }

    [Theory]
    [InlineData(" com007 ", "COM7")]
    [InlineData(@"\\.\COM7", "COM7")]
    [InlineData("mock", "MOCK")]
    [InlineData(null, "")]
    public void PortIdentityIsCanonical(string? value, string expected) => Assert.Equal(expected, ProfileInputHistory.PortKey(value));

    [Fact]
    public async Task ConnectedPortOwnsSnapshotEvenIfAnotherPortIsSelectedForReconnect()
    {
        var service = new ProfileService();
        var profile = await LoadForPort(service, "COM7");
        SetHistory(profile, "connected");
        profile.SerialSettings.PortName = "COM8";
        await service.SaveAsync(ProfilePath, profile, default);
        AssertHistory(await LoadForPort(new(), "COM7"), "connected");
        Assert.Empty((await LoadForPort(new(), "COM8")).CommandHistory);
    }

    [Fact]
    public async Task NoPortAndMockHaveIndependentHistories()
    {
        await SeedPort("", "offline");
        await SeedPort("MOCK", "mock");
        AssertHistory(await LoadForPort(new(), ""), "offline");
        AssertHistory(await LoadForPort(new(), "MOCK"), "mock");
    }

    [Fact]
    public async Task GuiPortSwitchRestoresBothHistoriesAndResetsNavigationAndDrafts()
    {
        var profile = new ProfileService().CreateDefaultProfile();
        profile.SerialSettings.PortName = "COM7";
        profile.PortHistoryMigrationVersion = 1;
        profile.PortHistories["COM7"] = History("first");
        profile.PortHistories["COM8"] = History("second");
        var model = CreateHistoryModel();
        Invoke(model, "RestorePortInputHistory", profile);
        Assert.Equal("first tx", Assert.Single(model.Commands.CommandHistory).CommandText);
        Assert.True(model.RecallSearchHistory(-1));
        Assert.Equal("first new", model.SearchText);
        model.Commands.CurrentCommandText = "unsent first command";
        Invoke(model, "SwitchPortInputHistory", "COM8");
        Assert.Equal("second last", model.SearchText);
        Assert.Empty(model.Commands.CurrentCommandText);
        Assert.True(model.NavigateCommandHistory(-1));
        Assert.Equal("second tx", model.Commands.CurrentCommandText);
        Assert.True(model.RecallSearchHistory(-1));
        Assert.Equal("second new", model.SearchText);
        Assert.True(model.RecallSearchHistory(-1));
        Assert.Equal("second old", model.SearchText);
        Assert.True(model.RecallSearchHistory(1));
        Assert.True(model.RecallSearchHistory(1));
        Assert.Equal("second last", model.SearchText);
        model.Commands.AddToHistory("new second tx");
        model.SearchText = "new second search";
        Invoke(model, "AddCurrentSearchTextToHistory");
        Invoke(model, "SwitchPortInputHistory", "COM7");
        Assert.Equal("first new", model.SearchText);
        Assert.Equal("first tx", Assert.Single(model.Commands.CommandHistory).CommandText);
        Invoke(model, "CaptureCurrentPortInputHistory");
        var state = (PortInputHistoryState)GetField(model, "_portInputHistories")!;
        profile.PortHistories = state.Snapshot();
        profile.ActiveHistoryPort = state.ActivePort;
        profile.CommandHistory = model.Commands.GetHistorySnapshot().ToList();
        profile.UiSettings.LastSearchText = model.SearchText;
        profile.UiSettings.SearchHistory = (List<string>)GetField(model, "_searchHistory")!;
        await new ProfileService().SaveAsync(ProfilePath, profile, default);
        var restoredSecond = await LoadForPort(new(), "COM8");
        Assert.Contains(restoredSecond.CommandHistory, entry => entry.CommandText == "new second tx");
        Assert.Contains("new second search", restoredSecond.UiSettings.SearchHistory);
    }

    private async Task SeedPort(string port, string text)
    {
        var service = new ProfileService();
        var profile = await LoadForPort(service, port);
        SetHistory(profile, text);
        await service.SaveAsync(ProfilePath, profile, default);
    }

    private async Task<AppProfile> LoadForPort(ProfileService service, string port)
    {
        var profile = await service.LoadAsync(ProfilePath, default);
        profile.SerialSettings.PortName = port;
        profile.ActiveHistoryPort = port;
        ProfileInputHistory.Apply(profile);
        return profile;
    }

    private static MainViewModel CreateHistoryModel()
    {
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        SetField(model, "_portInputHistories", new PortInputHistoryState());
        SetField(model, "_searchHistory", new List<string>());
        SetField(model, "_searchHistoryCursor", -1);
        SetField(model, "_searchText", string.Empty);
        SetField(model, "_currentUiSettings", new UiSettings());
        SetField(model, "_suppressSettingsApplyRecording", true);
        SetField(model, "<Commands>k__BackingField", new CommandViewModel());
        SetField(model, "<SearchResults>k__BackingField", new BulkObservableCollection<VisibleSearchResult>());
        foreach (var name in new[] { "FindNextCommand", "FindPreviousCommand", "SearchVisibleLogCommand",
                     "RefreshSearchResultsCommand", "PreviousSearchResultsPageCommand", "NextSearchResultsPageCommand" })
            SetField(model, $"<{name}>k__BackingField", new AsyncRelayCommand(() => Task.CompletedTask));
        return model;
    }

    private static void Invoke(MainViewModel model, string name, params object[] arguments) =>
        typeof(MainViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, arguments);
    private static object? GetField(MainViewModel model, string name) =>
        typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model);
    private static void SetField(MainViewModel model, string name, object value) =>
        typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, value);

    private static InstanceInputHistory History(string prefix) => new()
    {
        CommandHistory = [new CommandHistoryEntry { CommandText = prefix + " tx" }],
        SearchHistory = [prefix + " old", prefix + " new"],
        LastSearchText = prefix + " last"
    };

    private static void SetHistory(AppProfile profile, string prefix)
    {
        var history = History(prefix);
        profile.CommandHistory = history.CommandHistory;
        profile.UiSettings.SearchHistory = history.SearchHistory;
        profile.UiSettings.LastSearchText = history.LastSearchText;
    }

    private static void AssertHistory(AppProfile profile, string prefix)
    {
        Assert.Equal(prefix + " tx", Assert.Single(profile.CommandHistory).CommandText);
        Assert.Equal(new[] { prefix + " old", prefix + " new" }, profile.UiSettings.SearchHistory);
        Assert.Equal(prefix + " last", profile.UiSettings.LastSearchText);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
