using System.Text.Json;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Tests;

public sealed class SequenceStepEditSessionTests
{
    [Theory]
    [InlineData(TxSendMode.Hex)]
    [InlineData(TxSendMode.Terminal)]
    public void FollowingTxModeThenCancelingPreservesTheOriginalRxStep(TxSendMode mode)
    {
        var original = new CommandSequenceStep { Kind = CommandSequenceStepKind.RxCheck,
            CommandText = "41", TxMode = mode, DelayAfterMs = 700, Name = "RX", Comment = "keep",
            CaptureMs = 12, Expect = new("text", "legacy") };
        var before = JsonSerializer.Serialize(original);
        var edit = new SequenceStepEditSession(original);
        edit.FollowTxMode();
        edit.Draft.CommandText = "changed";
        edit.Draft.Name = "unsaved";
        edit.Draft.DelayAfterMs = 42;
        // Cancel closes the dialog without requesting/applying CreateResult.
        Assert.Null(edit.Draft.TxMode);
        Assert.Equal(before, JsonSerializer.Serialize(original));
        Assert.Equal(mode, CommandSequenceExecutionPlan.CloneStep(original).TxMode);
    }

    [Fact]
    public void SavingReturnsAnIndependentResultWithTheChosenFollowMode()
    {
        var original = new CommandSequenceStep { Kind = CommandSequenceStepKind.RxCheck,
            CommandText = "READY", TxMode = TxSendMode.Terminal, Expect = new("text", "legacy") };
        var edit = new SequenceStepEditSession(original);
        edit.FollowTxMode();
        var result = edit.CreateResult();
        Assert.Null(result.TxMode);
        Assert.Equal(TxSendMode.Terminal, original.TxMode);
        Assert.Equal(original.Kind, result.Kind);
        Assert.Equal("READY", result.CommandText);
        Assert.NotSame(edit.Draft, result);
        Assert.NotSame(original.Expect, result.Expect);
        result.CommandText = "saved change";
        Assert.Equal("READY", original.CommandText);
        Assert.Equal("READY", edit.Draft.CommandText);
    }
}
