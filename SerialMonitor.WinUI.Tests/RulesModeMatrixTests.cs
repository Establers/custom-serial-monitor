using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class RulesModeMatrixTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var mode in Enum.GetValues<LogRuleMatchMode>())
        foreach (var active in Enum.GetValues<LogRuleMatchMode>())
        foreach (var direction in new[] { LogDirection.Rx, LogDirection.Tx })
        foreach (var ruleDirection in Enum.GetValues<HighlightMatchDirection>())
        foreach (var caseSensitive in new[] { false, true })
        foreach (var enabled in new[] { false, true })
            yield return [mode, active, direction, ruleDirection, caseSensitive, enabled];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EventHighlightAndFilter_AgreeAcrossModesDirectionsAndCase(
        LogRuleMatchMode mode, LogRuleMatchMode active, LogDirection direction,
        HighlightMatchDirection ruleDirection, bool caseSensitive, bool enabled)
    {
        var bytes = "error"u8.ToArray();
        var keyword = mode == LogRuleMatchMode.Hex ? "65 72 72 6F 72" : "ERROR";
        var highlight = new HighlightRule
        {
            Keyword = keyword, Mode = mode, MatchDirection = ruleDirection,
            CaseSensitive = caseSensitive, Enabled = enabled, ForegroundColor = "Red"
        };
        var line = new LogLine(DateTimeOffset.Now, direction, "error", bytes,
            contentMode: active == LogRuleMatchMode.Hex ? LogRuleMatchMode.Terminal : LogRuleMatchMode.Hex);
        var expected = enabled && mode == active &&
            (ruleDirection == HighlightMatchDirection.Both ||
             (ruleDirection == HighlightMatchDirection.RxOnly && direction == LogDirection.Rx) ||
             (ruleDirection == HighlightMatchDirection.TxOnly && direction == LogDirection.Tx)) &&
            (mode == LogRuleMatchMode.Hex || !caseSensitive);
        var eventRule = new EventRule
        {
            Keyword = keyword, Mode = mode, MatchDirection = (EventMatchDirection)ruleDirection,
            CaseSensitive = caseSensitive, Enabled = enabled
        };
        Assert.Equal(expected, LogRuleMatcher.IsMatch(line, LogRuleMatcher.Compile(eventRule), active, out _));
        var view = new LogViewModel(10);
        view.SetRxDisplayMode(active == LogRuleMatchMode.Hex ? RxDisplayMode.Hex : RxDisplayMode.Terminal);
        view.SetHighlightRules([highlight]);
        view.AddRange([line]);
        Assert.Equal(expected, view.GetVisibleTextSnapshot().Contains("\u001b[31m", StringComparison.Ordinal));
        view.SetViewFilter(highlight);
        Assert.Equal(expected ? 1 : 0, view.CurrentVisibleLineCount);
    }
}
