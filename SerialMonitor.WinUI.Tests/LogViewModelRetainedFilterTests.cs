using System.Collections.Concurrent;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogViewModelRetainedFilterTests
{
    [Fact]
    public void FilterAfterReceipt_SwitchRulesAndAll_RestoresOriginalOrderAndIds()
    {
        using var ui = new FilterUiContext();
        var log = new LogViewModel(100);
        log.AddRange([LogLine.Rx("ERROR old"), LogLine.Rx("WARN old"), LogLine.Rx("INFO old")]);
        var original = log.GetVisibleSearchContentSnapshot();
        var source = log.GetRetainedLinesSnapshot();
        var rebuilds = 0;
        var callerThread = Environment.CurrentManagedThreadId;
        log.TextRebuilt += (_, _) =>
        {
            Assert.Equal(callerThread, Environment.CurrentManagedThreadId);
            rebuilds++;
        };

        Assert.True(ui.Complete(log.SetViewFiltersAsync([Rule("ERROR")])));
        Assert.Equal(original[0], Assert.Single(log.GetVisibleSearchContentSnapshot()));
        Assert.False(log.TryGetSearchLine(source[1], out _));
        Assert.True(ui.Complete(log.SetViewFiltersAsync([Rule("WARN")])));
        Assert.Equal(original[1], Assert.Single(log.GetVisibleSearchContentSnapshot()));
        Assert.True(ui.Complete(log.SetViewFiltersAsync([Rule("ERROR"), Rule("WARN")])));
        Assert.Equal(original.Take(2), log.GetVisibleSearchContentSnapshot());
        Assert.True(ui.Complete(log.SetViewFiltersAsync([])));
        Assert.Equal(original, log.GetVisibleSearchContentSnapshot());
        Assert.True(log.TryGetSearchLine(source[1], out var restored));
        Assert.Equal(original[1], restored);
        Assert.Equal(source, log.GetRetainedLinesSnapshot());
        Assert.Equal(3, log.DisplayedLineCount);
        Assert.Equal(0, log.DroppedVisibleLineCount);
        Assert.Equal(4, rebuilds);
    }

    [Fact]
    public void LiveReceiptDuringFilter_CatchesUpAndDoesNotResurrectExpiredLines()
    {
        using var ui = new FilterUiContext();
        var log = new LogViewModel(3);
        log.AddRange([LogLine.Rx("KEEP expired"), LogLine.Rx("hidden old"), LogLine.Rx("KEEP old")]);
        var pending = log.SetViewFiltersAsync([Rule("KEEP")]);
        Assert.False(pending.IsCompleted);
        log.AddRange([LogLine.Rx("KEEP live"), LogLine.Rx("hidden live")]);

        Assert.True(ui.Complete(pending));
        Assert.Equal(new[] { "KEEP old", "KEEP live" }, Payloads(log));
        Assert.Equal(3, log.TotalRetainedLineCount);
        Assert.True(ui.Complete(log.SetViewFiltersAsync([])));
        Assert.Equal(new[] { "KEEP old", "KEEP live", "hidden live" }, Payloads(log));
        log.AddRange([LogLine.Rx("after apply")]);
        Assert.Equal(new[] { "KEEP live", "hidden live", "after apply" }, Payloads(log));
    }

    [Fact]
    public void ClearAndFormatChangeDuringFilter_UsesCurrentHistoryAndSettings()
    {
        using var ui = new FilterUiContext();
        var log = new LogViewModel(10);
        log.AddRange([LogLine.Rx("KEEP removed")]);
        var pending = log.SetViewFiltersAsync([Rule("KEEP")]);
        log.Clear();
        log.SetCapacity(2);
        log.SetTimestampDisplayFormat(TimestampDisplayFormat.TimeSeconds);
        log.SetShowRxTxDirectionPrefixInLogView(false);
        log.AddRange([LogLine.Rx("hidden"), LogLine.Rx("KEEP current")]);

        Assert.True(ui.Complete(pending));
        Assert.Equal(new[] { "KEEP current" }, Payloads(log));
        Assert.DoesNotContain("RX <", log.GetVisibleTextSnapshot());
        Assert.Equal(2, log.TotalRetainedLineCount);
        Assert.True(ui.Complete(log.SetViewFiltersAsync([])));
        Assert.Equal(new[] { "hidden", "KEEP current" }, Payloads(log));
    }

    [Fact]
    public void CatchUpOverflow_RestartsFromBoundedCurrentHistory()
    {
        using var ui = new FilterUiContext();
        var log = new LogViewModel(2);
        log.AddRange([LogLine.Rx("KEEP expired")]);
        var pending = log.SetViewFiltersAsync([Rule("KEEP")]);
        log.AddRange([LogLine.Rx("overflow"), LogLine.Rx("hidden"), LogLine.Rx("KEEP latest")]);

        Assert.True(ui.Complete(pending));
        Assert.Equal(new[] { "KEEP latest" }, Payloads(log));
        Assert.True(ui.Complete(log.SetViewFiltersAsync([])));
        Assert.Equal(new[] { "hidden", "KEEP latest" }, Payloads(log));
    }

    [Fact]
    public void SupersedingOrCancelledFilter_NeverPublishesAnObsoleteView()
    {
        using var ui = new FilterUiContext();
        var log = new LogViewModel(10);
        log.AddRange([LogLine.Rx("ERROR"), LogLine.Rx("WARN")]);
        var rebuilds = 0;
        log.TextRebuilt += (_, _) => rebuilds++;
        var older = log.SetViewFiltersAsync([Rule("ERROR")]);
        var latest = log.SetViewFiltersAsync([Rule("WARN")]);
        Assert.False(ui.Complete(older));
        Assert.True(ui.Complete(latest));
        Assert.Equal(new[] { "WARN" }, Payloads(log));

        using var cancellation = new CancellationTokenSource();
        var cancelled = log.SetViewFiltersAsync([], cancellation.Token);
        cancellation.Cancel();
        Assert.False(ui.Complete(cancelled));
        log.AddRange([LogLine.Rx("ERROR new"), LogLine.Rx("WARN new")]);
        Assert.Equal(new[] { "WARN", "WARN new" }, Payloads(log));
        Assert.Equal(1, rebuilds);
    }

    [Fact]
    public void PartialHexReceiptDuringFilter_KeepsIdentityAndCanRestoreHiddenSegments()
    {
        using var ui = new FilterUiContext();
        var log = new LogViewModel(20);
        log.SetRxDisplayMode(RxDisplayMode.Hex);
        log.AddRange([LogLine.Rx("", [0xAA], isPartialRxSegment: true)]);
        var pending = log.SetViewFiltersAsync([Rule("AA", LogRuleMatchMode.Hex)]);
        log.AddRange([
            LogLine.Rx("", [0xBB], isPartialRxSegment: true),
            LogLine.Rx("", [0xAA], isPartialRxSegment: true),
            LogLine.RxPartialTerminator()]);

        Assert.True(ui.Complete(pending));
        var filtered = Assert.Single(log.GetVisibleSearchContentSnapshot());
        Assert.Equal("AA AA", filtered.PayloadText);
        Assert.True(ui.Complete(log.SetViewFiltersAsync([])));
        var restored = Assert.Single(log.GetVisibleSearchContentSnapshot());
        Assert.Equal("AA BB AA", restored.PayloadText);
        Assert.Equal(filtered.LineId, restored.LineId);
        log.AddRange([LogLine.Rx("", [0xCC], isPartialRxSegment: true)]);
        Assert.Equal(2, log.CurrentVisibleLineCount);
    }

    [Fact]
    public void ActivePartialLine_AfterAsyncFilter_StillMergesLiveSegments()
    {
        using var ui = new FilterUiContext();
        var log = new LogViewModel(20);
        log.AddRange([LogLine.Rx("KEEP-A", isPartialRxSegment: true)]);
        var pending = log.SetViewFiltersAsync([Rule("KEEP")]);
        log.AddRange([LogLine.Rx("KEEP-B", isPartialRxSegment: true)]);
        Assert.True(ui.Complete(pending));
        var before = Assert.Single(log.GetVisibleSearchContentSnapshot());
        log.AddRange([LogLine.Rx("KEEP-C", isPartialRxSegment: true), LogLine.RxPartialTerminator()]);
        var after = Assert.Single(log.GetVisibleSearchContentSnapshot());
        Assert.Equal(before.LineId, after.LineId);
        Assert.Equal("KEEP-AKEEP-BKEEP-C", after.PayloadText);
        Assert.False(log.PartialRxVisualLineActive);
    }

    [Fact]
    public void CatchUpTrimAcrossPartialGroup_RebuildsOnlyCurrentRetainedSegments()
    {
        using var ui = new FilterUiContext();
        var log = new LogViewModel(3);
        log.SetViewFilters([Rule("KEEP")]);
        log.AddRange([
            LogLine.Rx("hidden-part", isPartialRxSegment: true),
            LogLine.Rx("KEEP-A", isPartialRxSegment: true),
            LogLine.Rx("KEEP-B", isPartialRxSegment: true)]);
        var pending = log.SetViewFiltersAsync([]);
        log.AddRange([LogLine.RxPartialTerminator()]);
        Assert.True(ui.Complete(pending));
        Assert.Equal(new[] { "KEEP-AKEEP-B" }, Payloads(log));
        Assert.Equal(2, Assert.Single(log.GetVisibleSearchContentSnapshot()).LineId);
        log.AddRange([LogLine.Rx("next")]);
        Assert.DoesNotContain("hidden-part", log.GetVisibleTextSnapshot());
        Assert.Contains("next", log.GetVisibleTextSnapshot());
    }

    [Fact]
    public void Refilter_PreservesReceiptTimeHighlightRules()
    {
        using var ui = new FilterUiContext();
        var log = new LogViewModel(10);
        log.AddRange([LogLine.Rx("ERROR old")]);
        var highlight = Rule("ERROR");
        highlight.ForegroundColor = "Red";
        log.SetHighlightRules([highlight]);
        log.AddRange([LogLine.Rx("ERROR new")]);

        Assert.True(ui.Complete(log.SetViewFiltersAsync([Rule("ERROR")])));
        Assert.True(ui.Complete(log.SetViewFiltersAsync([])));
        var text = log.GetVisibleTextSnapshot();
        Assert.Equal(1, text.Split("\u001b[31m").Length - 1);
        Assert.Equal(2, log.CurrentVisibleLineCount);
    }

    [Fact]
    public void SplitTerminalMatch_AfterAsyncPublish_KeepsItsHiddenSuffixAndIdentity()
    {
        using var ui = new FilterUiContext();
        var log = new LogViewModel(20);
        log.AddRange([LogLine.Rx("FA", sequenceNumber: 1, isPartialRxSegment: true)]);
        var pending = log.SetViewFiltersAsync([Rule("FAULT")]);
        log.AddRange([LogLine.Rx("U", sequenceNumber: 2, isPartialRxSegment: true)]);
        Assert.True(ui.Complete(pending));
        Assert.Empty(Payloads(log));
        var final = LogLine.Rx("LT", sequenceNumber: 3, isPartialRxSegment: true);
        log.AddRange([final, LogLine.RxPartialTerminator(4)]);
        var matched = Assert.Single(log.GetVisibleSearchContentSnapshot());
        Assert.Equal("FAULT", matched.PayloadText);
        Assert.True(log.TryGetSearchLine(final, out var navigation));
        Assert.Equal(matched, navigation);
        Assert.True(ui.Complete(log.SetViewFiltersAsync([])));
        Assert.Equal(new[] { "FAULT" }, Payloads(log));
    }

    [Fact]
    public void SplitTerminalMatch_TrimAndAsyncCatchUp_NeverResurrectEvictedPrefix()
    {
        using var ui = new FilterUiContext();
        var log = new LogViewModel(2);
        log.AddRange([LogLine.Rx("FA", sequenceNumber: 1, isPartialRxSegment: true)]);
        var pending = log.SetViewFiltersAsync([Rule("FAULT")]);
        log.AddRange([LogLine.Rx("noise", sequenceNumber: 2, isPartialRxSegment: true),
            LogLine.Rx("ULT", sequenceNumber: 3, isPartialRxSegment: true)]);
        Assert.True(ui.Complete(pending));
        Assert.Empty(Payloads(log));
        log.AddRange([LogLine.Rx("FAULT", sequenceNumber: 4)]);
        Assert.Equal(new[] { "FAULT" }, Payloads(log));
        Assert.DoesNotContain("noise", log.GetVisibleTextSnapshot());
    }

    private static string[] Payloads(LogViewModel log) =>
        log.GetVisibleSearchContentSnapshot().Select(line => line.PayloadText).ToArray();

    private static HighlightRule Rule(string keyword, LogRuleMatchMode mode = LogRuleMatchMode.Terminal) => new()
    {
        Name = keyword, Keyword = keyword, Enabled = true, UseAsViewFilter = true, Mode = mode
    };

    // Drive asynchronous completions on one caller thread, as the WinUI
    // dispatcher does, while deterministically injecting live UI operations.
    private sealed class FilterUiContext : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext? _previous = Current;
        private readonly BlockingCollection<Action> _queue = new();

        public FilterUiContext() => SetSynchronizationContext(this);
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add(() => callback(state));

        public bool Complete(Task<bool> task)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!task.IsCompleted)
            {
                if (_queue.TryTake(out var action, 100, timeout.Token)) action();
            }
            return task.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            SetSynchronizationContext(_previous);
            _queue.Dispose();
        }
    }
}
