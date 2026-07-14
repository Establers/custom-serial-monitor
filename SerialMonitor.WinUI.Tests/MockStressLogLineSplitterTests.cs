using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Tests;

public sealed class MockStressLogLineSplitterTests
{
    [Fact]
    public void HexBurst_ExposesEveryContainedMockLine()
    {
        var text = string.Join("\r\n", Enumerable.Range(1, 5).Select(CreateMockLine)) + "\r\n";
        var line = LogLine.Rx(
            text,
            rawBytes: System.Text.Encoding.UTF8.GetBytes(text),
            contentMode: LogRuleMatchMode.Hex);

        var lines = MockStressLogLineSplitter.Split(line).ToArray();

        Assert.Equal(Enumerable.Range(1, 5).Select(CreateMockLine), lines);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public void HexBurst_SupportsEveryLineEnding(string lineEnding)
    {
        var text = $"{CreateMockLine(41)}{lineEnding}{CreateMockLine(42)}{lineEnding}";
        var line = LogLine.Rx(text, contentMode: LogRuleMatchMode.Hex);

        var lines = MockStressLogLineSplitter.Split(line).ToArray();

        Assert.Equal(new[] { CreateMockLine(41), CreateMockLine(42) }, lines);
    }

    [Fact]
    public void TerminalLine_RemainsOneCandidate()
    {
        const string text = "000001 INFO mock serial sample";
        var line = LogLine.Rx(text, contentMode: LogRuleMatchMode.Terminal);

        var lines = MockStressLogLineSplitter.Split(line).ToArray();

        Assert.Equal(new[] { text }, lines);
    }

    private static string CreateMockLine(int sequence) =>
        $"{sequence:D6} INFO mock serial sample";
}
