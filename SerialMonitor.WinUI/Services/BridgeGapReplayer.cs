using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Services;

internal interface IBridgeClock
{
    long Frequency { get; }

    long GetTimestamp();

    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemBridgeClock : IBridgeClock
{
    public long Frequency => System.Diagnostics.Stopwatch.Frequency;

    public long GetTimestamp() => System.Diagnostics.Stopwatch.GetTimestamp();

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        new(Task.Delay(delay, cancellationToken));
}

internal readonly record struct BridgeReplayTiming(long TargetTimestamp, double LatenessMilliseconds);

internal sealed class BridgeGapReplayer
{
    private readonly IBridgeClock _clock;
    private bool _hasFirstChunk;
    private long _firstSourceTimestamp;
    private long _firstActualWriteTimestamp;
    private long _previousActualWriteTimestamp;
    private bool _previousEndsAtIdleBoundary;
    private int _previousIdleTimeoutMs;

    public BridgeGapReplayer(IBridgeClock clock)
    {
        _clock = clock;
    }

    public async ValueTask<BridgeReplayTiming> WaitUntilDueAsync(
        BridgeRxChunk chunk,
        CancellationToken cancellationToken)
    {
        if (!_hasFirstChunk)
        {
            var now = _clock.GetTimestamp();
            _hasFirstChunk = true;
            _firstSourceTimestamp = chunk.ReceivedTimestamp;
            _firstActualWriteTimestamp = now;
            return new BridgeReplayTiming(now, 0);
        }

        var sourceDelta = Math.Max(0, chunk.ReceivedTimestamp - _firstSourceTimestamp);
        var target = _firstActualWriteTimestamp + sourceDelta;
        if (_previousEndsAtIdleBoundary && _previousIdleTimeoutMs > 0)
        {
            var minimumBoundaryTarget = _previousActualWriteTimestamp +
                MillisecondsToTicks(_previousIdleTimeoutMs);
            target = Math.Max(target, minimumBoundaryTarget);
        }

        var remainingTicks = target - _clock.GetTimestamp();
        if (remainingTicks > 0)
        {
            await _clock.DelayAsync(TicksToTimeSpan(remainingTicks), cancellationToken);
        }

        var latenessTicks = Math.Max(0, _clock.GetTimestamp() - target);
        return new BridgeReplayTiming(target, TicksToMilliseconds(latenessTicks));
    }

    public void RecordWriteCompleted(BridgeRxChunk chunk, long actualWriteTimestamp)
    {
        _previousActualWriteTimestamp = actualWriteTimestamp;
        _previousEndsAtIdleBoundary = chunk.EndsAtNativeIdleBoundary;
        _previousIdleTimeoutMs = Math.Max(0, chunk.AppliedIdleTimeoutMs);
    }

    private long MillisecondsToTicks(double milliseconds) =>
        (long)Math.Ceiling(milliseconds * _clock.Frequency / 1000d);

    private double TicksToMilliseconds(long ticks) => ticks * 1000d / _clock.Frequency;

    private TimeSpan TicksToTimeSpan(long ticks) => TimeSpan.FromSeconds((double)ticks / _clock.Frequency);
}
