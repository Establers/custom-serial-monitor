using System.Text.Json;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SequenceAiExampleTests
{
    [Fact]
    public async Task BundledGuiRxExampleLoadsAndKeepsPatternsOutOfTransmitFields()
    {
        var example = Assert.Single(await new CommandSequenceFileService().LoadAsync(
            [Path.Combine(AppContext.BaseDirectory, "docs", "sequence_rx_example.json")], default));

        Assert.Equal(1, example.RepeatCount);
        Assert.Equal(new[] { CommandSequenceStepKind.Tx, CommandSequenceStepKind.RxCheck,
            CommandSequenceStepKind.Tx, CommandSequenceStepKind.RxCheck }, example.Steps.Select(step => step.Kind));
        Assert.Equal(new[] { "status", "STATE=READY", "AA 10", "AA 55 ?? 00" },
            example.Steps.Select(step => step.CommandText));
        Assert.All(example.Steps.Where(step => step.Kind == CommandSequenceStepKind.RxCheck), step =>
        {
            Assert.Equal("", step.StoredCommandText);
            Assert.Null(step.TxMode);
            Assert.Equal(1000, step.DelayAfterMs);
        });

        using var exported = JsonDocument.Parse(CommandSequenceFileService.Serialize(example));
        Assert.Equal(3, exported.RootElement.GetProperty("FormatVersion").GetInt32());
        var rows = exported.RootElement.GetProperty("Steps");
        Assert.Equal("", rows[1].GetProperty("CommandText").GetString());
        Assert.Equal("STATE=READY", rows[1].GetProperty("RxPattern").GetString());
        Assert.Equal("", rows[3].GetProperty("CommandText").GetString());
        Assert.Equal("AA 55 ?? 00", rows[3].GetProperty("RxPattern").GetString());
        var copy = CommandSequenceFileService.Parse(exported.RootElement);
        Assert.Equal(example.Steps.Select(step => step.CommandText), copy.Steps.Select(step => step.CommandText));
    }

    [Fact]
    public async Task BundledExamplesKeepLegacyAutomationAndRejectActiveGuiRxChecks()
    {
        var examples = await new CommandSequenceFileService().LoadAsync(
            [Path.Combine(AppContext.BaseDirectory, "docs", "sequence_examples.json"),
             Path.Combine(AppContext.BaseDirectory, "docs", "sequence_rx_example.json")], default);

        var legacy = AutomationSequenceMapper.Snapshot(examples[0], TxSendMode.Terminal, TxLineEndingMode.Crlf);
        Assert.Equal(3, legacy.RepeatCount);
        Assert.Equal(new[] { "status", "version" }, legacy.Steps.Select(step => step.Command));
        Assert.Equal(new[] { 500, 1000 }, legacy.Steps.Select(step => step.CaptureMs));
        var error = Assert.Throws<AutomationException>(() =>
            AutomationSequenceMapper.Snapshot(examples[1], TxSendMode.Terminal, TxLineEndingMode.Crlf));
        Assert.Equal("unsupported_sequence_feature", error.Code);

        foreach (var step in examples[1].Steps.Where(step => step.Kind == CommandSequenceStepKind.RxCheck))
            step.IsEnabled = false;
        var txOnly = AutomationSequenceMapper.Snapshot(examples[1], TxSendMode.Terminal, TxLineEndingMode.Crlf);
        Assert.Equal(new[] { "status", "AA 10" }, txOnly.Steps.Select(step => step.Command));
        Assert.Equal(new[] { "text", "hex" }, txOnly.Steps.Select(step => step.TxMode));
    }
}
