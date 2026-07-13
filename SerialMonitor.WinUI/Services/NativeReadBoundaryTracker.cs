namespace SerialMonitor.WinUI.Services;

internal readonly record struct NativeReadBoundary(
    int ByteCount,
    long CompletedTimestamp,
    bool EndsAtNativeIdleBoundary,
    bool BoundarySuppressedByLineError);

internal sealed class NativeReadBoundaryTracker
{
    private readonly int _bufferCapacity;
    private long _producedByteCount;
    private long _consumedByteCount;

    public NativeReadBoundaryTracker(int bufferCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferCapacity);
        _bufferCapacity = bufferCapacity;
    }

    public NativeReadBoundary ObserveCompletion(
        int availableByteCount,
        long completedTimestamp,
        bool usesNativeIdleTimeout,
        bool lineErrorObserved = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(availableByteCount);
        if (availableByteCount > _bufferCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(availableByteCount),
                availableByteCount,
                "Available bytes cannot exceed the native read-buffer capacity.");
        }

        var outstandingByteCount = _producedByteCount - _consumedByteCount;
        if (outstandingByteCount < 0 || outstandingByteCount > _bufferCapacity)
        {
            throw new InvalidOperationException(
                $"Native RX outstanding byte count is invalid: {outstandingByteCount}.");
        }

        var completedByteCount = availableByteCount - outstandingByteCount;
        if (completedByteCount <= 0 || completedByteCount > _bufferCapacity)
        {
            throw new InvalidOperationException(
                $"Native RX boundary tracking lost synchronization: " +
                $"available={availableByteCount}, outstanding={outstandingByteCount}, " +
                $"completion={completedByteCount}.");
        }

        var readStart = (int)(_consumedByteCount % _bufferCapacity);
        var outstandingBytes = (int)outstandingByteCount;
        var contiguousWriteCapacity = readStart + outstandingBytes >= _bufferCapacity
            ? _bufferCapacity - outstandingBytes
            : _bufferCapacity - readStart - outstandingBytes;
        if (completedByteCount > contiguousWriteCapacity)
        {
            throw new InvalidOperationException(
                $"Native RX completion exceeded RJCP's contiguous write region: " +
                $"completion={completedByteCount}, capacity={contiguousWriteCapacity}, " +
                $"start={readStart}, outstanding={outstandingBytes}.");
        }

        _producedByteCount += completedByteCount;

        // Filling RJCP's entire contiguous write region completes ReadFile
        // without proving that an idle interval occurred. This covers both
        // the physical array end and a wrapped buffer filling up to its read
        // start. Shorter completions are the ones ended by the native timeout.
        // A driver line-status error can complete or disturb an in-flight
        // read. Such a short completion is not positive proof of an idle gap;
        // let the application timeout handle it conservatively.
        var endsAtNativeIdleBoundary = usesNativeIdleTimeout &&
            !lineErrorObserved &&
            completedByteCount < contiguousWriteCapacity;
        return new NativeReadBoundary(
            (int)completedByteCount,
            completedTimestamp,
            endsAtNativeIdleBoundary,
            BoundarySuppressedByLineError:
                lineErrorObserved && usesNativeIdleTimeout && completedByteCount < contiguousWriteCapacity);
    }

    public void RecordConsumed(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);
        var outstandingByteCount = _producedByteCount - _consumedByteCount;
        if (byteCount > outstandingByteCount)
        {
            throw new InvalidOperationException(
                $"Native RX consumed beyond tracked data: " +
                $"consume={byteCount}, outstanding={outstandingByteCount}.");
        }

        _consumedByteCount += byteCount;
    }
}
