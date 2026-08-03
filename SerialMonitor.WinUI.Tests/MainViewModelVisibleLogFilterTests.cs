using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class MainViewModelVisibleLogFilterTests
{
    [Fact]
    public void BuildViewFilterKey_CaseSensitiveRules_PreserveKeywordCase()
    {
        var upper = CreateRule("ERROR", caseSensitive: true);
        var lower = CreateRule("error", caseSensitive: true);

        var upperKey = MainViewModel.BuildViewFilterKey(upper);
        var lowerKey = MainViewModel.BuildViewFilterKey(lower);

        Assert.NotEqual(upperKey, lowerKey);
    }

    [Fact]
    public void BuildViewFilterKey_CaseInsensitiveRules_NormalizeKeywordCase()
    {
        var upper = CreateRule("ERROR", caseSensitive: false);
        var lower = CreateRule("error", caseSensitive: false);

        Assert.Equal(
            MainViewModel.BuildViewFilterKey(upper),
            MainViewModel.BuildViewFilterKey(lower));
    }

    [Fact]
    public void BuildViewFilterKey_SeparatorCharacters_DoNotCollapseFieldBoundaries()
    {
        var separatorInName = CreateRule("C", caseSensitive: true);
        separatorInName.Name = "A|B";
        var separatorInKeyword = CreateRule("B|C", caseSensitive: true);
        separatorInKeyword.Name = "A";

        Assert.NotEqual(
            MainViewModel.BuildViewFilterKey(separatorInName),
            MainViewModel.BuildViewFilterKey(separatorInKeyword));
    }

    [Fact]
    public void SelectionState_CancelDiscardsDraft_AndApplyCommitsIt()
    {
        var state = new VisibleLogFilterSelectionState();
        var errorKey = CreateKey("ERROR");
        var warnKey = CreateKey("WARN");

        state.BeginEdit();
        state.ReplaceDraft([errorKey, warnKey]);
        Assert.True(state.HasPendingChanges);

        state.CancelDraft();
        Assert.False(state.HasPendingChanges);
        Assert.Equal(0, state.CurrentCount);
        Assert.Equal(0, state.DraftCount);

        state.ReplaceDraft([errorKey, warnKey]);
        state.ApplyDraft();
        Assert.False(state.HasPendingChanges);
        Assert.True(state.IsCurrent(errorKey));
        Assert.True(state.IsCurrent(warnKey));

        state.BeginEdit();
        state.ReplaceDraft([warnKey]);
        Assert.True(state.HasPendingChanges);
        state.ApplyDraft();

        Assert.False(state.HasPendingChanges);
        Assert.False(state.IsCurrent(errorKey));
        Assert.True(state.IsCurrent(warnKey));
    }

    [Fact]
    public void SelectionState_RetainAvailable_RemovesUnavailableSelections()
    {
        var state = new VisibleLogFilterSelectionState();
        var errorKey = CreateKey("ERROR");
        var warnKey = CreateKey("WARN");
        var faultKey = CreateKey("FAULT");
        state.ReplaceDraft([errorKey, warnKey]);
        state.ApplyDraft();

        var changed = state.RetainAvailable([warnKey, faultKey], preserveSelection: true);

        Assert.True(changed);
        Assert.False(state.IsCurrent(errorKey));
        Assert.True(state.IsCurrent(warnKey));
        Assert.False(state.HasPendingChanges);
    }

    private static VisibleLogFilterKey CreateKey(string keyword) =>
        MainViewModel.BuildViewFilterKey(CreateRule(keyword, caseSensitive: true));

    private static LogRule CreateRule(string keyword, bool caseSensitive) => new()
    {
        Name = keyword,
        Keyword = keyword,
        Enabled = true,
        UseAsViewFilter = true,
        CaseSensitive = caseSensitive,
        Mode = LogRuleMatchMode.Terminal,
        MatchDirection = HighlightMatchDirection.Both
    };
}
