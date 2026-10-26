using System.Text;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed class AutomationSequenceMatcherTests
{
    [Theory]
    [InlineData("abcd", "text", "abcd", "matched")]
    [InlineData("ab12c3d4", "text", "abcd", "not_matched")]
    [InlineData("abcd", "hex", "61 62\t63\n64", "matched")]
    [InlineData("aaaaab", "text", "aaab", "matched")]
    public async Task LiteralMatchingDoesNotReconstructInterleavedOutput(string rx, string kind, string value, string expected)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, rx, new UTF8Encoding(false));
            Assert.Equal(expected, await AutomationSequenceMatcher.EvaluateAsync(path, 0, new FileInfo(path).Length,
                new(kind, value), Encoding.UTF8, false));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PatternCanCrossReadChunkBoundaryButCannotCrossSelectedRange()
    {
        var path = Path.GetTempFileName();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(new string('x', 16382) + "응답OK" + "tail");
            await File.WriteAllBytesAsync(path, bytes);
            var expectation = new AutomationResponseExpectation("text", "응답OK");
            Assert.Equal("matched", await AutomationSequenceMatcher.EvaluateAsync(path, 0, bytes.Length, expectation, Encoding.UTF8, false));
            Assert.Equal("not_matched", await AutomationSequenceMatcher.EvaluateAsync(path, 0, 16384, expectation, Encoding.UTF8, false));
            Assert.Equal("not_matched", await AutomationSequenceMatcher.EvaluateAsync(path, 16383, bytes.Length, expectation, Encoding.UTF8, false));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task GapsMissingFilesAndInvalidRangesAreInconclusive()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, "OK"u8.ToArray());
            var expectation = new AutomationResponseExpectation("text", "OK");
            Assert.Equal("inconclusive", await AutomationSequenceMatcher.EvaluateAsync(path, 0, 2, expectation, Encoding.UTF8, true));
            Assert.Equal("inconclusive", await AutomationSequenceMatcher.EvaluateAsync(path, -1, 2, expectation, Encoding.UTF8, false));
            Assert.Equal("inconclusive", await AutomationSequenceMatcher.EvaluateAsync(path, 2, 1, expectation, Encoding.UTF8, false));
            Assert.Equal("inconclusive", await AutomationSequenceMatcher.EvaluateAsync(path, 0, 3, expectation, Encoding.UTF8, false));
            Assert.Equal("not_requested", await AutomationSequenceMatcher.EvaluateAsync(path, 0, 2, null, Encoding.UTF8, true));
            File.Delete(path);
            Assert.Equal("inconclusive", await AutomationSequenceMatcher.EvaluateAsync(path, 0, 2, expectation, Encoding.UTF8, false));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("regex", "OK")]
    [InlineData("text", "")]
    [InlineData("hex", "0")]
    [InlineData("hex", "GG")]
    [InlineData("hex", " ")]
    public void InvalidPatternsAreRejected(string kind, string value) =>
        Assert.Throws<AutomationException>(() => AutomationSequenceMatcher.PatternBytes(new(kind, value), Encoding.UTF8));

    [Fact]
    public async Task CancellationPropagatesAndPatternInputIsBounded()
    {
        Assert.Throws<AutomationException>(() => AutomationSequenceMatcher.PatternBytes(new("text", new string('x', 4097)), Encoding.UTF8));
        Assert.Throws<AutomationException>(() => AutomationSequenceMatcher.PatternBytes(new("text", "한글"), Encoding.ASCII));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AutomationSequenceMatcher.EvaluateAsync("missing", 0, 0,
            new("text", "OK"), Encoding.UTF8, false, cancellation.Token));
    }
}
