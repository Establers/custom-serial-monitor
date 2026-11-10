using System.Diagnostics;

namespace SerialMonitor.WinUI.Models;

public sealed class ReceivedByteChunk
{
    public ReceivedByteChunk(
        byte[] bytes,
        long receivedTimestamp,
        bool endsAtNativeIdleBoundary = false,
        long? streamOffset = null,
        DateTimeOffset? receivedAt = null)
    {
        Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        ReceivedTimestamp = receivedTimestamp;
        // Convert once so raw observers and the delayed log pipeline use the
        // exact same wall-clock observation time, including queued native RX.
        ReceivedAt = receivedAt ?? (receivedTimestamp > 0
            ? DateTimeOffset.Now - Stopwatch.GetElapsedTime(receivedTimestamp)
            : DateTimeOffset.Now);
        EndsAtNativeIdleBoundary = endsAtNativeIdleBoundary;
        StreamOffset = streamOffset;
    }

    public byte[] Bytes { get; }

    public long ReceivedTimestamp { get; }

    public DateTimeOffset ReceivedAt { get; }

    // Parser-only position in this transport session, including omitted bytes.
    // Raw capture/bridge observers receive the original chunk unchanged.
    public long? StreamOffset { get; }

    // True only when the transport has already waited for the configured
    // inter-byte idle timeout. LogPipeline can then close the HEX group
    // without applying the same timeout a second time.
    public bool EndsAtNativeIdleBoundary { get; }

    public static ReceivedByteChunk Capture(byte[] bytes) =>
        new(bytes, Stopwatch.GetTimestamp());

    public static ReceivedByteChunk CaptureAt(
        byte[] bytes,
        long receivedTimestamp,
        bool endsAtNativeIdleBoundary = false) =>
        new(bytes, receivedTimestamp, endsAtNativeIdleBoundary);
}
