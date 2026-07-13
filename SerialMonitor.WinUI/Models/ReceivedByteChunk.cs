using System.Diagnostics;

namespace SerialMonitor.WinUI.Models;

public sealed class ReceivedByteChunk
{
    public ReceivedByteChunk(byte[] bytes, long receivedTimestamp)
    {
        Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        ReceivedTimestamp = receivedTimestamp;
    }

    public byte[] Bytes { get; }

    public long ReceivedTimestamp { get; }

    public static ReceivedByteChunk Capture(byte[] bytes) =>
        new(bytes, Stopwatch.GetTimestamp());
}
