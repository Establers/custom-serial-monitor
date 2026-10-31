using System.Text;
using System.Text.RegularExpressions;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogViewMemoryTests
{
    [Theory]
    [InlineData(RxDisplayMode.Terminal, false)]
    [InlineData(RxDisplayMode.Hex, false)]
    [InlineData(RxDisplayMode.Terminal, true)]
    public void DefaultPolicyDisplaysAWholeMaximumSizeNativeCompletion(RxDisplayMode mode, bool brackets)
    {
        var log = new LogViewModel(UiSettings.DefaultVisibleLogLines);
        log.SetRxDisplayMode(mode);
        var bytes = new byte[1024 * 1024];
        Array.Fill(bytes, (byte)'x');
        if (brackets)
            for (var i = 0; i < bytes.Length; i++) bytes[i] = i % 2 == 0 ? (byte)'[' : (byte)']';
        "FULL_END"u8.CopyTo(bytes.AsSpan(bytes.Length - 8));
        var original = LogLine.Rx(Encoding.UTF8.GetString(bytes), bytes, 1, isPartialRxSegment: true,
            displayText: mode == RxDisplayMode.Hex ? HexFormatter.Format(bytes) : null,
            contentMode: mode == RxDisplayMode.Hex ? LogRuleMatchMode.Hex : LogRuleMatchMode.Terminal);
        log.AddRange([original]);
        var text = log.GetVisibleTextSnapshot();
        Assert.DoesNotContain(LogViewMemoryPolicy.PreviewSuffix, text);
        Assert.Contains(mode == RxDisplayMode.Hex ? "46 55 4C 4C 5F 45 4E 44" : "FULL_END", text);
        Assert.Same(original, Assert.Single(log.GetRetainedLinesSnapshot()));
        Assert.True(log.VisibleCharacterCount <= LogViewMemoryPolicy.Default.VisibleCharacters);
    }

    [Theory]
    [InlineData(RxDisplayMode.Terminal)]
    [InlineData(RxDisplayMode.Hex)]
    public void DefaultPolicyEvictsLargeStreamsLongBeforeTheMillionRecordLimit(RxDisplayMode mode)
    {
        var log = new LogViewModel(UiSettings.DefaultVisibleLogLines);
        log.SetRxDisplayMode(mode);
        for (var i = 0; i < 768; i++)
        {
            var bytes = new byte[65536];
            Array.Fill(bytes, (byte)'x');
            var text = Encoding.UTF8.GetString(bytes);
            log.AddRange([LogLine.Rx(text, bytes, i + 1, isPartialRxSegment: true,
                displayText: mode == RxDisplayMode.Hex ? HexFormatter.Format(bytes) : null,
                contentMode: mode == RxDisplayMode.Hex ? LogRuleMatchMode.Hex : LogRuleMatchMode.Terminal)]);
            Assert.True(log.RetainedMemoryBytes <= log.RetainedMemoryByteLimit);
            Assert.True(log.VisibleCharacterCount <= LogViewMemoryPolicy.Default.VisibleCharacters);
        }
        Assert.True(log.DroppedVisibleLineCount > 0);
        Assert.True(log.TotalRetainedLineCount < 768);
        Assert.All(log.GetVisibleSearchContentSnapshot(), line =>
            Assert.True(line.FullText.Length <= LogViewMemoryPolicy.Default.ContinuousLineCharacters));
    }

    [Theory]
    [InlineData(RxDisplayMode.Terminal)]
    [InlineData(RxDisplayMode.Hex)]
    public void ContinuousLargeRecordsStayWithinScreenBudgetsAndKeepOriginalRetainedRecords(RxDisplayMode mode)
    {
        var policy = new LogViewMemoryPolicy(RetainedBytes: 512 * 1024, VisibleCharacters: 64 * 1024,
            ContinuousLineCharacters: 8192, PreviewCharacters: 512, HexPreviewBytes: 128);
        var log = new LogViewModel(1_000_000, policy);
        log.SetRxDisplayMode(mode);
        var rebuilds = 0;
        var projected = string.Empty;
        log.TextRebuilt += (_, _) => { rebuilds++; projected = log.GetVisibleTextSnapshot(); };
        log.TextBatchAppended += (_, batch) =>
        {
            var text = Regex.Replace(batch.AppendedText, "\u001b\\]777;[^\u0007]*\u0007", "");
            projected = projected[Math.Min(projected.Length, batch.TrimCharacterCount)..] + text;
        };
        for (var pass = 0; pass < 6; pass++)
        {
            LogLine? last = null;
            var batch = new LogLine[100];
            for (var i = 0; i < batch.Length; i++)
            {
                var bytes = Encoding.UTF8.GetBytes(new string('x', 4096));
                last = LogLine.Rx(Encoding.UTF8.GetString(bytes), bytes, pass * 100 + i + 1,
                    isPartialRxSegment: true, displayText: mode == RxDisplayMode.Hex ? HexFormatter.Format(bytes) : null,
                    contentMode: mode == RxDisplayMode.Hex ? LogRuleMatchMode.Hex : LogRuleMatchMode.Terminal);
                batch[i] = last;
            }
            log.AddRange(batch);
            Assert.InRange(log.RetainedMemoryBytes, 1, policy.RetainedBytes);
            Assert.InRange(log.VisibleCharacterCount, 1, policy.VisibleCharacters);
            Assert.Same(last, log.GetRetainedLinesSnapshot()[^1]);
            Assert.Equal(4096, log.GetRetainedLinesSnapshot()[^1].RawBytes!.Length);
            Assert.All(log.GetVisibleSearchContentSnapshot(), line => Assert.True(line.FullText.Length <= policy.ContinuousLineCharacters));
            Assert.Equal(log.GetVisibleTextSnapshot(), projected);
        }
        Assert.True(rebuilds > 0);
        Assert.True(log.DroppedVisibleLineCount > 0);
        log.Clear();
        Assert.Equal(0, log.RetainedMemoryBytes);
        Assert.Equal(0, log.VisibleCharacterCount);
    }

    [Theory]
    [InlineData(RxDisplayMode.Terminal)]
    [InlineData(RxDisplayMode.Hex)]
    public async Task HighlightAndFilterInspectTheWholeRecordBeforeMakingAPreview(RxDisplayMode mode)
    {
        var policy = new LogViewMemoryPolicy(PreviewCharacters: 64, HexPreviewBytes: 16);
        var log = new LogViewModel(100, policy);
        log.SetRxDisplayMode(mode);
        var kind = mode == RxDisplayMode.Hex ? LogRuleMatchMode.Hex : LogRuleMatchMode.Terminal;
        var bytes = Encoding.UTF8.GetBytes(new string('x', 8192) + "FAULT_END");
        var rule = new HighlightRule { Keyword = kind == LogRuleMatchMode.Hex ? "46 41 55 4C 54 5F 45 4E 44" : "FAULT_END",
            Mode = kind, Enabled = true, ForegroundColor = "red", UseAsViewFilter = true };
        var original = LogLine.Rx(Encoding.UTF8.GetString(bytes), bytes, 1, isPartialRxSegment: true,
            displayText: kind == LogRuleMatchMode.Hex ? HexFormatter.Format(bytes) : null, contentMode: kind);
        var fileText = original.FileFormatted;
        log.SetHighlightRules([rule]);
        log.SetViewFilters([rule]);
        log.AddRange([original]);
        Assert.Equal(1, log.HighlightedLineCount);
        Assert.Equal(1, log.CurrentVisibleLineCount);
        Assert.Contains(LogViewMemoryPolicy.PreviewSuffix, log.GetVisibleTextSnapshot());
        Assert.Contains("\u001b[31m", log.GetVisibleTextSnapshot());
        Assert.Same(original, Assert.Single(log.GetRetainedLinesSnapshot()));
        Assert.Equal(fileText, original.FileFormatted);
        Assert.Equal(bytes, original.RawBytes);
        Assert.True(await log.SetViewFiltersAsync([rule]));
        log.RefreshVisibleFormatting();
        Assert.Equal(1, log.CurrentVisibleLineCount);
        Assert.Contains("\u001b[31m", log.GetVisibleTextSnapshot());
    }

    [Fact]
    public void MemoryEvictionAndVisualWrappingDoNotBreakTerminalStreamingRuleState()
    {
        var policy = new LogViewMemoryPolicy(RetainedBytes: 2048, VisibleCharacters: 4096,
            ContinuousLineCharacters: 128, PreviewCharacters: 64, HexPreviewBytes: 16);
        var log = new LogViewModel(100, policy);
        var rule = new HighlightRule { Keyword = "FAULT", Enabled = true, ForegroundColor = "red", UseAsViewFilter = true };
        log.SetHighlightRules([rule]);
        log.SetViewFilters([rule]);
        LogLine Fragment(string text, long sequence) => LogLine.Rx(text, Encoding.UTF8.GetBytes(text), sequence, isPartialRxSegment: true);
        log.AddRange([Fragment(new string('x', 198), 1), Fragment(new string('x', 198) + "FA", 2)]);
        log.AddRange([Fragment("ULT" + new string('z', 197), 3)]);
        Assert.True(log.DroppedVisibleLineCount > 0);
        Assert.Equal(1, log.HighlightedLineCount);
        Assert.Contains("FAULT", log.GetVisibleTextSnapshot());
    }

    [Fact]
    public async Task RestoringHiddenLargeHistoryRespectsTheVisibleBudgetAndKeepsFilterCatchUpConsistent()
    {
        var policy = new LogViewMemoryPolicy(RetainedBytes: 4 * 1024 * 1024, VisibleCharacters: 8192,
            ContinuousLineCharacters: 1024, PreviewCharacters: 256, HexPreviewBytes: 64);
        var log = new LogViewModel(100_000, policy);
        log.SetViewFilters([new HighlightRule { Keyword = "never matches", Enabled = true }]);
        log.AddRange(Enumerable.Range(0, 300).Select(i => LogLine.Rx($"record {i:D5} " + new string('x', 300))).ToArray());
        Assert.Equal(0, log.CurrentVisibleLineCount);
        Assert.True(await log.SetViewFiltersAsync([]));
        Assert.InRange(log.VisibleCharacterCount, 1, policy.VisibleCharacters);
        Assert.Equal(log.TotalRetainedLineCount, log.CurrentVisibleLineCount);
        var expectedBytes = log.GetRetainedLinesSnapshot().Sum(LogViewMemoryPolicy.EstimateRetainedBytes);
        Assert.Equal(expectedBytes, log.RetainedMemoryBytes);
        log.AddRange([LogLine.Rx("after restore")]);
        Assert.True(await log.SetViewFiltersAsync([]));
        Assert.Equal(log.TotalRetainedLineCount, log.CurrentVisibleLineCount);
        Assert.Contains("after restore", log.GetVisibleTextSnapshot());
    }

    [Fact]
    public async Task RevealingBracketHeavyHistoryKeepsOnlyABoundedSuffixOfWholeRecords()
    {
        var policy = new LogViewMemoryPolicy(RetainedBytes: 8 * 1024 * 1024,
            VisibleCharacters: 128 * 1024, ContinuousLineCharacters: 64 * 1024);
        var log = new LogViewModel(10_000, policy);
        log.SetViewFilters([new HighlightRule { Keyword = "never matches", Enabled = true }]);
        LogLine? last = null;
        for (var i = 0; i < 300; i++)
        {
            var text = string.Concat(Enumerable.Repeat("[]", 2048)) + $"END_{i}";
            last = LogLine.Rx(text, Encoding.UTF8.GetBytes(text), i + 1, isPartialRxSegment: true);
            log.AddRange([last]);
        }
        Assert.Equal(300, log.TotalRetainedLineCount);
        Assert.True(await log.SetViewFiltersAsync([]));
        Assert.True(log.DroppedVisibleLineCount > 0);
        Assert.InRange(log.VisibleCharacterCount, 1, policy.VisibleCharacters);
        Assert.Same(last, log.GetRetainedLinesSnapshot()[^1]);
        Assert.Contains("END_299", log.GetVisibleTextSnapshot());
        Assert.DoesNotContain(LogViewMemoryPolicy.PreviewSuffix, log.GetVisibleTextSnapshot());
    }

    [Fact]
    public void OversizedRecordsAreRejectedOnlyByTheScreenAndDoNotEvictTheCurrentWindow()
    {
        var policy = new LogViewMemoryPolicy(RetainedBytes: 1024);
        var log = new LogViewModel(100, policy);
        var keep = LogLine.Rx("keep");
        log.AddRange([keep]);
        var large = LogLine.Rx(new string('x', 10000));
        log.AddRange([large]);
        Assert.Same(keep, Assert.Single(log.GetRetainedLinesSnapshot()));
        Assert.Equal(10000, large.Text.Length);
        Assert.Equal(1, log.DroppedVisibleLineCount);
        Assert.True(log.RetainedMemoryBytes <= policy.RetainedBytes);
    }
}
