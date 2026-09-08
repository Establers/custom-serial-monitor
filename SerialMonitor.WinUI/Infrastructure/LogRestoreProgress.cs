namespace SerialMonitor.WinUI.Infrastructure;

internal readonly record struct LogRestoreProgressSnapshot(double? Percent, TimeSpan Elapsed, TimeSpan? Remaining);

internal sealed class LogRestoreProgress(TimeProvider clock)
{
    private readonly long _started = clock.GetTimestamp();
    private long _workStarted;
    private long? _total;
    private long _completed;

    public void SetTotal(long total)
    {
        _total = Math.Max(0, total);
        _completed = 0;
        _workStarted = clock.GetTimestamp();
    }

    public void Advance(long parsedCharacters)
    {
        if (_total.HasValue)
            _completed += Math.Min(_total.Value - _completed, Math.Max(0, parsedCharacters));
    }

    public LogRestoreProgressSnapshot Snapshot()
    {
        var elapsed = clock.GetElapsedTime(_started);
        if (!_total.HasValue) return new(null, elapsed, null);
        if (_completed >= _total.Value) return new(100, elapsed, TimeSpan.Zero);
        var percent = 100.0 * _completed / _total.Value;
        var working = clock.GetElapsedTime(_workStarted);
        // Avoid a misleading ETA based on only the first tiny chunk.
        TimeSpan? remaining = percent >= 1 && working.TotalSeconds >= 1
            ? TimeSpan.FromSeconds(Math.Min(TimeSpan.MaxValue.TotalSeconds / 2,
                working.TotalSeconds * (_total.Value - _completed) / _completed))
            : null;
        return new(percent, elapsed, remaining);
    }
}
