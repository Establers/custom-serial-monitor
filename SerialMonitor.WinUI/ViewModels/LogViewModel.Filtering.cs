using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed partial class LogViewModel
{
    private readonly SemaphoreSlim _filterRebuildGate = new(1, 1);
    private CancellationTokenSource? _filterRebuildCancellation;
    private Queue<RetainedLogLine>? _pendingFilterLines;
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
                        await Task.Run(() =>
                        {
                            foreach (var line in pending)
                            {
                                token.ThrowIfCancellationRequested();
                                rebuilt._retainedLines.Enqueue(line);
                            }
                            rebuilt.AppendRebuiltLines(pending, token);
                            rebuilt.TrimFilteredPrefix(firstRetainedId, token);
                        }, token);
                    }
                    // Clear, capacity/format changes or catch-up overflow require
                    // a fresh bounded snapshot. Never resurrect removed history.
                }
            }
            finally
            {
                _pendingFilterLines = null;
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
        var snapshot = new LogViewModel(_capacity)
        {
            _viewFilters = filters,
            _showTimestampInLogView = _showTimestampInLogView,
            _showRxTxDirectionPrefixInLogView = _showRxTxDirectionPrefixInLogView,
            _timestampDisplayFormat = _timestampDisplayFormat,
            _rxDisplayMode = _rxDisplayMode
        };
        snapshot._retainedLines.EnsureCapacity(_retainedLines.Count);
        foreach (var line in _retainedLines) snapshot._retainedLines.Enqueue(line);
        return snapshot;
    }

    private void TrackPendingFilterLine(RetainedLogLine line)
    {
        if (_pendingFilterLines is null || _filterRebuildOverflowed) return;
        if (_pendingFilterLines.Count >= _capacity)
        {
            _filterRebuildOverflowed = true;
            _pendingFilterLines.Clear();
            return;
        }
        _pendingFilterLines.Enqueue(line);
    }

    private void TrimFilteredPrefix(long firstRetainedId, CancellationToken cancellationToken)
    {
        var partialPrefix = false;
        var visibleLinesToRemove = 0;
        while (_retainedLines.TryPeek(out var line) && line.LineId < firstRetainedId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _retainedLines.Dequeue();
            var contribution = _retainedVisibleLineContributions.Dequeue();
            visibleLinesToRemove += contribution;
            if (line.Line.IsPartialRxSegment) partialPrefix = true;
            else if (line.Line.IsPartialRxTerminator || contribution > 0) partialPrefix = false;
        }

        if (partialPrefix && _retainedVisibleLineContributions.TryPeek(out var next) && next == 0)
        {
            RebuildVisibleLinesFromRetained(cancellationToken);
            return;
        }
        for (var index = 0; index < visibleLinesToRemove; index++) RemoveFirstVisibleLine();
    }

    private void PublishFilteredView(LogViewModel rebuilt, LogRuleMatcher.CompiledHighlightRule[] filters)
    {
        _viewFilters = filters;
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
        XtermFormattingErrorCount += rebuilt.XtermFormattingErrorCount;
        ViewFilterMatchErrorCount += rebuilt.ViewFilterMatchErrorCount;
        _visibleRebuildVersion++;
        RaiseVisibleCountProperties();
        TextRebuilt?.Invoke(this, EventArgs.Empty);
    }
}
