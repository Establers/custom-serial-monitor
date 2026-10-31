using System.Collections.Concurrent;
using System.Text;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class RuleContextRetentionTests
{
    private const string Red = "\u001b[31m";
    private static readonly LogViewMemoryPolicy SmallPolicy = new(RetainedBytes: 2048,
        VisibleCharacters: 4096, ContinuousLineCharacters: 128, PreviewCharacters: 64, HexPreviewBytes: 16);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MemoryEviction_MatchedTerminalRecordSurvivesFormattingAndRefilter(bool filtered)
    {
        using var ui = new UiContext();
        var log = new LogViewModel(100, SmallPolicy);
        log.SetHighlightRules([Rule("FAULT")]);
        if (filtered) log.SetViewFilter(Rule("FAULT"));
        log.AddRange([Rx(new string('x', 198), 1), Rx(new string('x', 198) + "FA", 2)]);
        var final = Rx("ULT" + new string('z', 197), 3);
        log.AddRange([final]);
        Assert.Contains(Red, log.GetVisibleTextSnapshot());
        Assert.True(log.DroppedVisibleLineCount > 0);
        Assert.DoesNotContain(log.GetRetainedLinesSnapshot(), line => line.SequenceNumber == 2);
        log.SetShowTimestampInLogView(false);
        Assert.Contains(Red, log.GetVisibleTextSnapshot());
        Assert.True(ui.Complete(log.SetViewFiltersAsync([Rule("FAULT")])));
        Assert.Contains(Red, log.GetVisibleTextSnapshot());
        Assert.StartsWith("FAULT", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        Assert.True(log.TryGetSearchLine(final, out var found));
        Assert.StartsWith("FAULT", found.PayloadText);
        Assert.True(ui.Complete(log.SetViewFiltersAsync([])));
        Assert.Contains(Red, log.GetVisibleTextSnapshot());
        Assert.Same(final, Assert.Single(log.GetRetainedLinesSnapshot()));
        Assert.StartsWith("ULT", final.Text);
        Assert.Equal(Encoding.UTF8.GetBytes(final.Text), final.RawBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScreenRejectedMiddleRecord_PreservesContextWithoutRejoiningOlderText(bool sequenceNumbers)
    {
        var log = new LogViewModel(100, SmallPolicy);
        log.SetHighlightRules([Rule("FAULT")]);
        log.SetViewFilter(Rule("FAULT"));
        var old = Rx("AAAA", sequenceNumbers ? 1 : null);
        var skipped = Rx(new string('x', 2000) + "FA", sequenceNumbers ? 2 : null);
        var final = Rx("ULT", sequenceNumbers ? 3 : null);
        log.AddRange([old, skipped, final]);
        Assert.Equal("FAULT", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        log.RefreshVisibleFormatting();
        Assert.Equal("FAULT", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        Assert.Contains(Red, log.GetVisibleTextSnapshot());
        Assert.Equal(new[] { old, final }, log.GetRetainedLinesSnapshot());
        log.SetViewFilter(Rule("AAAAULT"));
        Assert.Empty(log.GetVisibleSearchContentSnapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AsyncPublish_KeepsLiveSuffixFromScreenRejectedRecord(bool fromHighlight)
    {
        using var ui = new UiContext();
        var log = new LogViewModel(100, SmallPolicy);
        if (fromHighlight) log.SetHighlightRules([Rule("FAULT")]);
        else log.SetViewFilter(Rule("FAULT"));
        var pending = log.SetViewFiltersAsync([Rule("FAULT")]);
        log.AddRange([Rx(new string('x', 2000) + "FA", 1)]);
        Assert.Empty(log.GetRetainedLinesSnapshot());
        Assert.True(ui.Complete(pending));
        log.AddRange([Rx("ULT", 2)]);
        Assert.Equal("FAULT", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
        if (fromHighlight) Assert.Contains(Red, log.GetVisibleTextSnapshot());
        log.RefreshVisibleFormatting();
        Assert.Equal("FAULT", Assert.Single(log.GetVisibleSearchContentSnapshot()).PayloadText);
    }

    [Fact]
    public void CachedContext_RespectsActualRxGapAfterAsyncPublish()
    {
        using var ui = new UiContext();
        var log = new LogViewModel(100, SmallPolicy);
        log.SetViewFilter(Rule("FAULT"));
        var pending = log.SetViewFiltersAsync([Rule("FAULT")]);
        log.AddRange([Rx(new string('x', 2000) + "FA", 1)]);
        Assert.True(ui.Complete(pending));
        log.AddRange([Rx("ULT", 3)]);
        log.RefreshVisibleFormatting();
        Assert.Empty(log.GetVisibleSearchContentSnapshot());
    }

    [Fact]
    public void Hex_ReformatAndRefilterStillMatchOnlyWholeRecords()
    {
        using var ui = new UiContext();
        var log = new LogViewModel(100, SmallPolicy);
        log.SetRxDisplayMode(RxDisplayMode.Hex);
        var rule = Rule("46 41 55 4C 54");
        rule.Mode = LogRuleMatchMode.Hex;
        log.SetHighlightRules([rule]);
        log.SetViewFilter(rule);
        log.AddRange([Rx(new string('x', 198) + "FA", 1), Rx("ULT", 2)]);
        log.RefreshVisibleFormatting();
        Assert.Empty(log.GetVisibleSearchContentSnapshot());
        var whole = Rx("FAULT", 3);
        log.AddRange([whole]);
        Assert.Contains(Red, log.GetVisibleTextSnapshot());
        Assert.True(ui.Complete(log.SetViewFiltersAsync([rule])));
        Assert.Single(log.GetVisibleSearchContentSnapshot());
        Assert.Contains(Red, log.GetVisibleTextSnapshot());
        Assert.True(log.TryGetSearchLine(whole, out _));
    }

    [Fact]
    public void ReceiptContext_IsIncludedInTheRetainedMemoryBudget()
    {
        var log = new LogViewModel(100, SmallPolicy);
        log.SetHighlightRules([Rule(new string('F', 63) + "Z")]);
        for (var sequence = 1; sequence <= 2000; sequence++)
        {
            log.AddRange([Rx(new string('F', 12), sequence)]);
            Assert.InRange(log.RetainedMemoryBytes, 0, log.RetainedMemoryByteLimit);
        }
        var rawBytes = log.GetRetainedLinesSnapshot().Sum(LogViewMemoryPolicy.EstimateRetainedBytes);
        Assert.True(log.RetainedMemoryBytes > rawBytes);
        log.RefreshVisibleFormatting();
        Assert.InRange(log.RetainedMemoryBytes, 0, log.RetainedMemoryByteLimit);
        log.Clear();
        Assert.Equal(0, log.RetainedMemoryBytes);
    }

    private static HighlightRule Rule(string keyword) => new()
    {
        Keyword = keyword, ForegroundColor = "Red", UseAsViewFilter = true
    };

    private static LogLine Rx(string text, long? sequence) =>
        LogLine.Rx(text, Encoding.UTF8.GetBytes(text), sequence, isPartialRxSegment: true);

    private sealed class UiContext : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext? _previous = Current;
        private readonly BlockingCollection<Action> _queue = new();
        public UiContext() => SetSynchronizationContext(this);
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add(() => callback(state));
        public bool Complete(Task<bool> task)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!task.IsCompleted)
                if (_queue.TryTake(out var action, 100, timeout.Token)) action();
            return task.GetAwaiter().GetResult();
        }
        public void Dispose() { SetSynchronizationContext(_previous); _queue.Dispose(); }
    }
}
