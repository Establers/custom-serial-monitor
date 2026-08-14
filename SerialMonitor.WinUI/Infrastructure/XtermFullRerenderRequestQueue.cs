namespace SerialMonitor.WinUI.Infrastructure;

internal enum XtermRerenderRequestDisposition
{
    Schedule,
    Coalesced
}

internal readonly record struct XtermFullRerenderRequest(
    string Reason,
    bool IsRestoreRender);

/// <summary>
/// Coalesces full-render requests into one queued run and at most one trailing
/// run while a snapshot replacement is already in progress.
/// </summary>
internal sealed class XtermFullRerenderRequestQueue
{
    private readonly object _gate = new();
    private bool _queued;
    private bool _running;
    private bool _requestedWhileRunning;
    private bool _isRestoreRender;
    private string _reason = "full re-render";

    public bool HasQueuedRequestReady
    {
        get
        {
            lock (_gate)
            {
                return _queued && !_running;
            }
        }
    }

    public XtermRerenderRequestDisposition Request(string reason, bool isRestoreRender)
    {
        var normalizedReason = NormalizeReason(reason);
        lock (_gate)
        {
            if (_running)
            {
                _requestedWhileRunning = true;
                _isRestoreRender |= isRestoreRender;
                _reason = MergeReason(_reason, normalizedReason);
                return XtermRerenderRequestDisposition.Coalesced;
            }

            if (_queued)
            {
                _isRestoreRender |= isRestoreRender;
                _reason = MergeReason(_reason, normalizedReason);
                return XtermRerenderRequestDisposition.Coalesced;
            }

            _queued = true;
            _isRestoreRender = isRestoreRender;
            _reason = normalizedReason;
            return XtermRerenderRequestDisposition.Schedule;
        }
    }

    public bool TryStart(out XtermFullRerenderRequest request)
    {
        lock (_gate)
        {
            if (!_queued || _running)
            {
                request = default;
                return false;
            }

            _queued = false;
            _running = true;
            request = new XtermFullRerenderRequest(_reason, _isRestoreRender);
            _reason = "full re-render";
            _isRestoreRender = false;
            return true;
        }
    }

    public bool Complete()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return false;
            }

            _running = false;
            if (!_requestedWhileRunning)
            {
                return false;
            }

            _requestedWhileRunning = false;
            _queued = true;
            return true;
        }
    }

    private static string NormalizeReason(string? reason)
    {
        return string.IsNullOrWhiteSpace(reason)
            ? "full re-render"
            : reason.Trim();
    }

    private static string MergeReason(string existingReason, string nextReason)
    {
        var existing = NormalizeReason(existingReason);
        var next = NormalizeReason(nextReason);
        if (string.Equals(existing, next, StringComparison.Ordinal))
        {
            return existing;
        }

        if (string.Equals(existing, "full re-render", StringComparison.Ordinal) &&
            !string.Equals(next, "full re-render", StringComparison.Ordinal))
        {
            return next;
        }

        if (existing.Contains(next, StringComparison.Ordinal))
        {
            return existing;
        }

        var merged = $"{existing}; {next}";
        return merged.Length <= 160 ? merged : merged[^160..];
    }
}
