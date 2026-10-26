using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Tests;

public sealed class AutomationSequenceMapperTests
{
    [Fact]
    public void RegisteredDefinitionKeepsExplicitBytesAndEvidenceSettingsAcrossGuiModes()
    {
        var original = new AutomationSequenceDefinition("Device check", 2,
        [
            new("Text", "  status\t", "text", "lf", 250, 30, new("text", "abcd")),
            new("Bytes", "00 ff", "hex", "none", 100, 0, new("hex", "00 FF"))
        ]);

        var gui = AutomationSequenceMapper.Create(original);
        var snapshot = AutomationSequenceMapper.Snapshot(gui, TxSendMode.Hex, TxLineEndingMode.Cr);

        Assert.Equal(original.Name, snapshot.Name);
        Assert.Equal(2, snapshot.RepeatCount);
        Assert.Equal(original.Steps, snapshot.Steps);
        Assert.Equal(new[] { 1, 2 }, gui.Steps.Select(step => step.StepNumber));
        Assert.Equal(TxSendMode.Terminal, gui.Steps[0].TxMode);
        Assert.Equal("  status\t", gui.Steps[0].CommandText);
    }

    [Fact]
    public void LegacyGuiDefaultsAreResolvedOnceAndSnapshotIsDetachedFromEdits()
    {
        var gui = new CommandSequence
        {
            Name = "Legacy",
            Steps = [new() { CommandText = "AA 55", DelayAfterMs = 50 }]
        };
        var snapshot = AutomationSequenceMapper.Snapshot(gui, TxSendMode.Hex, TxLineEndingMode.Crlf);

        gui.Name = "Edited";
        gui.Steps[0].CommandText = "BB";
        gui.Steps[0].LineEndingMode = TxLineEndingMode.None;
        gui.Steps.Clear();

        Assert.Equal("Legacy", snapshot.Name);
        var step = Assert.Single(snapshot.Steps);
        Assert.Equal("AA 55", step.Command);
        Assert.Equal("hex", step.TxMode);
        Assert.Equal("crlf", step.LineEnding);
        Assert.Equal(2000, step.CaptureMs);
        Assert.Null(step.Expect);
    }
}
