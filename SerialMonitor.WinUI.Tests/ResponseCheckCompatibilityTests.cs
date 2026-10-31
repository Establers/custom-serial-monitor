using System.Text.Json;
using SerialMonitor.Core.Automation;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class ResponseCheckCompatibilityTests
{
    [Fact]
    public void LegacyJsonAndAutomationMetadataKeepTheirOldMeaning()
    {
        var sequence = new CommandSequence { Name = "Legacy", Steps = [new() { CommandText = "status", CaptureMs = 3456, Expect = new("hex", "AA 00") }] };
        using var json = JsonDocument.Parse(CommandSequenceFileService.Serialize(sequence));
        Assert.Equal(1, json.RootElement.GetProperty("FormatVersion").GetInt32());
        var copy = CommandSequenceFileService.Parse(json.RootElement);
        Assert.Null(copy.Steps[0].ResponseCheck);
        var step = Assert.Single(AutomationSequenceMapper.Snapshot(copy, TxSendMode.Terminal, TxLineEndingMode.Crlf).Steps);
        Assert.Equal(3456, step.CaptureMs);
        Assert.Equal(new AutomationResponseExpectation("hex", "AA 00"), step.Expect);
    }

    [Fact]
    public void V2PreservesWildcardConditionAndLegacyCliCannotSilentlyIgnoreIt()
    {
        var sequence = new CommandSequence { Name = "Live", Steps = [new() { CommandText = "AA 10", TxMode = TxSendMode.Hex,
            ResponseCheck = new("hex", "AA 55 ?? 00", 1234) }] };
        using var json = JsonDocument.Parse(CommandSequenceFileService.Serialize(sequence));
        Assert.Equal(3, json.RootElement.GetProperty("FormatVersion").GetInt32());
        var copy = CommandSequenceFileService.Parse(json.RootElement);
        Assert.Null(copy.Steps[0].ResponseCheck);
        Assert.Equal(CommandSequenceStepKind.RxCheck, copy.Steps[1].Kind);
        Assert.Equal("AA 55 ?? 00", copy.Steps[1].CommandText);
        Assert.Equal(1234, copy.Steps[1].DelayAfterMs);
        var error = Assert.Throws<AutomationException>(() => AutomationSequenceMapper.Snapshot(copy, TxSendMode.Hex, TxLineEndingMode.Crlf));
        Assert.Equal("unsupported_sequence_feature", error.Code);
        copy.Steps[0].IsEnabled = false; copy.Steps[1].IsEnabled = false;
        Assert.Empty(AutomationSequenceMapper.Snapshot(copy, TxSendMode.Hex, TxLineEndingMode.None).Steps);
    }

    [Fact]
    public void CloneAndProfileMergeKeepResponseConditionsAndDetectConcurrentEdits()
    {
        var sequence = new CommandSequence { Name = "Live", Steps = [new() { CommandText = "status", ResponseCheck = new("text", "READY") }] };
        var baseline = ProfileSequenceMerge.Snapshot(new[] { sequence });
        var copy = ProfileSequenceMerge.Clone(new[] { sequence });
        var plan = CommandSequenceExecutionPlan.Create(sequence);
        Assert.Equal(sequence.Steps[0].ResponseCheck, plan.Steps[0].ResponseCheck);
        Assert.NotSame(sequence.Steps[0].ResponseCheck, plan.Steps[0].ResponseCheck);
        sequence.Steps[0].ResponseCheck = new("text", "OK");
        copy[0].Steps[0].ResponseCheck = new("text", "DONE");
        Assert.Throws<ProfileConflictException>(() => ProfileSequenceMerge.Merge(baseline, new[] { sequence }, copy));
    }

    [Theory]
    [InlineData("AA ? 00")][InlineData("?? ??")]
    public void MalformedV2IsRejectedRatherThanRunningWithoutChecking(string pattern)
    {
        var sequence = new CommandSequence { Name = "Invalid", Steps = [new() { CommandText = "status", ResponseCheck = new("hex", pattern) }] };
        Assert.Throws<InvalidDataException>(() => CommandSequenceFileService.Serialize(sequence));
    }

    [Fact]
    public async Task ProfileSaveLoadKeepsLiveAndLegacyConditionsSeparate()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ResponseProfileTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var service = new ProfileService(); var profile = service.CreateDefaultProfile();
            profile.CommandSequences = [new() { Name = "Live", Steps = [new() { CommandText = "status",
                Expect = new("text", "old"), CaptureMs = 987, ResponseCheck = new("hex", "AA ?? 00", 456) }] }];
            var path = Path.Combine(folder, "profile.json");
            await service.SaveAsync(path, profile, default);
            var steps = Assert.Single((await service.LoadAsync(path, default)).CommandSequences).Steps;
            var restored = steps[0];
            Assert.Null(restored.ResponseCheck);
            Assert.Equal(CommandSequenceStepKind.RxCheck, steps[1].Kind);
            Assert.Equal("AA ?? 00", steps[1].CommandText); Assert.Equal(456, steps[1].DelayAfterMs);
            Assert.Equal(987, restored.CaptureMs); Assert.Equal("old", restored.Expect!.Value);
        }
        finally { Directory.Delete(folder, true); }
    }
}
