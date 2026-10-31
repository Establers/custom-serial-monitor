using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class LogViewModel
{
    private readonly SemaphoreSlim _filterRebuildGate = new(1, 1);
    private CancellationTokenSource? _filterRebuildCancellation;
    private Queue<RetainedLogLine>? _pendingFilterLines;
    private long _pendingFilterBytes;
    private bool _filterRebuildOverflowed;
    private long _visibleRebuildVersion;

    // Start and await on the UI thread, just like AddRange. Only the detached
    // snapshot is formatted on a worker; publishing the result stays on the UI.
    // A superseding request or cancellation returns false without publishing.
    public async Task<bool> SetViewFiltersAsync(
        IEnumerable<HighlightRule> viewFilters,
        CancellationToken cancellationToken = default)
    {
        var compiled = viewFilters.Select(LogRuleMatcher.Compile).ToArray();
        CancelPendingViewFilter();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _filterRebuildCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            // Rapid changes share one worker and one bounded live catch-up queue.
            await _filterRebuildGate.WaitAsync(token);
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var version = _visibleRebuildVersion;
                    var rebuilt = CreateFilterSnapshot(compiled);
                    _pendingFilterLines = new Queue<RetainedLogLine>();
                    _pendingFilterBytes = 0;
                    _filterRebuildOverflowed = false;
                    // Let the caller paint its applying status before starting
                    // the expensive formatting pass, even for a small history.
                    await Task.Yield();
                    await Task.Run(() => rebuilt.RebuildVisibleLinesFromRetained(token), token);

                    while (version == _visibleRebuildVersion && !_filterRebuildOverflowed)
                    {
                        token.ThrowIfCancellationRequested();
                        var firstRetainedId = _retainedLines.TryPeek(out var first)
                            ? first.LineId
                            : _nextLineId + 1;
                        if (_pendingFilterLines.Count == 0)
                        {
                            // Prefix trimming was already completed by the worker
                            // for the most recent catch-up batch.
                            PublishFilteredView(rebuilt, compiled);
                            return true;
                        }

                        var pending = _pendingFilterLines.ToArray();
                        _pendingFilterLines.Clear();
                        _pendingFilterBytes = 0;
                        await Task.Run(() =>
                        {
                            rebuilt.AppendRebuiltLines(pending, token);
                            rebuilt.TrimFilteredPrefix(firstRetainedId, token);
                            rebuilt.TrimScreenMemoryAfterRebuild();
                        }, token);
                    }
                    // Clear, capacity/format changes or catch-up overflow require
                    // a fresh bounded snapshot. Never resurrect removed history.
                }
            }
            finally
            {
                _pendingFilterLines = null;
                _pendingFilterBytes = 0;
                _filterRebuildGate.Release();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (ReferenceEquals(_filterRebuildCancellation, cancellation))
            {
                _filterRebuildCancellation = null;
            }
        }
    }

    public void CancelPendingViewFilter() => _filterRebuildCancellation?.Cancel();

    private LogViewModel CreateFilterSnapshot(LogRuleMatcher.CompiledHighlightRule[] filters)
    {
        var snapshot = new LogViewModel(_capacity, _memoryPolicy)
        {
            _viewFilters = filters,
            _showTimestampInLogView = _showTimestampInLogView,
            _showRxTxDirectionPrefixInLogView = _showRxTxDirectionPrefixInLogView,
            _timestampDisplayFormat = _timestampDisplayFormat,
            _rxDisplayMode = _rxDisplayMode,
            _retainedMemoryBytes = _retainedMemoryBytes
        };
        snapshot._retainedLines.EnsureCapacity(_retainedLines.Count);
        foreach (var line in _retainedLines) snapshot._retainedLines.Enqueue(line);
        return snapshot;
    }

    private void TrackPendingFilterLine(RetainedLogLine line)
    {
        if (_pendingFilterLines is null || _filterRebuildOverflowed) return;
        var bytes = line.EstimatedBytes;
        if (_pendingFilterLines.Count >= _capacity || _pendingFilterBytes + bytes > _memoryPolicy.RetainedBytes)
        {
            _filterRebuildOverflowed = true;
            _pendingFilterLines.Clear();
            _pendingFilterBytes = 0;
            return;
        }
        _pendingFilterLines.Enqueue(line);
        _pendingFilterBytes += bytes;
    }

    private void TrimFilteredPrefix(long firstRetainedId, CancellationToken cancellationToken)
    {
        var partialPrefix = false;
        var visibleLinesToRemove = 0;
        while (_retainedLines.TryPeek(out var line) && line.LineId < firstRetainedId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _retainedLines.Dequeue();
            _retainedMemoryBytes -= line.EstimatedBytes;
            var contribution = _retainedVisibleLineContributions.Dequeue();
            visibleLinesToRemove += contribution > 0 ? 1 : 0;
            if (line.Line.IsPartialRxSegment) partialPrefix = true;
            else if (line.Line.IsPartialRxTerminator || contribution > 0) partialPrefix = false;
        }

        if (partialPrefix && _retainedVisibleLineContributions.TryPeek(out var next) && next != 1)
        {
            RebuildVisibleLinesFromRetained(cancellationToken);
            return;
        }
        for (var index = 0; index < visibleLinesToRemove; index++) RemoveFirstVisibleLine();
    }

    private void PublishFilteredView(LogViewModel rebuilt, LogRuleMatcher.CompiledHighlightRule[] filters)
    {
        // A newly visible projection can hit the character budget even when
        // its formerly hidden records fit the byte budget. Retain the same
        // bounded suffix as the worker before publishing its line identities.
        var firstVisibleHistoryId = rebuilt._retainedLines.TryPeek(out var first) ? first.LineId : _nextLineId + 1;
        while (_retainedLines.TryPeek(out var removed) && removed.LineId < firstVisibleHistoryId)
        {
            _retainedLines.Dequeue();
            _retainedMemoryBytes -= removed.EstimatedBytes;
            DroppedVisibleLineCount++;
        }
        _viewFilters = filters;
        // Highlight detection already consumed every live input, including
        // screen-only rejected records which the worker never received.
        _filterMatcher = rebuilt._filterMatcher.WithLatestContext(
            _filterMatcher, _highlightMatcher, filters, ActiveRuleMode);
        _visibleLines = rebuilt._visibleLines;
        _retainedVisibleLineContributions = rebuilt._retainedVisibleLineContributions;
        _lastVisibleLine = rebuilt._lastVisibleLine;
        _visibleCharacterCount = rebuilt._visibleCharacterCount;
        _partialRxVisualLineActive = rebuilt._partialRxVisualLineActive;
        _partialRxVisualLength = rebuilt._partialRxVisualLength;
        _partialRxDisplayBuilder = rebuilt._partialRxDisplayBuilder;
        _partialRxSearchableBuilder = rebuilt._partialRxSearchableBuilder;
        _partialRxLine = rebuilt._partialRxLine;
        _partialRxLineDirty = rebuilt._partialRxLineDirty;
        PublishTerminalHighlightState(rebuilt);
        XtermFormattingErrorCount += rebuilt.XtermFormattingErrorCount;
        ViewFilterMatchErrorCount += rebuilt.ViewFilterMatchErrorCount;
        _visibleRebuildVersion++;
        RaiseVisibleCountProperties();
        TextRebuilt?.Invoke(this, EventArgs.Empty);
    }
}
