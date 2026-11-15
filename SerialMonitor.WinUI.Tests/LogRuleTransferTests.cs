using System.Text;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogRuleTransferTests
{
    [Fact]
    public void TextRoundTrip_PreservesUnicodeWhitespaceAndEverySetting()
    {
        var rule = new LogRule { Name = "온도 ⚠", Keyword = @" temp=\d+ ", UseRegex = true,
            Enabled = false, CaseSensitive = true, UseForEvent = true, UseForHighlight = false, UseAsViewFilter = true,
            MatchDirection = HighlightMatchDirection.RxOnly, ForegroundColor = "Orange", BackgroundColor = "Blue",
            Priority = int.MaxValue, NotificationCooldownSeconds = 86400, TrayNotificationEnabled = true,
            SoundNotificationEnabled = true, PopupNotificationEnabled = true, TriggerSequenceName = "센서 조회" };
        var text = LogRuleTransfer.Encode([rule]);
        var restored = Assert.Single(LogRuleTransfer.Decode(" \r\n" + text + "\r\n "));
        Assert.Equal(text, LogRuleTransfer.Encode([restored]));
        Assert.Equal(rule.Keyword, restored.Keyword);
        Assert.True(restored.UseRegex);
        Assert.Equal("센서 조회", restored.TriggerSequenceName);
    }

    [Theory]
    [InlineData("SMRULES:2:abcd")]
    [InlineData("SMRULES:1:!!!!!")]
    [InlineData("{\"LogRules\":[]}")]
    public void DamagedOrUnsupportedText_IsRejected(string text) => Assert.Throws<FormatException>(() => LogRuleTransfer.Decode(text));

    [Theory]
    [InlineData("[null]")]
    [InlineData("[]")]
    [InlineData("[{\"Keyword\":\"ERROR\",\"Mode\":999}]")]
    [InlineData("[{\"Keyword\":\"ERROR\",\"NotificationCooldownSeconds\":0}]")]
    [InlineData("[{\"Keyword\":\"ERROR\",\"UnknownSetting\":true}]")]
    [InlineData("[{\"Keyword\":\"A\",\"Mode\":1}]")]
    public void InvalidBatch_IsRejectedAtomically(string json) => Assert.Throws<FormatException>(() =>
        LogRuleTransfer.Decode(LogRuleTransfer.Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))));

    [Fact]
    public void Import_IsAdditiveIdempotent_AndDisablesAutomaticSequenceExecution()
    {
        var existing = new LogRule { Name = "existing", Keyword = "ERROR" };
        var automatic = new LogRule { Name = "new", Keyword = "FAULT", TriggerSequenceName = "Reset" };
        var result = LogRuleTransfer.PrepareImport([existing], [existing.Clone(), automatic, automatic.Clone()]);
        Assert.Equal(2, result.Duplicates);
        Assert.Equal(1, result.DisabledTriggers);
        Assert.False(Assert.Single(result.Added).Enabled);
        Assert.True(automatic.Enabled);
        var second = LogRuleTransfer.PrepareImport(new[] { existing }.Concat(result.Added), [existing, automatic]);
        Assert.Empty(second.Added);
        Assert.Equal(2, second.Duplicates);
    }

    [Fact]
    public async Task ProfileRoundTrip_PreservesSignificantSpaces_AndProjectsRegex()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SerialMonitor.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "profile.json");
        try
        {
            var service = new ProfileService();
            var profile = service.CreateDefaultProfile();
            profile.LogRules = [new LogRule { Name = "space", Keyword = " ERROR ", UseForEvent = true },
                new LogRule { Name = "regex", Keyword = @" temp=\d+ ", UseRegex = true, UseForEvent = true }];
            await service.SaveAsync(path, profile, default);
            var loaded = await service.LoadAsync(path, default);
            Assert.Equal(" ERROR ", loaded.LogRules[0].Keyword);
            Assert.Equal(@" temp=\d+ ", loaded.LogRules[1].Keyword);
            Assert.True(loaded.EventRules[1].UseRegex);
            Assert.True(loaded.HighlightRules[1].UseRegex);
            Assert.NotEqual(MainViewModel.BuildViewFilterKey(loaded.LogRules[0]),
                MainViewModel.BuildViewFilterKey(new LogRule { Name = "space", Keyword = "ERROR" }));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task InvalidProfileRegex_IsPreservedDisabledForRepair()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SerialMonitor.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "profile.json");
        try
        {
            var service = new ProfileService();
            var profile = service.CreateDefaultProfile();
            profile.LogRules = [new LogRule { Name = "repair", Keyword = "[", UseRegex = true }];
            await service.SaveAsync(path, profile, default);
            var loaded = await service.LoadAsync(path, default);
            var rule = Assert.Single(loaded.LogRules);
            Assert.False(rule.Enabled);
            Assert.True(rule.UseRegex);
            Assert.Equal("[", rule.Keyword);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
