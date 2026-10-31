using System.Buffers.Binary;

namespace SerialMonitor.Core.Sequences;

// v2 stores each chunk's offset, monotonic timestamp and connection generation
// and length once (24 bytes); wall time uses the shared anchor. Explicit lengths
// exclude raw bytes not yet committed to the index after a crash or during RX.
// Original chunk boundaries and 100 ns wall times survive, including reconnects.
internal static class SequenceRxTimeIndex
{
    public const string FileName = "rx-times-v2.idx";
    internal const int HeaderBytes = 32;
    internal const int EntryBytes = 24;

    public static byte[] Header(long timestamp, DateTimeOffset wallTime, long frequency)
    {
        var header = new byte[HeaderBytes];
        "SRX2"u8.CopyTo(header);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), timestamp);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), wallTime.UtcTicks);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), frequency);
        return header;
    }

    public static byte[] Entry(long offset, int length, long timestamp, long generation)
    {
        var entry = new byte[EntryBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(entry, checked((uint)offset)); // RX is capped at 1 GiB/run.
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(4), length);
        BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(8), timestamp);
        BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(16), generation);
        return entry;
    }

    public static async Task<IReadOnlyList<SequenceRxSlice>> ReadAsync(FileStream index, long start, byte[] bytes, CancellationToken token)
    {
        var header = new byte[HeaderBytes];
        await index.ReadExactlyAsync(header, token).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual("SRX2"u8)) throw new InvalidDataException("Invalid RX time index header.");
        var anchor = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
        var wallTicks = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
        var frequency = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24));
        if (frequency <= 0) throw new InvalidDataException("Invalid RX clock frequency.");
        var count = (index.Length - HeaderBytes) / EntryBytes;
        var entry = new byte[EntryBytes];
        long low = 0, high = count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            index.Position = HeaderBytes + middle * EntryBytes;
            await index.ReadExactlyAsync(entry, token).ConfigureAwait(false);
            var offset = (long)BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var length = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(4));
            if (offset + length <= start) low = middle + 1; else high = middle;
        }
        var current = low;
        if (current >= count) return [];
        index.Position = HeaderBytes + current * EntryBytes;
        var rows = new List<SequenceRxSlice>();
        var end = start + bytes.Length;
        while (current < count && rows.Count < SequenceRunStore.PageSize)
        {
            await index.ReadExactlyAsync(entry, token).ConfigureAwait(false);
            var offset = (long)BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var length = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(4));
            if (offset >= end) break;
            if (length <= 0) throw new InvalidDataException("Invalid RX chunk length.");
            var from = Math.Max(start, offset);
            var to = Math.Min(end, offset + length);
            if (from < to)
            {
                var timestamp = BinaryPrimitives.ReadInt64LittleEndian(entry.AsSpan(8));
                // Same conversion as Stopwatch.GetElapsedTime, using the writer's
                // frequency so recordings remain portable between computers.
                var ticks = wallTicks + (long)((timestamp - anchor) * ((double)TimeSpan.TicksPerSecond / frequency));
                rows.Add(new(from, bytes.AsSpan((int)(from - start), (int)(to - from)).ToArray(),
                    new DateTimeOffset(ticks, TimeSpan.Zero), timestamp,
                    BinaryPrimitives.ReadInt64LittleEndian(entry.AsSpan(16))));
            }
            current++;
        }
        return rows;
    }
}
