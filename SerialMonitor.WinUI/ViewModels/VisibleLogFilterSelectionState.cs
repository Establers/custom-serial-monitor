namespace SerialMonitor.WinUI.ViewModels;

internal sealed class VisibleLogFilterSelectionState
{
    private readonly HashSet<VisibleLogFilterKey> _currentKeys = new();
    private readonly HashSet<VisibleLogFilterKey> _draftKeys = new();

    public int CurrentCount => _currentKeys.Count;

    public int DraftCount => _draftKeys.Count;

    public bool HasPendingChanges => !_currentKeys.SetEquals(_draftKeys);

    public bool IsCurrent(VisibleLogFilterKey key) => _currentKeys.Contains(key);

    public bool IsDraft(VisibleLogFilterKey key) => _draftKeys.Contains(key);

    public void BeginEdit()
    {
        CopyCurrentToDraft();
    }

    public void ReplaceDraft(IEnumerable<VisibleLogFilterKey> keys)
    {
        _draftKeys.Clear();
        _draftKeys.UnionWith(keys);
    }

    public void ApplyDraft()
    {
        _currentKeys.Clear();
        _currentKeys.UnionWith(_draftKeys);
    }

    public void CancelDraft()
    {
        CopyCurrentToDraft();
    }

    public bool RetainAvailable(
        IEnumerable<VisibleLogFilterKey> availableKeys,
        bool preserveSelection)
    {
        var nextKeys = preserveSelection
            ? new HashSet<VisibleLogFilterKey>(_currentKeys)
            : new HashSet<VisibleLogFilterKey>();
        nextKeys.IntersectWith(availableKeys);

        var changed = !_currentKeys.SetEquals(nextKeys);
        _currentKeys.Clear();
        _currentKeys.UnionWith(nextKeys);
        CopyCurrentToDraft();
        return changed;
    }

    private void CopyCurrentToDraft()
    {
        _draftKeys.Clear();
        _draftKeys.UnionWith(_currentKeys);
    }
}
