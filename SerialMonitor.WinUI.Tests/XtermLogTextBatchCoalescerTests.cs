using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class XtermLogTextBatchCoalescerTests
{
    [Fact]
    public void Coalesce_MergesThousandsOfSmallRestoreBatchesWithinTransportBounds()
    {
        var input = Enumerable.Range(1, 5_000)
            .Select(index => new LogTextBatch($"line {index}\n", index % 2, 1, index))
            .ToArray();

        var result = XtermLogTextBatchCoalescer.Coalesce(
            input,
            maxLineCount: 2_000,
            maxCharacterCount: 256 * 1024);

        Assert.Equal(3, result.Length);
        Assert.Equal([2_000, 2_000, 1_000], result.Select(batch => batch.LineCount));
        Assert.Equal([2_000L, 4_000L, 5_000L], result.Select(batch => batch.EndDisplayedLineCount));
        Assert.Equal(input.Sum(batch => batch.TrimCharacterCount), result.Sum(batch => batch.TrimCharacterCount));
        Assert.Equal(string.Concat(input.Select(batch => batch.AppendedText)), string.Concat(result.Select(batch => batch.AppendedText)));
    }

    [Fact]
    public void Coalesce_UsesCharacterLimitAndKeepsOversizedSourceBatchIntact()
    {
        var input = new[]
        {
            new LogTextBatch("abc", 1, 1, 10),
            new LogTextBatch("def", 2, 1, 11),
            new LogTextBatch("oversized", 3, 1, 12),
            new LogTextBatch("x", 4, 1, 13)
        };

        var result = XtermLogTextBatchCoalescer.Coalesce(
            input,
            maxLineCount: 10,
            maxCharacterCount: 5);

        Assert.Equal(4, result.Length);
        Assert.Equal(["abc", "def", "oversized", "x"], result.Select(batch => batch.AppendedText));
        Assert.Equal([10L, 11L, 12L, 13L], result.Select(batch => batch.EndDisplayedLineCount));
    }
}
