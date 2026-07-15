using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogRuleMatchingConsistencyTests
{
    private const string Pattern = "DE AD BE EF";

    [Fact]
    public async Task HexPattern_InOneLogLine_MatchesEventHighlightAndViewFilter()
    {
        var line = LogLine.Rx(
            "10 DE AD BE EF 20",
            new byte[] { 0x10, 0xDE, 0xAD, 0xBE, 0xEF, 0x20 },
            contentMode: LogRuleMatchMode.Hex);
        var eventRule = CreateEventRule();
        var highlightRule = CreateHighlightRule();

        Assert.True(LogRuleMatcher.IsMatch(
            line,
            LogRuleMatcher.Compile(eventRule),
            LogRuleMatchMode.Hex,
            out var eventMatchError));
        Assert.Null(eventMatchError);
        Assert.True(LogRuleMatcher.IsMatch(
            line,
            LogRuleMatcher.Compile(highlightRule),
            LogRuleMatchMode.Hex,
            out var highlightMatchError));
        Assert.Null(highlightMatchError);

        await using var detector = new EventDetector();
        await detector.StartAsync(
            new[] { eventRule },
            new EventContextSettings(),
            CancellationToken.None);
        detector.UpdateRuleMode(LogRuleMatchMode.Hex);
        Assert.True(detector.TryEnqueue(line));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var detected = await detector.DetectedEvents.ReadAsync(timeout.Token);
        Assert.Equal("consistent-hex", detected.RuleName);

        var highlightView = CreateHexLogView();
        highlightView.SetHighlightRules(new[] { highlightRule });
        highlightView.AddRange(new[] { line });
        Assert.Equal(1, highlightView.HighlightedLineCount);
        Assert.Contains("\u001b[31m", highlightView.GetVisibleTextSnapshot(), StringComparison.Ordinal);

        var filteredView = CreateHexLogView();
        filteredView.SetViewFilter(highlightRule);
        filteredView.AddRange(new[] { line });
        Assert.Equal(1, filteredView.CurrentVisibleLineCount);
    }

    [Fact]
    public async Task HexPattern_SplitAcrossLogLines_MatchesNoneOfEventHighlightOrViewFilter()
    {
        var lines = new[]
        {
            LogLine.Rx(
                "10 DE AD",
                new byte[] { 0x10, 0xDE, 0xAD },
                contentMode: LogRuleMatchMode.Hex),
            LogLine.Rx(
                "BE EF 20",
                new byte[] { 0xBE, 0xEF, 0x20 },
                contentMode: LogRuleMatchMode.Hex)
        };
        var eventRule = CreateEventRule();
        var highlightRule = CreateHighlightRule();
        var compiledEvent = LogRuleMatcher.Compile(eventRule);
        var compiledHighlight = LogRuleMatcher.Compile(highlightRule);

        Assert.All(lines, line =>
        {
            Assert.False(LogRuleMatcher.IsMatch(
                line,
                compiledEvent,
                LogRuleMatchMode.Hex,
                out var eventMatchError));
            Assert.Null(eventMatchError);
            Assert.False(LogRuleMatcher.IsMatch(
                line,
                compiledHighlight,
                LogRuleMatchMode.Hex,
                out var highlightMatchError));
            Assert.Null(highlightMatchError);
        });

        await using var detector = new EventDetector();
        await detector.StartAsync(
            new[] { eventRule },
            new EventContextSettings(),
            CancellationToken.None);
        detector.UpdateRuleMode(LogRuleMatchMode.Hex);
        Assert.All(lines, line => Assert.True(detector.TryEnqueue(line)));
        await WaitUntilAsync(() => detector.PendingInputLineCount == 0);
        await Task.Delay(50);
        Assert.False(detector.DetectedEvents.TryRead(out _));

        var highlightView = CreateHexLogView();
        highlightView.SetHighlightRules(new[] { highlightRule });
        highlightView.AddRange(lines);
        Assert.Equal(0, highlightView.HighlightedLineCount);
        Assert.DoesNotContain("\u001b[31m", highlightView.GetVisibleTextSnapshot(), StringComparison.Ordinal);

        var filteredView = CreateHexLogView();
        filteredView.SetViewFilter(highlightRule);
        filteredView.AddRange(lines);
        Assert.Equal(2, filteredView.TotalRetainedLineCount);
        Assert.Equal(0, filteredView.CurrentVisibleLineCount);
    }

    private static EventRule CreateEventRule() => new()
    {
        Name = "consistent-hex",
        Keyword = Pattern,
        Enabled = true,
        Mode = LogRuleMatchMode.Hex,
        MatchDirection = EventMatchDirection.RxOnly
    };

    private static HighlightRule CreateHighlightRule() => new()
    {
        Name = "consistent-hex",
        Keyword = Pattern,
        Enabled = true,
        Mode = LogRuleMatchMode.Hex,
        MatchDirection = HighlightMatchDirection.RxOnly,
        ForegroundColor = "Red",
        UseAsViewFilter = true
    };

    private static LogViewModel CreateHexLogView()
    {
        var viewModel = new LogViewModel(capacity: 100);
        viewModel.SetRxDisplayMode(RxDisplayMode.Hex);
        return viewModel;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }
}
