using System.Text;
using System.Text.RegularExpressions;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogRetentionProjectionRegressionTests
{
    [Theory]
    [InlineData(17, RxDisplayMode.Terminal)]
    [InlineData(42, RxDisplayMode.Terminal)]
    [InlineData(1234, RxDisplayMode.Terminal)]
    [InlineData(17, RxDisplayMode.Hex)]
    [InlineData(42, RxDisplayMode.Hex)]
    [InlineData(1234, RxDisplayMode.Hex)]
    public void MixedBatchesFiltersAndCapacityChangesKeepTheEmittedProjectionConsistent(int seed, RxDisplayMode mode)
    {
        var policy = new LogViewMemoryPolicy(RetainedBytes: 8192, VisibleCharacters: 4096,
            ContinuousLineCharacters: 128, PreviewCharacters: 64, HexPreviewBytes: 16);
        var log = new LogViewModel(100, policy);
        log.SetRxDisplayMode(mode);
        var projected = "";
        log.TextCleared += (_, _) => projected = "";
        log.TextRebuilt += (_, _) => projected = log.GetVisibleTextSnapshot();
        log.TextBatchAppended += (_, batch) =>
        {
            projected += Regex.Replace(batch.AppendedText, "\u001b\\]777;[^\u0007]*\u0007", "");
            Assert.InRange(batch.TrimCharacterCount, 0, projected.Length);
            projected = projected[batch.TrimCharacterCount..];
        };
        var random = new Random(seed);
        long sequence = 0;
        for (var pass = 0; pass < 300; pass++)
        {
            switch (random.Next(10))
            {
                case 0:
                    log.SetCapacity(random.Next(1, 101));
                    break;
                case 1:
                    log.SetShowTimestampInLogView(random.Next(2) == 0);
                    break;
                case 2:
                    log.SetViewFilters(random.Next(2) == 0 ? [] : [new HighlightRule
                    {
                        Keyword = mode == RxDisplayMode.Hex ? "4B 45 45 50" : "KEEP",
                        Mode = mode == RxDisplayMode.Hex ? LogRuleMatchMode.Hex : LogRuleMatchMode.Terminal
                    }]);
                    break;
                default:
                    var batch = Enumerable.Range(0, random.Next(1, 21)).Select(_ =>
                    {
                        var text = (random.Next(2) == 0 ? "KEEP " : "other ") + new string('x', random.Next(600)) + " [한글🙂]\t\0";
                        return random.Next(6) switch
                        {
                            0 => LogLine.Tx(text),
                            1 => LogLine.System(text),
                            2 => LogLine.RxPartialTerminator(++sequence),
                            _ => LogLine.Rx(text, Encoding.UTF8.GetBytes(text), ++sequence,
                                isPartialRxSegment: random.Next(2) == 0)
                        };
                    }).ToArray();
                    log.AddRange(batch);
                    break;
            }
            Assert.Equal(log.GetVisibleTextSnapshot(), projected);
            Assert.InRange(log.RetainedMemoryBytes, 0, policy.RetainedBytes);
            Assert.InRange(log.VisibleCharacterCount, 0, policy.VisibleCharacters);
            Assert.InRange(log.TotalRetainedLineCount, 0, log.Capacity);
        }
        Assert.True(log.DroppedVisibleLineCount > 0);
    }
}
