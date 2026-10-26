using System.Collections.ObjectModel;
using System.Text.Json;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class CommandSequenceAutomationMetadataTests
{
    private static CommandSequence Parse(string metadata = "", string commandJson = "\"  status  \"")
    {
        using var document = JsonDocument.Parse("{\"FormatVersion\":1,\"Name\":\"Check\",\"RepeatCount\":1,\"Steps\":[{" +
            "\"CommandText\":" + commandJson + ",\"LineEndingMode\":\"None\",\"DelayAfterMs\":0" + metadata + "}]}");
        return CommandSequenceFileService.Parse(document.RootElement);
    }

    [Fact]
    public void LegacyFilesKeepGlobalModeDefaultsAndTrimming()
    {
        var step = Assert.Single(Parse().Steps);
        Assert.Null(step.TxMode);
        Assert.Null(step.Expect);
        Assert.Equal(2000, step.CaptureMs);
        Assert.Equal("status", step.CommandText);
    }

    [Theory]
    [InlineData("Terminal", "  status\r\n  ")]
    [InlineData("Hex", "  AA 01 FF  ")]
    public void ExplicitModeAndMetadataSurviveFileRoundTripWithoutTrimming(string mode, string command)
    {
        var sequence = Parse(",\"TxMode\":\"" + mode + "\",\"CaptureMs\":3456,\"Expect\":{\"Kind\":\"text\",\"Value\":\" OK\\r\\n\"}",
            JsonSerializer.Serialize(command));
        var exported = CommandSequenceFileService.Serialize(sequence);
        using var document = JsonDocument.Parse(exported);
        var restored = Assert.Single(CommandSequenceFileService.Parse(document.RootElement).Steps);
        Assert.Equal(command, restored.CommandText);
        Assert.Equal(Enum.Parse<TxSendMode>(mode), restored.TxMode);
        Assert.Equal(3456, restored.CaptureMs);
        Assert.Equal(new AutomationResponseExpectation("text", " OK\r\n"), restored.Expect);
        Assert.Contains("CLI/MCP only", restored.RowToolTip);
        Assert.DoesNotContain("AutomationHelpText", exported);
    }

    [Theory]
    [InlineData(",\"TxMode\":\"hex\"")]
    [InlineData(",\"TxMode\":\"Escaped\"")]
    [InlineData(",\"TxMode\":3")]
    [InlineData(",\"CaptureMs\":-1")]
    [InlineData(",\"CaptureMs\":60001")]
    [InlineData(",\"CaptureMs\":\"1\"")]
    [InlineData(",\"CaptureMs\":null")]
    [InlineData(",\"Expect\":[]")]
    [InlineData(",\"Expect\":{\"Kind\":\"regex\",\"Value\":\"OK\"}")]
    [InlineData(",\"Expect\":{\"Kind\":\"text\",\"Value\":\"\"}")]
    [InlineData(",\"Expect\":{\"Kind\":\"hex\",\"Value\":\"ABC\"}")]
    [InlineData(",\"Expect\":{\"Kind\":\"hex\",\"Value\":\"GG\"}")]
    [InlineData(",\"Expect\":{\"Kind\":\"text\",\"Value\":\"OK\",\"Regex\":true}")]
    [InlineData(",\"Expect\":{\"Kind\":\"text\",\"Kind\":\"hex\",\"Value\":\"00\"}")]
    public void InvalidAutomationMetadataIsRejectedWithoutPartialImport(string metadata) =>
        Assert.Throws<InvalidDataException>(() => Parse(metadata));

    [Fact]
    public void LiteralWhitespaceExpectationAndExplicitTextCommandRemainSignificant()
    {
        var step = Assert.Single(Parse(",\"TxMode\":\"Terminal\",\"CaptureMs\":0,\"Expect\":{\"Kind\":\"text\",\"Value\":\" \"}", "\" \"").Steps);
        Assert.Equal(" ", step.CommandText);
        Assert.Equal(" ", step.Expect!.Value);
        Assert.Equal(0, step.CaptureMs);
        Assert.Throws<InvalidDataException>(() => Parse("", "\" \""));
    }

    [Fact]
    public async Task ProfileRoundTripPreservesAutomationFieldsAndExplicitCommandWhitespace()
    {
        var path = NewPath();
        try
        {
            var service = new ProfileService();
            var profile = service.CreateDefaultProfile();
            profile.CommandSequences = [new CommandSequence
            {
                Name = "Mixed",
                Steps = new ObservableCollection<CommandSequenceStep>
                {
                    new() { CommandText = "  status\r\n ", TxMode = TxSendMode.Terminal, CaptureMs = 42,
                        Expect = new("text", " OK ") },
                    new() { CommandText = " AA 01 ", TxMode = TxSendMode.Hex, CaptureMs = 0,
                        Expect = new("hex", "FF 00") },
                    new() { CommandText = "  legacy  " }
                }
            }];
            await service.SaveAsync(path, profile, default);
            var loaded = await service.LoadAsync(path, default);
            var steps = Assert.Single(loaded.CommandSequences).Steps;
            Assert.Equal("  status\r\n ", steps[0].CommandText);
            Assert.Equal(TxSendMode.Terminal, steps[0].TxMode);
            Assert.Equal(42, steps[0].CaptureMs);
            Assert.Equal(new AutomationResponseExpectation("text", " OK "), steps[0].Expect);
            Assert.Equal(" AA 01 ", steps[1].CommandText);
            Assert.Equal(TxSendMode.Hex, steps[1].TxMode);
            Assert.Equal(0, steps[1].CaptureMs);
            Assert.Equal(new AutomationResponseExpectation("hex", "FF 00"), steps[1].Expect);
            Assert.Equal("legacy", steps[2].CommandText);
            Assert.Null(steps[2].TxMode);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task InvalidProfileMetadataIsNormalizedWithoutBreakingProfileLoad()
    {
        var path = NewPath();
        try
        {
            var service = new ProfileService();
            var profile = service.CreateDefaultProfile();
            profile.CommandSequences = [new CommandSequence
            {
                Name = "Invalid metadata", Steps = new ObservableCollection<CommandSequenceStep>
                { new() { CommandText = "status", TxMode = (TxSendMode)999, CaptureMs = 60001, Expect = new("regex", "OK") } }
            }];
            await service.SaveAsync(path, profile, default);
            var loaded = await service.LoadAsync(path, default);
            var step = Assert.Single(Assert.Single(loaded.CommandSequences).Steps);
            Assert.Null(step.TxMode);
            Assert.Equal(60000, step.CaptureMs);
            Assert.Null(step.Expect);
            Assert.Equal(0, service.LoadErrorCount);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    private static string NewPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SerialMonitorMetadataTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "profile.json");
    }
}
