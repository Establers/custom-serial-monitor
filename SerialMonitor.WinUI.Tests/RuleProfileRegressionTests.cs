using System.Text.Json;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class RuleProfileRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SerialMonitor.Tests", "Rules-" + Guid.NewGuid().ToString("N"));
    private string ProfilePath => Path.Combine(_directory, "profile.json");

    [Theory]
    [InlineData("{\"EventRules\":[],\"HighlightRules\":[]}")]
    [InlineData("{\"EventRules\":[]}")]
    [InlineData("{\"HighlightRules\":[]}")]
    [InlineData("{\"eventRules\":[],\"highlightRules\":[]}")]
    public async Task ExplicitEmptyLegacyRules_StayEmptyAcrossMigrationAndSave(string json)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(ProfilePath, json);
        var service = new ProfileService();
        var loaded = await service.LoadAsync(ProfilePath, default);
        Assert.Empty(loaded.LogRules);
        Assert.Empty(loaded.EventRules);
        Assert.Empty(loaded.HighlightRules);
        await service.SaveAsync(ProfilePath, loaded, default);
        Assert.Empty((await service.LoadAsync(ProfilePath, default)).LogRules);
    }

    [Fact]
    public async Task AbsentRuleConfiguration_StillReceivesDefaults()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(ProfilePath, "{}");
        var service = new ProfileService();
        var loaded = await service.LoadAsync(ProfilePath, default);
        Assert.Equal(service.CreateDefaultProfile().LogRules.Count, loaded.LogRules.Count);
        Assert.NotEmpty(loaded.LogRules);
    }

    [Fact]
    public async Task ExplicitEmptyUnifiedRules_OverrideStaleLegacyRulesAndStayEmpty()
    {
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.LogRules.Clear(); // Deliberately retain stale legacy projections.
        await service.SaveAsync(ProfilePath, profile, default);
        var loaded = await service.LoadAsync(ProfilePath, default);
        Assert.Empty(loaded.LogRules);
        Assert.Empty(loaded.EventRules);
        Assert.Empty(loaded.HighlightRules);
        await service.SaveAsync(ProfilePath, loaded, default);
        Assert.Empty((await service.LoadAsync(ProfilePath, default)).LogRules);
    }

    [Theory]
    [InlineData(LogRuleMatchMode.Terminal, 0)]
    [InlineData(LogRuleMatchMode.Terminal, 1)]
    [InlineData(LogRuleMatchMode.Terminal, 2)]
    [InlineData(LogRuleMatchMode.Hex, 0)]
    [InlineData(LogRuleMatchMode.Hex, 1)]
    [InlineData(LogRuleMatchMode.Hex, 2)]
    public async Task NotificationOnlyRule_PersistsAndDetectsWithoutEventList(LogRuleMatchMode mode, int notification)
    {
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.LogRules = [new LogRule
        {
            Name = "Notify", Keyword = mode == LogRuleMatchMode.Hex ? "46 41 55 4C 54" : "FAULT",
            Mode = mode, UseForEvent = false, UseForHighlight = false,
            TrayNotificationEnabled = notification == 0, SoundNotificationEnabled = notification == 1,
            PopupNotificationEnabled = notification == 2
        }];
        await service.SaveAsync(ProfilePath, profile, default);
        var loaded = await service.LoadAsync(ProfilePath, default);
        Assert.True(Assert.Single(loaded.LogRules).RequiresEventDetection);
        Assert.False(Assert.Single(loaded.EventRules).ShowInEventList);
        await using var detector = new EventDetector();
        await detector.StartAsync(loaded.EventRules, new(), default);
        detector.UpdateRuleMode(mode);
        Assert.True(detector.TryEnqueue(LogLine.Rx("FAULT", "FAULT"u8.ToArray(), contentMode: mode)));
        await detector.StopAsync(default);
        Assert.True(detector.DetectedEvents.TryRead(out var found));
        Assert.False(found.ShowInEventList);
        Assert.True(DetectedEventRoutingPolicy.Decide(found).QueueNotification);
        Assert.Equal(0, detector.ContextCapturesStartedCount);
        Assert.False(detector.SequenceTriggerEvents.TryRead(out _));
    }

    [Theory]
    [InlineData(LogRuleMatchMode.Terminal, true, "ERROR", "error", HighlightMatchDirection.RxOnly, HighlightMatchDirection.RxOnly, 2)]
    [InlineData(LogRuleMatchMode.Terminal, false, "ERROR", "error", HighlightMatchDirection.RxOnly, HighlightMatchDirection.RxOnly, 1)]
    [InlineData(LogRuleMatchMode.Terminal, false, "ERROR", "ERROR", HighlightMatchDirection.RxOnly, HighlightMatchDirection.TxOnly, 2)]
    [InlineData(LogRuleMatchMode.Hex, true, "AA FF", "aa ff", HighlightMatchDirection.RxOnly, HighlightMatchDirection.RxOnly, 1)]
    [InlineData(LogRuleMatchMode.Hex, false, "AA FF", "AA FF", HighlightMatchDirection.RxOnly, HighlightMatchDirection.TxOnly, 2)]
    public async Task LegacyMigration_PreservesDirectionAndModeSpecificCaseSemantics(
        LogRuleMatchMode mode, bool caseSensitive, string eventKeyword, string highlightKeyword,
        HighlightMatchDirection eventDirection, HighlightMatchDirection highlightDirection, int count)
    {
        Directory.CreateDirectory(_directory);
        var json = JsonSerializer.Serialize(new
        {
            EventRules = new[] { new { Name = "Same", Keyword = eventKeyword, Mode = (int)mode,
                CaseSensitive = caseSensitive, MatchDirection = (int)eventDirection } },
            HighlightRules = new[] { new { Name = "Same", Keyword = highlightKeyword, Mode = (int)mode,
                CaseSensitive = caseSensitive, MatchDirection = (int)highlightDirection, ForegroundColor = "Red" } }
        });
        await File.WriteAllTextAsync(ProfilePath, json);
        var service = new ProfileService();
        var loaded = await service.LoadAsync(ProfilePath, default);
        Assert.Equal(count, loaded.LogRules.Count);
        var highlight = Assert.Single(loaded.HighlightRules);
        Assert.Equal(highlightDirection, highlight.MatchDirection);
        Assert.Equal(highlightKeyword, highlight.Keyword, ignoreCase: mode == LogRuleMatchMode.Hex || !caseSensitive);
        Assert.Equal((EventMatchDirection)eventDirection, Assert.Single(loaded.EventRules).MatchDirection);
        await service.SaveAsync(ProfilePath, loaded, default);
        Assert.Equal(count, (await service.LoadAsync(ProfilePath, default)).LogRules.Count);
    }

    [Fact]
    public async Task LegacyMigration_DoesNotCollapseMultipleHighlightPriorities()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(ProfilePath, """
            {"EventRules":[{"Name":"E","Keyword":"ERROR","MatchDirection":"Both"}],
             "HighlightRules":[
               {"Name":"E","Keyword":"ERROR","MatchDirection":"Both","ForegroundColor":"Red","Priority":1},
               {"Name":"E","Keyword":"ERROR","MatchDirection":"Both","ForegroundColor":"Yellow","Priority":9}]}
            """);
        var loaded = await new ProfileService().LoadAsync(ProfilePath, default);
        Assert.Equal(new[] { 1, 9 }, loaded.HighlightRules.Select(rule => rule.Priority));
    }

    [Fact]
    public async Task LegacyKeywordNamedLogRules_DoesNotHideTheMissingUnifiedProperty()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(ProfilePath, """
            {"EventRules":[{"Name":"LogRules","Keyword":"LogRules"}],"HighlightRules":[]}
            """);
        var loaded = await new ProfileService().LoadAsync(ProfilePath, default);
        Assert.Equal("LogRules", Assert.Single(loaded.LogRules).Keyword);
        Assert.Single(loaded.EventRules);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
