using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Tests;

public sealed class SingleLineCommandTextTests
{
    [Theory]
    [InlineData("aaaaaaa\rbbbbbb")]
    [InlineData("aaaaaaa\nbbbbbb")]
    [InlineData("aaaaaaa\r\nbbbbbb")]
    [InlineData("aaaaaaa\u2028bbbbbb")]
    [InlineData("aaaaaaa\u2029bbbbbb")]
    [InlineData("aaaaaaa\u0085bbbbbb")]
    [InlineData("aaaaaaa\fbbbbbb")]
    public void ReopeningOldTxOrRxPatternShowsBothLinesWithoutHiddenControlCharacters(string stored)
    {
        Assert.True(SingleLineCommandText.HasLineBreaks(stored));
        var visible = SingleLineCommandText.ForEditing(stored);
        Assert.Equal("aaaaaaa bbbbbb", visible);
        Assert.False(SingleLineCommandText.HasLineBreaks(visible));
    }

    [Fact]
    public void SingleLineEditingPreservesMeaningfulSpacesAndLiteralBackslashes()
    {
        const string text = "  aaaaaaa\\rbbbbbb  ";
        Assert.False(SingleLineCommandText.HasLineBreaks(text));
        Assert.Equal(text, SingleLineCommandText.ForEditing(text));
    }

    [Fact]
    public void GuiValidationBlocksEmbeddedTerminalNewlinesButKeepsSeparateLineEndingsAndHexFormatting()
    {
        var step = new CommandSequenceStep { CommandText = "aaaaaaa\rbbbbbb" };
        Assert.True(SingleLineCommandText.IsMultilineTerminalTx(step, TxSendMode.Terminal));
        step.TxMode = TxSendMode.Terminal;
        Assert.True(SingleLineCommandText.IsMultilineTerminalTx(step, TxSendMode.Hex));
        step.CommandText = "status"; step.LineEndingMode = TxLineEndingMode.Crlf;
        Assert.False(SingleLineCommandText.IsMultilineTerminalTx(step, TxSendMode.Terminal));
        step.CommandText = "AA\r\nBB"; step.TxMode = TxSendMode.Hex;
        Assert.False(SingleLineCommandText.IsMultilineTerminalTx(step, TxSendMode.Terminal));
        step.Kind = CommandSequenceStepKind.RxCheck; step.TxMode = TxSendMode.Terminal;
        Assert.False(SingleLineCommandText.IsMultilineTerminalTx(step, TxSendMode.Terminal));
    }
}
