namespace SerialMonitor.Core.Protocols;

public readonly record struct ProtocolInputTiming(long ReceivedTimestamp = 0, bool EndsAtNativeIdleBoundary = false,
    int AppliedIdleTimeoutMs = 0, double MinimumFrameGapMs = 0)
{
    public bool HasQualifiedBoundary => EndsAtNativeIdleBoundary && MinimumFrameGapMs > 0 && AppliedIdleTimeoutMs >= MinimumFrameGapMs;
    public static double FrameGapMilliseconds(int baud, double bitsPerCharacter) =>
        baud <= 0 || bitsPerCharacter <= 0 ? 0 : baud > 19200 ? 1.75 : 3500 * bitsPerCharacter / baud;
}

public sealed record ProtocolAnalysisStatistics(long Rx, long Tx, long CrcValid, long CrcErrors,
    long Incomplete, long Undecoded, long Exceptions, DateTimeOffset StartedAt, DateTimeOffset? LastCrcValidAt);

public sealed record ProtocolAnalysisSnapshot(long Version, bool Enabled, int RetainedCount, int HistoryCapacity, long RetainedBytes,
    long TotalFrames, long EvictedFrames, long DroppedChunks, long DroppedBytes, long PendingBytes,
    string? LastError, IReadOnlyList<ModbusRtuFrame>? Frames, ProtocolAnalysisStatistics Statistics);

public interface IProtocolAnalyzer : IAsyncDisposable
{
    event EventHandler<string>? Error;
    bool Enabled { get; }
    void SetEnabled(bool enabled);
    bool TryEnqueue(ReadOnlyMemory<byte> bytes, ProtocolDirection direction, long session, DateTimeOffset timestamp,
        ProtocolInputTiming timing = default);
    ProtocolAnalysisSnapshot Snapshot(long knownVersion = -1);
    void Clear();
    void ResetStatistics();
}
