using System.Text.Json;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class CommandSequenceEnabledTests
{
    private const string LegacyJson = """
        {"FormatVersion":1,"Name":"Check","RepeatCount":2,"Steps":[
          {"CommandText":"status","LineEndingMode":"Crlf","DelayAfterMs":500}]}
        """;

    [Fact]
    public void LegacyFileStepsRemainEnabledAndDisabledStateSurvivesExport()
    {
        using var input = JsonDocument.Parse(LegacyJson);
        var sequence = CommandSequenceFileService.Parse(input.RootElement);
        var step = Assert.Single(sequence.Steps);
        Assert.True(step.IsEnabled);
        step.IsEnabled = false;

        using var output = JsonDocument.Parse(CommandSequenceFileService.Serialize(sequence));
        var restored = Assert.Single(CommandSequenceFileService.Parse(output.RootElement).Steps);
        Assert.False(restored.IsEnabled);
        Assert.Equal("status", restored.CommandText);
        Assert.Equal(500, restored.DelayAfterMs);
        Assert.False(CommandSequenceTriggerPolicy.CanUseAsTrigger(sequence));
        step.IsEnabled = true;
        Assert.True(CommandSequenceTriggerPolicy.CanUseAsTrigger(sequence));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("\"false\"")]
    [InlineData("[]")]
    public void IsEnabledRequiresBooleanInStrictSequenceFiles(string value)
    {
        using var input = JsonDocument.Parse(LegacyJson.Replace("\"CommandText\"", "\"IsEnabled\":" + value + ",\"CommandText\""));
        Assert.Throws<InvalidDataException>(() => CommandSequenceFileService.Parse(input.RootElement));
    }

    [Fact]
    public void AutomationProjectionOmitsDisabledCommandsAndTheirDelaysWithoutChangingSavedDefinition()
    {
        var sequence = new CommandSequence
        {
            Name = "Check",
            RepeatCount = 2,
            Steps =
            [
                new() { CommandText = "disabled", IsEnabled = false, DelayAfterMs = 600000 },
                new() { CommandText = "status", DelayAfterMs = 0, CaptureMs = 0 },
                new() { CommandText = "later", IsEnabled = false }
            ]
        };

        var execution = AutomationSequenceValidation.Validate(AutomationSequenceMapper.Snapshot(sequence, TxSendMode.Terminal, TxLineEndingMode.Crlf));
        Assert.Equal("status", Assert.Single(execution.Steps).Command);
        Assert.Equal(2, execution.RepeatCount);
        Assert.Equal(3, sequence.Steps.Count);
        Assert.False(sequence.Steps[0].IsEnabled);

        sequence.Steps[1].IsEnabled = false;
        var disabled = AutomationSequenceMapper.Snapshot(sequence, TxSendMode.Terminal, TxLineEndingMode.Crlf);
        Assert.Empty(disabled.Steps);
        Assert.Throws<AutomationException>(() => AutomationSequenceValidation.Validate(disabled));
    }

    [Fact]
    public async Task ProfileRoundTripAndStaleWindowSavePreserveDisabledStepsAndExistingRuleLinks()
    {
        var folder = CreateTestFolder();
        var path = Path.Combine(folder, "profile.json");
        try
        {
            var service = new ProfileService();
            var initial = service.CreateDefaultProfile();
            initial.CommandSequences = [new() { Name = "Check", Steps = [new() { CommandText = "status" }] }];
            initial.LogRules = [new() { Name = "Boot", Keyword = "READY", TriggerSequenceName = "Check", MatchDirection = HighlightMatchDirection.RxOnly }];
            await service.SaveAsync(path, initial, default);

            var editor = new ProfileService();
            var staleWindow = new ProfileService();
            var edited = await editor.LoadAsync(path, default);
            var stale = await staleWindow.LoadAsync(path, default);
            edited.CommandSequences[0].Steps[0].IsEnabled = false;
            await editor.SaveAsync(path, edited, default);
            stale.UiSettings.LastSearchText = "other window";
            await staleWindow.SaveAsync(path, stale, default);

            var restored = await new ProfileService().LoadAsync(path, default);
            Assert.False(Assert.Single(Assert.Single(restored.CommandSequences).Steps).IsEnabled);
            Assert.Equal("Check", Assert.Single(restored.LogRules).TriggerSequenceName);
            Assert.Equal("other window", restored.UiSettings.LastSearchText);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task EnabledToggleConflictsWithAnotherWindowsDefinitionEditInsteadOfSilentlyOverwriting()
    {
        var folder = CreateTestFolder();
        var path = Path.Combine(folder, "profile.json");
        try
        {
            var seed = new ProfileService();
            var initial = seed.CreateDefaultProfile();
            initial.CommandSequences = [new() { Name = "Check", Steps = [new() { CommandText = "status" }] }];
            await seed.SaveAsync(path, initial, default);

            var first = new ProfileService();
            var second = new ProfileService();
            var a = await first.LoadAsync(path, default);
            var b = await second.LoadAsync(path, default);
            a.CommandSequences[0].Steps[0].IsEnabled = false;
            await first.SaveAsync(path, a, default);
            var committed = await File.ReadAllBytesAsync(path);
            b.CommandSequences[0].Steps[0].CommandText = "version";

            await Assert.ThrowsAsync<ProfileConflictException>(() => second.SaveAsync(path, b, default));
            Assert.Equal(committed, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task MissingProfilePropertyDefaultsToEnabled()
    {
        var folder = CreateTestFolder();
        var path = Path.Combine(folder, "profile.json");
        try
        {
            var service = new ProfileService();
            var profile = service.CreateDefaultProfile();
            profile.CommandSequences = [new() { Name = "Legacy", Steps = [new() { CommandText = "status" }] }];
            await service.SaveAsync(path, profile, default);
            var json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"IsEnabled\": true,", json);
            // Legacy profiles predate the property entirely.
            await File.WriteAllTextAsync(path, json.Replace("\"IsEnabled\": true,", string.Empty));
            var restored = await new ProfileService().LoadAsync(path, default);
            Assert.True(Assert.Single(Assert.Single(restored.CommandSequences).Steps).IsEnabled);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static string CreateTestFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SerialMonitorEnabledTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
