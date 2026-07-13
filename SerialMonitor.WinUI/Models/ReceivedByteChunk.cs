using System.Diagnostics;

namespace SerialMonitor.WinUI.Models;

public sealed class ReceivedByteChunk
{
    public ReceivedByteChunk(
        byte[] bytes,
        long receivedTimestamp,
        bool endsAtNativeIdleBoundary = false)
    {
        Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        ReceivedTimestamp = receivedTimestamp;
        EndsAtNativeIdleBoundary = endsAtNativeIdleBoundary;
    }

    public byte[] Bytes { get; }

    public long ReceivedTimestamp { get; }

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
