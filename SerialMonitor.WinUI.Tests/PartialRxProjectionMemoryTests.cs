using System.Text.RegularExpressions;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;
using Xunit.Abstractions;

namespace SerialMonitor.WinUI.Tests;

public sealed class PartialRxProjectionMemoryTests(ITestOutputHelper output)
{
    [Fact]
    public void CompletedPartialRecordsRetainSearchableHistoryWithinTheBudget()
    {
        const int count = 20_000;
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000);
        var records = Enumerable.Range(0, count).SelectMany(i => new[]
        {
            LogLine.Rx($"packet {i:D8} " + new string('x', 239), timestamp: timestamp,
                isPartialRxSegment: true, sequenceNumber: 2 * i + 1),
            LogLine.RxPartialTerminator(2 * i + 2)
        }).ToArray();
        // Warm JIT/formatting before measuring this synchronous screen path.
        var warmup = new LogViewModel(100);
        warmup.AddRange(records.Take(100).ToArray());
        warmup.Clear();
        var before = GC.GetTotalMemory(true);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var log = new LogViewModel(count * 2);
        for (var index = 0; index < records.Length; index += 1000)
            log.AddRange(new ArraySegment<LogLine>(records, index, 1000));
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var retained = GC.GetTotalMemory(true) - before;
        output.WriteLine($"20,000 x 255-character completed RX lines: screen retained {retained:N0} B; allocated {allocated:N0} B");
        Assert.Equal(count, log.CurrentVisibleLineCount);
        Assert.Equal(count * 2, log.TotalRetainedLineCount);
        Assert.Equal(0, log.DroppedVisibleLineCount);
        Assert.InRange(log.RetainedMemoryBytes, 1, log.RetainedMemoryByteLimit);
        var search = log.GetVisibleSearchContentSnapshot();
        Assert.Equal(records[0].Text, search[0].PayloadText);
        Assert.Equal(records[^2].Text, search[^1].PayloadText);
        GC.KeepAlive(records);
        GC.KeepAlive(log);
    }

    [Fact]
    public void RecoloringAndRemovingColorNeverChangesAnActiveLinesSearchText()
    {
        var log = new LogViewModel(100);
        log.SetShowTimestampInLogView(false);
        log.SetShowRxTxDirectionPrefixInLogView(false);
        log.SetHighlightRules([
            new HighlightRule { Keyword = "FAULT", ForegroundColor = "Red" },
            new HighlightRule { Keyword = "IGNORE", ForegroundColor = "Default", Priority = 10 }
        ]);
        var text = "";
        long sequence = 0;
        foreach (var part in new[] { "plain ", "FA", "ULT ", "IGNORE ", "tail" })
        {
            text += part;
            log.AddRange([LogLine.Rx(part, sequenceNumber: ++sequence, isPartialRxSegment: true)]);
            Assert.Equal(text, Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
            var display = log.GetVisibleTextSnapshot();
            Assert.Equal(text, Regex.Replace(display, "\u001b\\[[0-9;]*m", ""));
            Assert.DoesNotContain('\u001b', Assert.Single(log.GetVisibleSearchLinesSnapshot()));
        }
        log.AddRange([LogLine.RxPartialTerminator(++sequence)]);
        Assert.Equal(text + Environment.NewLine, log.GetVisibleTextSnapshot());
        log.RefreshVisibleFormatting();
        Assert.Equal(text, Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        Assert.Equal(text + Environment.NewLine, log.GetVisibleTextSnapshot());
    }
}
