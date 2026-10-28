using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Tests;

public sealed class CommandSequenceExecutionPlanTests
{
    [Fact]
    public void RunSkipsDisabledStepsAndKeepsOriginalEditorNumbersAndRepeatCount()
    {
        var source = new CommandSequence
        {
            Name = "Device check", RepeatCount = 4,
            Steps = [new() { CommandText = "A", IsEnabled = false }, new() { CommandText = "B" },
                new() { CommandText = "C", IsEnabled = false }, new() { CommandText = "D" }]
        };

        var plan = CommandSequenceExecutionPlan.Create(source);

        Assert.Equal(4, plan.RepeatCount);
        Assert.Equal(["B", "D"], plan.Steps.Select(step => step.CommandText));
        Assert.Equal([2, 4], plan.Steps.Select(step => step.StepNumber));
        Assert.Equal(4, source.Steps.Count);
        Assert.False(source.Steps[0].IsEnabled);
    }

    [Fact]
    public void RunFreezesEnabledStepSettingsAndSequenceRepeatCount()
    {
        var step = new CommandSequenceStep
        {
            Name = "Read version", CommandText = "  version  ", TxMode = TxSendMode.Terminal,
            LineEndingMode = TxLineEndingMode.Lf, DelayAfterMs = 42, Comment = "note",
            CaptureMs = 321, Expect = new AutomationResponseExpectation("text", "v1")
        };
        var source = new CommandSequence
        {
            Name = "Repeat", RepeatCount = 9999,
            Steps = [new() { CommandText = "disabled invalid HEX", TxMode = TxSendMode.Hex, IsEnabled = false }, step]
        };

        var plan = CommandSequenceExecutionPlan.Create(source);
        var actual = Assert.Single(plan.Steps);
        step.CommandText = "changed";
        step.IsEnabled = false;
        step.DelayAfterMs = 900;
        source.RepeatCount = 1;
        source.Steps.Clear();

        Assert.Equal(9999, plan.RepeatCount);
        Assert.Equal(2, actual.StepNumber);
        Assert.True(actual.IsEnabled);
        Assert.Equal("  version  ", actual.CommandText);
        Assert.Equal(TxSendMode.Terminal, actual.TxMode);
        Assert.Equal(TxLineEndingMode.Lf, actual.LineEndingMode);
        Assert.Equal(42, actual.DelayAfterMs);
        Assert.Equal("note", actual.Comment);
        Assert.Equal(321, actual.CaptureMs);
        Assert.Equal(new AutomationResponseExpectation("text", "v1"), actual.Expect);
        Assert.NotSame(step.Expect, actual.Expect);
    }

    [Fact]
    public void DuplicateSnapshotRetainsDisabledStateAndMetadataWithoutSharingObjects()
    {
        var source = new CommandSequenceStep
        {
            IsEnabled = false, CommandText = "AA 01", TxMode = TxSendMode.Hex,
            Name = "Status", Comment = "Keep", CaptureMs = 123, Expect = new("hex", "FF")
        };
        var clone = CommandSequenceExecutionPlan.CloneStep(source);
        Assert.False(clone.IsEnabled);
        Assert.Equal(source.Expect, clone.Expect);
        Assert.NotSame(source.Expect, clone.Expect);
        clone.IsEnabled = true;
        clone.CommandText = "AA 02";
        Assert.False(source.IsEnabled);
        Assert.Equal("AA 01", source.CommandText);
    }
}
