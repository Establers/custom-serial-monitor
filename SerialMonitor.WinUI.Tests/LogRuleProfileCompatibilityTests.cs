using System.Text.Json;
using System.Text.Json.Serialization;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogRuleProfileCompatibilityTests
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    [Fact]
    public void LegacyMatchMode_LoadsForAllRuleModels()
    {
        var logRule = JsonSerializer.Deserialize<LogRule>(
            """{"Name":"legacy","MatchMode":"Text"}""",
            Options);
        var eventRule = JsonSerializer.Deserialize<EventRule>(
            """{"Name":"legacy","MatchMode":"Hex"}""",
            Options);
        var highlightRule = JsonSerializer.Deserialize<HighlightRule>(
            """{"Name":"legacy","MatchMode":"Text"}""",
            Options);

        Assert.NotNull(logRule);
        Assert.NotNull(eventRule);
        Assert.NotNull(highlightRule);
        Assert.Equal(LogRuleMatchMode.Terminal, logRule.Mode);
        Assert.Equal(LogRuleMatchMode.Hex, eventRule.Mode);
        Assert.Equal(LogRuleMatchMode.Terminal, highlightRule.Mode);
    }

    [Fact]
    public void NewProfile_WritesModeTerminalWithoutLegacyMatchField()
    {
        var json = JsonSerializer.Serialize(
            new LogRule { Name = "terminal", Mode = LogRuleMatchMode.Terminal },
            Options);

        Assert.Contains("\"Mode\":\"Terminal\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("MatchMode", json, StringComparison.Ordinal);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
