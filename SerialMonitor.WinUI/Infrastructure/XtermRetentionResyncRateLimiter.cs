using System.Diagnostics;

namespace SerialMonitor.WinUI.Infrastructure;

internal enum XtermRetentionResyncDisposition
{
    RunNow,
    Schedule,
    Coalesced,
    None
}

internal readonly record struct XtermRetentionResyncDecision(
    XtermRetentionResyncDisposition Disposition,
    TimeSpan Delay);

/// <summary>
/// Applies a monotonic completion-to-start cooldown only to retention-triggered
/// snapshots. Scheduling remains owned by the caller, so at most one timer is
/// needed and an active snapshot retains only one pending reconciliation bit.
/// </summary>
internal sealed class XtermRetentionResyncRateLimiter
{
    private readonly object _gate = new();
    private readonly TimeSpan _minimumInterval;
    private readonly Func<long> _getTimestamp;
    private readonly long _timestampFrequency;
    private bool _hasSnapshotCompletion;
    private long _lastSnapshotCompletion;
    private bool _snapshotInProgress;
    private bool _runRequestOutstanding;
    private bool _pendingDuringSnapshot;
    private bool _delayedRequestPending;

    public XtermRetentionResyncRateLimiter(TimeSpan minimumInterval)
        : this(minimumInterval, Stopwatch.GetTimestamp, Stopwatch.Frequency)
    {
    }

    internal XtermRetentionResyncRateLimiter(
        TimeSpan minimumInterval,
        Func<long> getTimestamp,
        long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minimumInterval, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(getTimestamp);
        ArgumentOutOfRangeException.ThrowIfLessThan(timestampFrequency, 1);

        _minimumInterval = minimumInterval;
        _getTimestamp = getTimestamp;
        _timestampFrequency = timestampFrequency;
    }

    public bool HasPendingRequest
    {
        get
        {
            lock (_gate)
            {
                return _runRequestOutstanding ||
                    _delayedRequestPending ||
                    _pendingDuringSnapshot;
            }
        }
    }

    public XtermRetentionResyncDecision Request()
    {
        lock (_gate)
        {
            if (_snapshotInProgress)
            {
                _pendingDuringSnapshot = true;
                return new(
                    XtermRetentionResyncDisposition.Coalesced,
                    TimeSpan.Zero);
            }

            if (_runRequestOutstanding || _delayedRequestPending)
            {
                return new(
                    XtermRetentionResyncDisposition.Coalesced,
                    TimeSpan.Zero);
            }

            var remaining = GetRemainingDelayLocked(_getTimestamp());
            if (remaining <= TimeSpan.Zero)
            {
                _runRequestOutstanding = true;
                return new(
                    XtermRetentionResyncDisposition.RunNow,
                    TimeSpan.Zero);
            }

            _delayedRequestPending = true;
            return new(XtermRetentionResyncDisposition.Schedule, remaining);
        }
    }

    public XtermRetentionResyncDecision ConsumeDelayedRequest()
    {
        lock (_gate)
        {
            if (!_delayedRequestPending)
            {
                return new(XtermRetentionResyncDisposition.None, TimeSpan.Zero);
            }

            if (_snapshotInProgress)
            {
                _pendingDuringSnapshot = true;
                _delayedRequestPending = false;
                return new(XtermRetentionResyncDisposition.None, TimeSpan.Zero);
            }

            var remaining = GetRemainingDelayLocked(_getTimestamp());
            if (remaining > TimeSpan.Zero)
            {
                return new(XtermRetentionResyncDisposition.Schedule, remaining);
            }

            _delayedRequestPending = false;
            _runRequestOutstanding = true;
            return new(XtermRetentionResyncDisposition.RunNow, TimeSpan.Zero);
        }
    }

    public void RecordSnapshotStarted()
    {
        lock (_gate)
        {
            _snapshotInProgress = true;
            _runRequestOutstanding = false;
            _delayedRequestPending = false;
            _pendingDuringSnapshot = false;
        }
    }

    public XtermRetentionResyncDecision RecordSnapshotCompleted()
    {
        lock (_gate)
        {
            if (!_snapshotInProgress)
            {
                return new(XtermRetentionResyncDisposition.None, TimeSpan.Zero);
            }

            _snapshotInProgress = false;
            _hasSnapshotCompletion = true;
            _lastSnapshotCompletion = _getTimestamp();
            if (!_pendingDuringSnapshot)
            {
                return new(XtermRetentionResyncDisposition.None, TimeSpan.Zero);
            }

            _pendingDuringSnapshot = false;
            _delayedRequestPending = true;
            return new(XtermRetentionResyncDisposition.Schedule, _minimumInterval);
        }
    }

    public bool CancelPendingRequest()
    {
        lock (_gate)
        {
            var wasPending = _delayedRequestPending;
            wasPending |= _runRequestOutstanding || _pendingDuringSnapshot;
            _delayedRequestPending = false;
            _runRequestOutstanding = false;
            _pendingDuringSnapshot = false;
            return wasPending;
        }
    }

    private TimeSpan GetRemainingDelayLocked(long now)
    {
        if (!_hasSnapshotCompletion)
        {
            return TimeSpan.Zero;
        }

        var elapsedTimestamp = now >= _lastSnapshotCompletion
            ? now - _lastSnapshotCompletion
            : long.MaxValue;
        var elapsedSeconds = (double)elapsedTimestamp / _timestampFrequency;
        var elapsed = elapsedSeconds >= TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.MaxValue
            : TimeSpan.FromSeconds(elapsedSeconds);
        return elapsed >= _minimumInterval
            ? TimeSpan.Zero
            : _minimumInterval - elapsed;
    }
}
