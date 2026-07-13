using System.Diagnostics;

namespace SerialMonitor.Core;

public readonly record struct IdleGapObservation(
    bool HasPreviousTimestamp,
    bool StartsNewGroup,
    long NormalizedTimestamp,
    TimeSpan ObservedGap);

public static class IdleGapBoundaryDetector
{
    public static IdleGapObservation Observe(
        long? previousTimestamp,
        long currentTimestamp,
        TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Idle timeout must be greater than zero.");
        }

        if (!previousTimestamp.HasValue)
        {
            return new IdleGapObservation(
                HasPreviousTimestamp: false,
                StartsNewGroup: false,
                NormalizedTimestamp: currentTimestamp,
                ObservedGap: TimeSpan.Zero);
        }

        var normalizedTimestamp = Math.Max(previousTimestamp.Value, currentTimestamp);
        var observedGap = Stopwatch.GetElapsedTime(previousTimestamp.Value, normalizedTimestamp);
        return new IdleGapObservation(
            HasPreviousTimestamp: true,
            StartsNewGroup: observedGap >= timeout,
            NormalizedTimestamp: normalizedTimestamp,
            ObservedGap: observedGap);
    }

    public static TimeSpan GetRemainingDelay(
        long? lastTimestamp,
        long nowTimestamp,
        TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Idle timeout must be greater than zero.");
        }

        if (!lastTimestamp.HasValue)
        {
            return timeout;
        }

        var normalizedNow = Math.Max(lastTimestamp.Value, nowTimestamp);
        return timeout - Stopwatch.GetElapsedTime(lastTimestamp.Value, normalizedNow);
    }
}
